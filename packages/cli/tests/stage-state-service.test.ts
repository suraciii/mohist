import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { WorkflowRunRepo } from '../src/db/workflow-run-repo';
import { Stage } from '../src/types';
import { StageStateService, normalizeCheckStatus, normalizeTaskStatus } from '../src/services/stage-state-service';
import { createWorkflowDefinitionSnapshot, WorkflowRun } from '../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../src/workflow/builtins/workflows/mohist-default';

describe('StageStateService', () => {
  let db: DatabaseManager;
  let service: StageStateService;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;

    service = new StageStateService(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('ensureStage', () => {
    it('should create a stage state row with pending status', () => {
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state).not.toBeNull();
      expect(state!.stage).toBe(Stage.Plan);
      expect(state!.status).toBe('running');
      expect(state!.attempts).toBe(0);
      expect(state!.startedAt).toBeTruthy();
    });

    it('should seed static tasks for plan stage', () => {
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.tasks.length).toBe(0);
    });

    it('should seed static tasks for check stage', () => {
      service.ensureStage(issueId, Stage.Check);

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.tasks.length).toBe(0);
    });

    it('should not seed static tasks for integrate stage', () => {
      service.ensureStage(issueId, Stage.Integrate);

      const state = service.getStageState(issueId, Stage.Integrate);
      expect(state!.tasks.length).toBe(0);
    });

    it('should not seed tasks for build stage (dynamic tasks)', () => {
      service.ensureStage(issueId, Stage.Build);

      const state = service.getStageState(issueId, Stage.Build);
      expect(state!.tasks.length).toBe(0);
    });

    it('should increment attempts on repeated calls', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.attempts).toBe(1);
      expect(state!.status).toBe('running');
    });

    it('should not re-seed static tasks on repeated calls', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.tasks.length).toBe(0);
    });
  });

  describe('upsertTask', () => {
    it('should insert a new task', () => {
      service.ensureStage(issueId, Stage.Build);
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'T-001',
        title: 'Compile code',
        status: 'completed',
        source: 'dynamic',
        order: 1,
      });

      const state = service.getStageState(issueId, Stage.Build);
      const task = state!.tasks.find(t => t.taskId === 'T-001');
      expect(task).toBeDefined();
      expect(task!.taskId).toBe('T-001');
      expect(task!.status).toBe('completed');
      expect(task!.source).toBe('dynamic');
    });

    it('should update existing task in place', () => {
      service.ensureStage(issueId, Stage.Plan);

      service.upsertTask(issueId, Stage.Plan, {
        taskId: 'proposal',
        title: 'Write proposal',
        status: 'running',
      });

      const state1 = service.getStageState(issueId, Stage.Plan);
      expect(state1!.tasks.find(t => t.taskId === 'proposal')!.status).toBe('running');

      service.upsertTask(issueId, Stage.Plan, {
        taskId: 'proposal',
        title: 'Write proposal',
        status: 'completed',
        attempts: 1,
        duration: 5000,
      });

      const state2 = service.getStageState(issueId, Stage.Plan);
      const task = state2!.tasks.find(t => t.taskId === 'proposal');
      expect(task!.status).toBe('completed');
      expect(task!.attempts).toBe(1);
      expect(task!.duration).toBe(5000);
    });

    it('should store artifacts and output', () => {
      service.ensureStage(issueId, Stage.Build);
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'T-001',
        title: 'Compile',
        status: 'completed',
        artifacts: ['dist/main.js', 'dist/vendor.js'],
        output: { lines: 42 },
      });

      const state = service.getStageState(issueId, Stage.Build);
      const task = state!.tasks.find(t => t.taskId === 'T-001');
      expect(task!.artifacts).toEqual(['dist/main.js', 'dist/vendor.js']);
      expect(task!.output).toEqual({ lines: 42 });
    });

    it('should create stage row automatically if missing', () => {
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'T-001',
        title: 'Compile',
        status: 'running',
      });

      const state = service.getStageState(issueId, Stage.Build);
      expect(state).not.toBeNull();
      const task = state!.tasks.find(t => t.taskId === 'T-001');
      expect(task).toBeDefined();
    });

    it('should support dynamic fix tasks', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertTask(issueId, Stage.Check, {
        taskId: 'fix-check-health',
        title: 'Fix check health issues',
        status: 'completed',
        source: 'dynamic',
        order: 100,
      });

      const state = service.getStageState(issueId, Stage.Check);
      const fixTask = state!.tasks.find(t => t.taskId === 'fix-check-health');
      expect(fixTask).toBeDefined();
      expect(fixTask!.source).toBe('dynamic');
    });
  });

  describe('upsertCheck', () => {
    it('should insert a new check', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        message: 'All tests passed',
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks.length).toBe(1);
      expect(state!.checks[0].checkName).toBe('build-test');
      expect(state!.checks[0].status).toBe('passed');
      expect(state!.checks[0].message).toBe('All tests passed');
    });

    it('should update existing check in place', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'failed',
        message: 'Test failed',
      });

      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        message: 'All tests passed',
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks.length).toBe(1);
      expect(state!.checks[0].status).toBe('passed');
      expect(state!.checks[0].runCount).toBe(2);
    });

    it('should accept explicit runCount', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        runCount: 3,
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks[0].runCount).toBe(3);
    });

    it('should store output', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'ai-review',
        status: 'passed',
        output: { verdict: 'PASS', reviewReport: 'LGTM' },
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks[0].output).toEqual({ verdict: 'PASS', reviewReport: 'LGTM' });
    });
  });

  describe('setApproval', () => {
    it('should set approval state on a stage', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.setApproval(issueId, Stage.Plan, {
        status: 'awaiting',
        output: { result: 'PASS' },
        requestedAt: '2024-01-01T00:00:00Z',
      });

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.approval).not.toBeNull();
      expect(state!.approval!.status).toBe('awaiting');
      expect(state!.approval!.output).toEqual({ result: 'PASS' });
    });

    it('should update approval state', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.setApproval(issueId, Stage.Plan, {
        status: 'awaiting',
        requestedAt: '2024-01-01T00:00:00Z',
      });
      service.setApproval(issueId, Stage.Plan, {
        status: 'approved',
        respondedAt: '2024-01-01T01:00:00Z',
      });

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.approval!.status).toBe('approved');
      expect(state!.approval!.respondedAt).toBe('2024-01-01T01:00:00Z');
    });
  });

  describe('getIssueStageState', () => {
    it('should return empty array when no stages exist', () => {
      const states = service.getIssueStageState(issueId);
      expect(states).toEqual([]);
    });

    it('should return all stages for an issue', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.ensureStage(issueId, Stage.Check);

      const states = service.getIssueStageState(issueId);
      expect(states.length).toBe(2);

      const stages = states.map(s => s.stage);
      expect(stages).toContain(Stage.Plan);
      expect(stages).toContain(Stage.Check);
    });

    it('should include tasks and checks for each stage', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);
      expect(checkState).toBeDefined();
      expect(checkState!.tasks.length).toBe(0);
      expect(checkState!.checks.length).toBe(1);
    });
  });

  describe('getStageState', () => {
    it('should return null for non-existent stage', () => {
      const state = service.getStageState(issueId, Stage.Plan);
      expect(state).toBeNull();
    });
  });

  describe('normalizeCheckStatus', () => {
    it('should normalize pass to passed', () => {
      expect(normalizeCheckStatus('pass')).toBe('passed');
    });

    it('should normalize passed to passed', () => {
      expect(normalizeCheckStatus('passed')).toBe('passed');
    });

    it('should normalize fail to failed', () => {
      expect(normalizeCheckStatus('fail')).toBe('failed');
    });

    it('should normalize error to error', () => {
      expect(normalizeCheckStatus('error')).toBe('error');
    });

    it('should normalize unknown to pending', () => {
      expect(normalizeCheckStatus('unknown')).toBe('pending');
    });

    it('should keep running as running', () => {
      expect(normalizeCheckStatus('running')).toBe('running');
    });
  });

  describe('normalizeTaskStatus', () => {
    it('should normalize completed to completed', () => {
      expect(normalizeTaskStatus('completed')).toBe('completed');
    });

    it('should normalize failed to failed', () => {
      expect(normalizeTaskStatus('failed')).toBe('failed');
    });

    it('should normalize unknown to pending', () => {
      expect(normalizeTaskStatus('unknown')).toBe('pending');
    });

    it('should normalize skipped to skipped', () => {
      expect(normalizeTaskStatus('skipped')).toBe('skipped');
    });
  });

  describe('row-based task projection', () => {
    it('returns all recorded tasks instead of filtering by built-in task ids', () => {
      service.ensureStage(issueId, Stage.Plan);

      const now = new Date().toISOString();
      service.db.run(
        `INSERT INTO stage_tasks
         (issue_id, stage, task_id, title, status, source, task_order, attempts, duration, artifacts, output, started_at, completed_at, updated_at)
         VALUES (?, ?, ?, ?, 'pending', 'static', ?, 0, 0, '[]', NULL, NULL, NULL, ?)`,
        [issueId, Stage.Plan, 'read-context', 'Read context files', 1, now],
      );
      service.db.run(
        `INSERT INTO stage_tasks
         (issue_id, stage, task_id, title, status, source, task_order, attempts, duration, artifacts, output, started_at, completed_at, updated_at)
         VALUES (?, ?, ?, ?, 'pending', 'static', ?, 0, 0, '[]', NULL, NULL, NULL, ?)`,
        [issueId, Stage.Plan, 'design-solution', 'Design solution', 2, now],
      );

      service.upsertTask(issueId, Stage.Plan, {
        taskId: 'proposal',
        title: 'Write proposal',
        status: 'completed',
        source: 'dynamic',
        order: 10,
        attempts: 1,
        duration: 5000,
      });
      service.upsertTask(issueId, Stage.Plan, {
        taskId: 'specs',
        title: 'Write specs',
        status: 'completed',
        source: 'dynamic',
        order: 11,
        attempts: 1,
        duration: 8000,
      });
      service.upsertTask(issueId, Stage.Plan, {
        taskId: 'self-review',
        title: 'Self-review plan',
        status: 'pending',
        source: 'dynamic',
        order: 12,
      });

      const state = service.getStageState(issueId, Stage.Plan);
      const taskIds = state!.tasks.map(t => t.taskId);

      expect(taskIds).toContain('read-context');
      expect(taskIds).toContain('design-solution');
      expect(taskIds).toContain('proposal');
      expect(taskIds).toContain('specs');
      expect(taskIds).toContain('self-review');
      expect(taskIds).toHaveLength(5);
    });

    it('adds reason and causedBy metadata to a runtime-added repair task', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertTask(issueId, Stage.Check, {
        taskId: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'completed',
        source: 'dynamic',
        order: 100,
        attempts: 1,
        duration: 15000,
      });

      const state = service.getStageState(issueId, Stage.Check);
      const fixTask = state!.tasks.find(t => t.taskId === 'fix-review-findings');

      expect(fixTask).toBeDefined();
      expect(fixTask!.status).toBe('completed');
      expect(fixTask!.reason).toBe('Added after review passed failed');
      expect(fixTask!.causedBy).toEqual({
        type: 'check-failure',
        checkName: 'ai-review',
        taskId: undefined,
        message: undefined,
      });
    });

    it('adds reason metadata to a rebase-branch task', () => {
      service.ensureStage(issueId, Stage.Integrate);
      service.upsertTask(issueId, Stage.Integrate, {
        taskId: 'rebase-branch',
        title: 'Rebase branch',
        status: 'completed',
        source: 'dynamic',
        order: 50,
        attempts: 1,
        duration: 30000,
      });

      const state = service.getStageState(issueId, Stage.Integrate);
      const rebaseTask = state!.tasks.find(t => t.taskId === 'rebase-branch');

      expect(rebaseTask).toBeDefined();
      expect(rebaseTask!.reason).toBe('Added because target branch moved');
      expect(rebaseTask!.causedBy).toEqual({
        type: 'rebase',
        checkName: undefined,
        taskId: undefined,
        message: undefined,
      });
    });

    it('returns Build stage tasks from tasks.json pattern (T-N IDs) plus repair tasks', () => {
      service.ensureStage(issueId, Stage.Build);
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'T-001',
        title: 'Add persistence',
        status: 'completed',
        source: 'dynamic',
        order: 1,
        attempts: 1,
        duration: 5000,
      });
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'T-002',
        title: 'Expose API',
        status: 'failed',
        source: 'dynamic',
        order: 2,
        attempts: 2,
        duration: 3000,
        output: { error: 'TypeScript build failed' },
      });
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'pending',
        source: 'dynamic',
        order: 100,
      });

      const state = service.getStageState(issueId, Stage.Build);
      const taskIds = state!.tasks.map(t => t.taskId);

      expect(taskIds).toContain('T-001');
      expect(taskIds).toContain('T-002');
      expect(taskIds).toContain('fix-build-health');
      expect(taskIds).toHaveLength(3);
    });
  });

  describe('check repair projection', () => {
    it('does not infer checkRepair from row state without workflow definition', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: 'Review found issues',
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);

      expect(checkState?.checkRepair).toBeUndefined();
    });

    it('keeps repair task and failed check visible without fabricating repair projection', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertTask(issueId, Stage.Check, {
        taskId: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'completed',
        attempts: 1,
        duration: 15000,
      });
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: 'Review still has unresolved issues',
        output: { verdict: 'FAIL', summary: '2 issues remain' },
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);

      expect(checkState?.checkRepair).toBeUndefined();
      expect(checkState?.tasks.map(task => task.taskId)).toContain('fix-review-findings');
      expect(checkState?.checks.find(check => check.checkName === 'review-passed')?.status).toBe('failed');
    });

    it('does not infer running repair state without workflow definition', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertTask(issueId, Stage.Check, {
        taskId: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'running',
        attempts: 1,
      });
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: 'Initial review failed',
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);

      expect(checkState?.checkRepair).toBeUndefined();
      expect(checkState?.tasks.find(task => task.taskId === 'fix-review-findings')?.status).toBe('running');
    });

    it('does not project checkRepair when no repair evidence exists and review-passed is pending', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'review-passed',
        status: 'pending',
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);

      expect(checkState?.checkRepair).toBeUndefined();
    });

    it('does not infer not-needed repair state without workflow definition', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'review-passed',
        status: 'passed',
        message: 'All good',
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);

      expect(checkState?.checkRepair).toBeUndefined();
    });

    it('keeps failed check output available without checkRepair projection', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        output: { verdict: 'FAIL', summary: 'Primitive tool_call_update.output missing metadata' },
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);

      expect(checkState?.checkRepair).toBeUndefined();
      expect(checkState?.checks[0].output).toEqual({ verdict: 'FAIL', summary: 'Primitive tool_call_update.output missing metadata' });
    });

    it('projects checkRepair from custom retry policy outside Check', () => {
      const snapshot = createWorkflowDefinitionSnapshot({
        definition: {
          id: 'custom-plan-review-state',
          stages: DEFAULT_STAGE_DEFINITIONS.map(definition => definition.stage === Stage.Plan
            ? {
              stage: Stage.Plan,
              tasks: [{ id: 'plan-review', title: 'Plan review', uses: 'mohist/agent' }],
              checks: [
                {
                  name: 'verify-plan',
                  title: 'Verify plan',
                  uses: 'mohist/health-gate',
                },
                {
                  name: 'plan-verdict',
                  title: 'Plan verdict',
                  uses: 'mohist/verdict',
                  onFailure: {
                    retry: {
                      limit: 2,
                      task: { id: 'fix-plan-verdict', title: 'Fix plan verdict', uses: 'mohist/agent' },
                    },
                  },
                },
                {
                  name: 'plan-candidate',
                  title: 'Plan candidate',
                  uses: 'mohist/merge-ready',
                },
              ],
              requiresApproval: true,
            }
            : definition),
        },
      });
      const { run } = WorkflowRun.startWorkflow({
        id: 'wr_custom_plan_repair_state',
        issueId,
        issueNumber: 1,
        workflowDefinitionSnapshot: snapshot,
      });
      run.completeTask(Stage.Plan, 'plan-review', { status: 'completed' });
      run.recordCheckResult(Stage.Plan, {
        name: 'verify-plan',
        status: 'pass',
        output: { headSha: 'plan-sha' },
      });
      run.recordCheckResult(Stage.Plan, {
        name: 'plan-verdict',
        status: 'fail',
        message: 'Plan review failed',
        output: { verdict: 'FAIL', summary: 'Plan has unresolved design concern' },
      });
      run.completeTask(Stage.Plan, 'fix-plan-verdict', { status: 'completed', output: { repairedItemIds: ['P-1'] } });
      new WorkflowRunRepo(db).saveAggregate(run, 'test');

      const latest = new WorkflowRunRepo(db).getLatestRunWithRelations(issueId)!;
      const planState = service.getIssueStageStateFromWorkflowRun(latest).find(stage => stage.stage === Stage.Plan)!;

      expect(planState.checkRepair).toMatchObject({
        checkName: 'plan-verdict',
        retryTaskId: 'fix-plan-verdict',
        status: 'completed',
        attemptsUsed: 1,
        attemptsMax: 2,
        attemptsRemaining: 1,
        followUpReviewStatus: 'pending',
      });
      expect(planState.convergence).toMatchObject({
        failedCheck: undefined,
        directlyRepairedCount: 1,
        reactionAttempts: 1,
      });
    });
  });

  describe('stage retry scenario', () => {
    it('should update current task state across retries', () => {
      service.ensureStage(issueId, Stage.Build);

      service.upsertTask(issueId, Stage.Build, {
        taskId: 'T-999',
        title: 'Compile',
        status: 'failed',
        attempts: 1,
      });

      const state1 = service.getStageState(issueId, Stage.Build);
      const compileTask = state1!.tasks.find(t => t.taskId === 'T-999');
      expect(compileTask).toBeDefined();
      expect(compileTask!.status).toBe('failed');
      expect(compileTask!.attempts).toBe(1);

      service.ensureStage(issueId, Stage.Build);

      service.upsertTask(issueId, Stage.Build, {
        taskId: 'T-999',
        title: 'Compile',
        status: 'completed',
        attempts: 2,
      });

      const state2 = service.getStageState(issueId, Stage.Build);
      const compileTask2 = state2!.tasks.find(t => t.taskId === 'T-999');
      expect(compileTask2).toBeDefined();
      expect(compileTask2!.status).toBe('completed');
      expect(compileTask2!.attempts).toBe(2);
      expect(state2!.attempts).toBe(1);
    });

    it('should update check across fix-recheck cycles', () => {
      service.ensureStage(issueId, Stage.Check);

      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'failed',
        message: 'Test failed',
      });

      const state1 = service.getStageState(issueId, Stage.Check);
      expect(state1!.checks[0].status).toBe('failed');
      expect(state1!.checks[0].runCount).toBe(1);

      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        message: 'All tests passed',
      });

      const state2 = service.getStageState(issueId, Stage.Check);
      expect(state2!.checks[0].status).toBe('passed');
      expect(state2!.checks[0].runCount).toBe(2);
    });
  });
});
