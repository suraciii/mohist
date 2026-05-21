import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { Stage, IssueStatus } from '../src/types';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { IssueTaskQueueRepo } from '../src/db/issue-task-queue-repo';
import { WorkflowRunService } from '../src/services/workflow-run-service';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { EventBus } from '../src/services/event-bus';
import { IssueService } from '../src/services/issue-service';
import { WorkflowRun } from '../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../src/workflow/builtins/workflows/mohist-default';
import { WorkflowApplicationService } from '../src/services/workflow-application-service';

let projectCounter = 0;

function createMockWorktreeManager() {
  return {
    exists: vi.fn().mockReturnValue(true),
    create: vi.fn().mockResolvedValue('/tmp/worktree/issue'),
    getPath: vi.fn().mockReturnValue('/tmp/worktree/issue'),
    remove: vi.fn().mockResolvedValue(undefined),
    canFastForward: vi.fn().mockResolvedValue(true),
    rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
    abortRebase: vi.fn().mockResolvedValue(undefined),
  };
}

function startDefaultWorkflowRun(input: { id: string; issueId: string; issueNumber: number }) {
  return WorkflowRun.startWorkflow({
    ...input,
    definitions: DEFAULT_STAGE_DEFINITIONS,
  });
}

describe('T-005: rejected approval retry regressions', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let taskQueueRepo: IssueTaskQueueRepo;
  let workflowRunService: WorkflowRunService;

  beforeEach(() => {
    projectCounter = 0;
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    taskQueueRepo = new IssueTaskQueueRepo(db);
    workflowRunService = new WorkflowRunService(db);
  });

  afterEach(() => {
    db.close();
  });

  function setupProject(name?: string) {
    const n = name ?? `project-${++projectCounter}`;
    return projectRepo.create({ name: n, path: `/tmp/${n}`, baseBranch: 'main' });
  }

  function setupIssue(projectId: string, title = 'Test Issue') {
    return issueService.create({ projectId, title });
  }

  function createService(maxConcurrent = 8, wtManager?: any) {
    return new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      maxConcurrent,
      undefined,
      undefined,
      projectRepo,
      wtManager ?? createMockWorktreeManager(),
      taskQueueRepo,
      undefined,
      undefined,
      undefined,
      undefined,
      workflowRunService,
    );
  }

  describe('AC-1: Plan approval rejection + resume-pipeline retry (not skipped)', () => {
    it('resume-pipeline task does not complete as skipped when Plan approval was rejected', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.setApprovalState(issue.id, {
        stage: Stage.Plan,
        status: 'awaiting',
        requestedAt: new Date().toISOString(),
      });

      const run = workflowRunService.startRun(issue.id, issue.number, 'start-pipeline');

      const planStageRun = db.get<{ id: string }>(
        `SELECT id FROM workflow_stage_runs WHERE workflow_run_id = ? AND stage = ?`,
        [run.id, Stage.Plan],
      )!;

      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        db.run(
          `UPDATE workflow_tasks SET status = 'completed' WHERE stage_run_id = ? AND task_id = ?`,
          [planStageRun.id, taskId],
        );
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        db.run(
          `UPDATE workflow_checks SET status = 'passed' WHERE stage_run_id = ? AND check_name = ?`,
          [planStageRun.id, checkName],
        );
      }
      db.run(
        `UPDATE workflow_stage_runs SET approval_status = 'awaiting', approval_requested_at = ? WHERE id = ?`,
        [new Date().toISOString(), planStageRun.id],
      );

      workflowRunService.startRun(issue.id, issue.number, 'start-pipeline');
      const latestRun = db.get<{ id: string; status: string }>(
        `SELECT id, status FROM workflow_runs WHERE issue_id = ? ORDER BY created_at DESC LIMIT 1`,
        [issue.id],
      )!;

      const latestStageRun = db.get<{ id: string }>(
        `SELECT id FROM workflow_stage_runs WHERE workflow_run_id = ? AND stage = ?`,
        [latestRun.id, Stage.Plan],
      )!;

      db.run(
        `UPDATE workflow_stage_runs SET approval_status = 'rejected', approval_output = ?, approval_responded_at = ? WHERE id = ?`,
        [JSON.stringify('Please make the proposal more specific'), new Date().toISOString(), latestStageRun.id],
      );
      db.run(
        `UPDATE workflow_runs SET status = 'failed', current_stage = ? WHERE id = ?`,
        [Stage.Plan, latestRun.id],
      );

      const service = createService();
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 100));

      const dbRecord = taskQueueRepo.findById(result.taskId)!;
      expect(dbRecord.result).not.toBe('skipped');
    });

    it('resume-pipeline re-enters pipeline execution for rejected Plan approval retries', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const run = workflowRunService.startRun(issue.id, issue.number, 'start-pipeline');
      const planStageRun = db.get<{ id: string }>(
        `SELECT id FROM workflow_stage_runs WHERE workflow_run_id = ? AND stage = ?`,
        [run.id, Stage.Plan],
      )!;

      db.run(
        `UPDATE workflow_stage_runs SET approval_status = 'rejected', approval_output = ?, approval_responded_at = ? WHERE id = ?`,
        [JSON.stringify('Please revise the plan artifacts'), new Date().toISOString(), planStageRun.id],
      );
      db.run(
        `UPDATE workflow_runs SET status = 'failed', current_stage = ? WHERE id = ?`,
        [Stage.Plan, run.id],
      );

      const service = createService();
      const runPipelineSpy = vi.spyOn(service as any, 'runPipelineToCompletion').mockResolvedValue(undefined);

      service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 100));

      expect(runPipelineSpy).toHaveBeenCalledTimes(1);
      expect(runPipelineSpy.mock.calls[0][1].stage).toBe(Stage.Plan);
    });
  });

  describe('AC-2: genuinely blocked issues remain skipped/non-runnable', () => {
    it('resume-pipeline completes as skipped when issue is blocked with no retryable failed WorkflowRun', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateBlockedReason(issue.id, 'Something went wrong');

      const service = createService();
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId)!;
      expect(dbRecord.status).toBe('completed');
      expect(dbRecord.result).toBe('skipped');
    });

    it('resume-pipeline completes as skipped when latest WorkflowRun is failed but at a different stage', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      db.run(
        `INSERT INTO workflow_runs (id, issue_id, issue_number, status, current_stage, started_by, created_at, updated_at)
         VALUES (?, ?, ?, 'failed', ?, 'test', datetime('now'), datetime('now'))`,
        ['wr-ac2-diff-stage', issue.id, issue.number, Stage.Build],
      );
      db.run(
        `INSERT INTO workflow_stage_runs (id, workflow_run_id, stage, status, stage_order, created_at, updated_at)
         VALUES (?, ?, ?, 'failed', 1, datetime('now'), datetime('now'))`,
        ['stagerun-ac2-build', 'wr-ac2-diff-stage', Stage.Build],
      );

      const service = createService();
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId)!;
      expect(dbRecord.status).toBe('completed');
      expect(dbRecord.result).toBe('skipped');
    });

    it('resume-pipeline completes as skipped when latest WorkflowRun status is not failed', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      db.run(
        `INSERT INTO workflow_runs (id, issue_id, issue_number, status, current_stage, started_by, created_at, updated_at)
         VALUES (?, ?, ?, 'running', ?, 'test', datetime('now'), datetime('now'))`,
        ['wr-ac2-running', issue.id, issue.number, Stage.Plan],
      );

      const service = createService();
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId)!;
      expect(dbRecord.status).toBe('completed');
      expect(dbRecord.result).toBe('skipped');
    });

    it('resume-pipeline does not skip when blocked issue has interrupted recovery projection', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Build);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const workflowApplicationService = new WorkflowApplicationService(db);
      workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Plan, taskId, result: { status: 'completed' } });
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Plan, result: { name: checkName, status: 'pass' } });
      }
      workflowApplicationService.approveStage({ issueId: issue.id, stage: Stage.Plan, approval: { output: { approved: true } } });
      workflowApplicationService.materializeTasks({ issueId: issue.id, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build task', order: 0 }] });
      workflowApplicationService.startTaskAttempt({ issueId: issue.id, stage: Stage.Build, taskId: 'T-001', evidence: { executionId: 'build-issue-task-1' } });
      workflowApplicationService.interruptRunningWorkAttempts({ issueId: issue.id, reason: 'agent-lost' });

      const service = createService();
      const runPipelineSpy = vi.spyOn(service as any, 'runPipelineToCompletion').mockResolvedValue(undefined);
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 100));

      const dbRecord = taskQueueRepo.findById(result.taskId)!;
      expect(dbRecord.result).not.toBe('skipped');
      expect(runPipelineSpy).toHaveBeenCalledTimes(1);
      expect(runPipelineSpy.mock.calls[0][1].stage).toBe(Stage.Build);
    });

    it('resume-pipeline does not treat stale running latest attempt as retryable failed work', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const workflowApplicationService = new WorkflowApplicationService(db);
      workflowApplicationService.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        workflowApplicationService.completeTask({ issueId: issue.id, stage: Stage.Plan, taskId, result: { status: 'completed' } });
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid']) {
        workflowApplicationService.recordCheckResult({ issueId: issue.id, stage: Stage.Plan, result: { name: checkName, status: 'pass' } });
      }
      workflowApplicationService.startCheckAttempt({
        issueId: issue.id,
        stage: Stage.Plan,
        checkName: 'self-review-passed',
        evidence: { executionId: 'stale-self-review-check' },
      });

      const service = createService();
      const runPipelineSpy = vi.spyOn(service as any, 'runPipelineToCompletion').mockResolvedValue(undefined);
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 100));

      const dbRecord = taskQueueRepo.findById(result.taskId)!;
      expect(dbRecord.result).not.toBe('skipped');
      expect(runPipelineSpy).toHaveBeenCalledTimes(1);

      const recovery = new WorkflowApplicationService(db).getRecoveryProjection(issue.id);
      expect(recovery?.latestAttemptState).toBe('interrupted');
      expect(recovery?.allowedActions).toContain('resume');
      expect(recovery?.allowedActions).not.toContain('retry');
    });
  });

  describe('AC-3: canRetryStage predicate from domain aggregate', () => {
    it('canRetryStage returns true for failed WorkflowRun at current stage due to rejected approval', () => {
      const { run } = startDefaultWorkflowRun({
        id: 'wr-test-1',
        issueId: 'issue-test-1',
        issueNumber: 1,
      });

      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        run.completeTask(Stage.Plan, taskId, { status: 'completed' });
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
      }
      run.rejectStage(Stage.Plan, { output: 'needs rework' });

      expect(run.status).toBe('failed');
      expect(run.currentStage).toBe(Stage.Plan);
      expect(run.canRetryStage(Stage.Plan)).toBe(true);
    });

    it('canRetryStage returns false when WorkflowRun is not failed', () => {
      const { run } = startDefaultWorkflowRun({
        id: 'wr-test-2',
        issueId: 'issue-test-2',
        issueNumber: 2,
      });

      expect(run.status).toBe('running');
      expect(run.canRetryStage(Stage.Plan)).toBe(false);
    });

    it('canRetryStage returns false when failed WorkflowRun currentStage differs from requested stage', () => {
      const { run } = startDefaultWorkflowRun({
        id: 'wr-test-3',
        issueId: 'issue-test-3',
        issueNumber: 3,
      });

      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        run.completeTask(Stage.Plan, taskId, { status: 'completed' });
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
      }
      run.rejectStage(Stage.Plan, { output: 'needs rework' });
      expect(run.currentStage).toBe(Stage.Plan);

      expect(run.canRetryStage(Stage.Build)).toBe(false);
    });

    it('canRetryStage does not mutate the stored WorkflowRun', () => {
      const { run } = startDefaultWorkflowRun({
        id: 'wr-test-4',
        issueId: 'issue-test-4',
        issueNumber: 4,
      });

      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        run.completeTask(Stage.Plan, taskId, { status: 'completed' });
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
      }
      run.rejectStage(Stage.Plan, { output: 'needs rework' });

      const snapshotBefore = run.snapshot();
      run.canRetryStage(Stage.Plan);
      const snapshotAfter = run.snapshot();

      expect(snapshotBefore.status).toBe(snapshotAfter.status);
      expect(snapshotBefore.currentStage).toBe(snapshotAfter.currentStage);
      expect(snapshotBefore.stageRuns.map(s => s.status)).toEqual(snapshotAfter.stageRuns.map(s => s.status));
    });

    it('canRetryStage reports non-retryable for a stage that is failed but not the current stage', () => {
      const { run } = startDefaultWorkflowRun({
        id: 'wr-test-5',
        issueId: 'issue-test-5',
        issueNumber: 5,
      });

      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        run.completeTask(Stage.Plan, taskId, { status: 'completed' });
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });

      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'failed', reason: 'crashed' });

      expect(run.status).toBe('failed');
      expect(run.currentStage).toBe(Stage.Build);
      expect(run.canRetryStage(Stage.Plan)).toBe(false);
    });
  });

  describe('AC-4: rejection feedback persisted in WorkflowRun', () => {
    it('rejectStage stores the rejection feedback as approval output', () => {
      const { run } = startDefaultWorkflowRun({
        id: 'wr-test-6',
        issueId: 'issue-test-6',
        issueNumber: 6,
      });

      for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
        run.completeTask(Stage.Plan, taskId, { status: 'completed' });
      }
      for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
      }

      const decision = run.rejectStage(Stage.Plan, { output: 'The proposal is too vague, please redo' });

      const planApproval = run.stageRun(Stage.Plan).approval;
      expect(planApproval?.status).toBe('rejected');
      expect(planApproval?.output).toBe('The proposal is too vague, please redo');
      expect(run.failure?.reason).toBe('approval-rejected');
      expect(run.failure?.message).toBe('The proposal is too vague, please redo');
      expect(decision.events).toContainEqual(expect.objectContaining({ type: 'approval-rejected', stage: Stage.Plan }));
    });
  });

  describe('AC-5: retryStage resets Plan stage for a new attempt', () => {
    it('retryStage on Plan run that failed mid-stage resets from the first incomplete task', () => {
      const { run } = startDefaultWorkflowRun({
        id: 'wr-test-8b',
        issueId: 'issue-test-8b',
        issueNumber: 81,
      });

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'failed', reason: 'agent stopped' });

      const decision = run.retryStage(Stage.Plan);

      expect(run.status).toBe('running');
      expect(run.stageRun(Stage.Plan).status).toBe('running');
      expect(run.stageRun(Stage.Plan).approval).toBeNull();
      expect(run.stageRun(Stage.Plan).findTask('proposal').status).toBe('completed');
      expect(run.stageRun(Stage.Plan).findTask('specs').status).toBe('pending');
      expect(decision.events).toContainEqual(expect.objectContaining({ type: 'stage-retried', stage: Stage.Plan }));
    });
  });

  describe('AC-6: approved approval continuation path still works', () => {
    it('resume-pipeline does not skip when issue is blocked but current stage approval is approved', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.setApprovalState(issue.id, {
        stage: Stage.Plan,
        status: 'approved',
        requestedAt: new Date().toISOString(),
        output: { approved: true },
      });

      workflowRunService.startRun(issue.id, issue.number, 'start-pipeline');

      const service = createService();
      const result = service.enqueue(issue.id, 'resume-pipeline');

      await new Promise((r) => setTimeout(r, 50));

      const dbRecord = taskQueueRepo.findById(result.taskId)!;
      expect(dbRecord.result).not.toBe('skipped');
    });
  });
});
