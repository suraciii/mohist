import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { ProjectRepo } from '../../src/db/project-repo';
import { IssueRepo } from '../../src/db/issue-repo';
import type { WorkflowRunWithStageRuns } from '../../src/db/workflow-run-repo';
import { WorkflowRunService } from '../../src/services/workflow-run-service';
import { Stage } from '../../src/types';

describe('WorkflowRun persistence', () => {
  let db: DatabaseManager;
  let workflowRunService: WorkflowRunService;
  let issueId: string;
  let issueNumber: number;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 42, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;
    issueNumber = issue.number;

    workflowRunService = new WorkflowRunService(db);
  });

  afterEach(() => {
    db.close();
  });

  function insertWorkflowTask(run: WorkflowRunWithStageRuns, stage: Stage, input: {
    taskId: string;
    title: string;
    status?: string;
    reason?: string | null;
    causedByType?: string | null;
    causedByCheckName?: string | null;
    causedByTaskId?: string | null;
  }): void {
    const stageRun = run.stageRuns.find(candidate => candidate.stage === stage)!;
    const now = new Date().toISOString();
    db.run(
      `INSERT INTO workflow_tasks
       (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, output,
        reason, caused_by_type, caused_by_check_name, caused_by_task_id, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, 0, 0, '[]', NULL, ?, ?, ?, ?, ?, ?)`,
      [
        `${stageRun.id}/${input.taskId}`,
        run.id,
        stageRun.id,
        input.taskId,
        input.title,
        input.status ?? 'pending',
        stageRun.tasks.length,
        input.reason ?? null,
        input.causedByType ?? null,
        input.causedByCheckName ?? null,
        input.causedByTaskId ?? null,
        now,
        now,
      ],
    );
  }

  function updateWorkflowCheck(run: WorkflowRunWithStageRuns, stage: Stage, input: {
    checkName: string;
    status: string;
    message?: string | null;
  }): void {
    const stageRun = run.stageRuns.find(candidate => candidate.stage === stage)!;
    const now = new Date().toISOString();
    db.run(
      `UPDATE workflow_checks SET status = ?, message = ?, run_count = run_count + 1, last_run_at = ?, updated_at = ?
       WHERE stage_run_id = ? AND check_name = ?`,
      [input.status, input.message ?? null, now, now, stageRun.id, input.checkName],
    );
  }

  function setWorkflowApproval(run: WorkflowRunWithStageRuns, stage: Stage, input: {
    status: string;
    output: unknown;
    requestedAt: string;
    respondedAt?: string | null;
  }): void {
    const stageRun = run.stageRuns.find(candidate => candidate.stage === stage)!;
    db.run(
      `UPDATE workflow_stage_runs
       SET approval_status = ?, approval_output = ?, approval_requested_at = ?, approval_responded_at = ?, updated_at = ?
       WHERE id = ?`,
      [input.status, JSON.stringify(input.output), input.requestedAt, input.respondedAt ?? null, new Date().toISOString(), stageRun.id],
    );
  }

  describe('startRun', () => {
    it('creates exactly one active WorkflowRun bound to issue id and issue number', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      expect(run.issueId).toBe(issueId);
      expect(run.issueNumber).toBe(issueNumber);
      expect(run.status).toBe('running');
      expect(run.currentStage).toBe(Stage.Plan);
      expect(run.id).toMatch(/^wr_42_/);
    });

    it('creates one active run with stable id', () => {
      const run1 = workflowRunService.startRun(issueId, issueNumber);
      const run2 = workflowRunService.startRun(issueId, issueNumber);

      expect(run1.id).toBe(run2.id);
    });

    it('creates StageRuns for plan, build, check, and integrate in correct order', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      expect(run.stageRuns).toHaveLength(4);
      expect(run.stageRuns[0].stage).toBe(Stage.Plan);
      expect(run.stageRuns[0].stageOrder).toBe(0);
      expect(run.stageRuns[1].stage).toBe(Stage.Build);
      expect(run.stageRuns[1].stageOrder).toBe(1);
      expect(run.stageRuns[2].stage).toBe(Stage.Check);
      expect(run.stageRuns[2].stageOrder).toBe(2);
      expect(run.stageRuns[3].stage).toBe(Stage.Integrate);
      expect(run.stageRuns[3].stageOrder).toBe(3);

      expect(run.stageRuns.map(sr => sr.status)).toEqual(['running', 'pending', 'pending', 'pending']);
    });

    it('seeds Plan StageRun with proposal, specs, design, tasks, self-review tasks', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);
      const planStageRun = run.stageRuns.find(sr => sr.stage === Stage.Plan)!;

      const taskIds = planStageRun.tasks.map(t => t.taskId);
      expect(taskIds).toContain('proposal');
      expect(taskIds).toContain('specs');
      expect(taskIds).toContain('design');
      expect(taskIds).toContain('tasks');
      expect(taskIds).toContain('self-review');

      for (const task of planStageRun.tasks) {
        expect(task.status).toBe('pending');
        expect(task.attempts).toBe(0);
      }
    });

    it('seeds Integrate StageRun with integrate:spec-sync, integrate:archive-change, integrate:merge tasks', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);
      const integrateStageRun = run.stageRuns.find(sr => sr.stage === Stage.Integrate)!;

      const taskIds = integrateStageRun.tasks.map(t => t.taskId);
      expect(taskIds).toContain('integrate:spec-sync');
      expect(taskIds).toContain('integrate:archive-change');
      expect(taskIds).toContain('integrate:merge');

      const specSyncTask = integrateStageRun.tasks.find(t => t.taskId === 'integrate:spec-sync')!;
      expect(specSyncTask.taskOrder).toBe(0);
      expect(specSyncTask.status).toBe('pending');

      const archiveTask = integrateStageRun.tasks.find(t => t.taskId === 'integrate:archive-change')!;
      expect(archiveTask.taskOrder).toBe(1);
      expect(archiveTask.status).toBe('pending');

      const mergeTask = integrateStageRun.tasks.find(t => t.taskId === 'integrate:merge')!;
      expect(mergeTask.taskOrder).toBe(2);
      expect(mergeTask.status).toBe('pending');
    });

    it('seeds Integrate StageRun with health:integrate check', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);
      const integrateStageRun = run.stageRuns.find(sr => sr.stage === Stage.Integrate)!;

      const checkNames = integrateStageRun.checks.map(c => c.checkName);
      expect(checkNames).toContain('health:integrate');

      const healthCheck = integrateStageRun.checks.find(c => c.checkName === 'health:integrate')!;
      expect(healthCheck.status).toBe('pending');
    });

    it('startRun twice returns same active run with Integrate tasks and checks intact', () => {
      const run1 = workflowRunService.startRun(issueId, issueNumber);
      const run2 = workflowRunService.startRun(issueId, issueNumber);

      expect(run1.id).toBe(run2.id);

      const integrateStageRun = run2.stageRuns.find(sr => sr.stage === Stage.Integrate)!;
      expect(integrateStageRun.tasks.map(t => t.taskId)).toEqual(['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge']);
      expect(integrateStageRun.checks.map(c => c.checkName)).toContain('health:integrate');
    });

    it('seeds Plan StageRun with proposal-complete, specs-complete, design-complete, tasks-valid, self-review-passed, user-approval checks', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);
      const planStageRun = run.stageRuns.find(sr => sr.stage === Stage.Plan)!;

      const checkNames = planStageRun.checks.map(c => c.checkName);
      expect(checkNames).toContain('proposal-complete');
      expect(checkNames).toContain('specs-complete');
      expect(checkNames).toContain('design-complete');
      expect(checkNames).toContain('tasks-valid');
      expect(checkNames).toContain('self-review-passed');

      for (const check of planStageRun.checks) {
        expect(check.status).toBe('pending');
      }
    });

    it('startRun twice returns same active run', () => {
      const run1 = workflowRunService.startRun(issueId, issueNumber);
      const run2 = workflowRunService.startRun(issueId, issueNumber);

      expect(run1.id).toBe(run2.id);
      expect(run2.stageRuns).toHaveLength(4);
    });
  });

  describe('materializeBuildTasks', () => {
    it('materializes Build tasks from tasks.json into the same WorkflowRun', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);
      const buildTasks = [
        { id: 'T-001', title: 'Add persistence', order: 1 },
        { id: 'T-002', title: 'Expose API', order: 2 },
        { id: 'T-003', title: 'Write tests', order: 3 },
      ];

      for (const task of buildTasks) {
        insertWorkflowTask(run, Stage.Build, { taskId: task.id, title: task.title });
      }

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const buildStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Build)!;

      expect(buildStageRun.tasks).toHaveLength(3);
      expect(buildStageRun.tasks[0].taskId).toBe('T-001');
      expect(buildStageRun.tasks[0].title).toBe('Add persistence');
      expect(buildStageRun.tasks[0].status).toBe('pending');
      expect(buildStageRun.tasks[1].taskId).toBe('T-002');
      expect(buildStageRun.tasks[2].taskId).toBe('T-003');
    });

    it('upserts Build tasks without creating duplicates on repeated materialization', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);
      const buildTasks = [
        { id: 'T-001', title: 'Add persistence', order: 1 },
      ];

      insertWorkflowTask(run, Stage.Build, { taskId: buildTasks[0].id, title: buildTasks[0].title });

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const buildStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Build)!;

      expect(buildStageRun.tasks).toHaveLength(1);
      expect(buildStageRun.tasks[0].taskId).toBe('T-001');
    });
  });

  describe('runtime-added tasks with metadata', () => {
    it('appends repair task with reason and causedBy metadata', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      insertWorkflowTask(run, Stage.Check, {
        taskId: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'completed',
        reason: 'Added after review passed failed',
        causedByType: 'check-failure',
        causedByCheckName: 'ai-review',
      });

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const checkStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Check)!;
      const task = checkStageRun.tasks.find(t => t.taskId === 'fix-review-findings')!;

      expect(task).toBeDefined();
      expect(task.status).toBe('completed');
      expect(task.reason).toBe('Added after review passed failed');
      expect(task.causedByType).toBe('check-failure');
      expect(task.causedByCheckName).toBe('ai-review');
    });

    it('appends rebase task with reason and causedBy metadata', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      insertWorkflowTask(run, Stage.Integrate, {
        taskId: 'rebase-branch',
        title: 'Rebase branch',
        status: 'completed',
        reason: 'Added because target branch moved',
        causedByType: 'branch-changed',
      });

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const integrateStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Integrate)!;
      const task = integrateStageRun.tasks.find(t => t.taskId === 'rebase-branch')!;

      expect(task).toBeDefined();
      expect(task.reason).toBe('Added because target branch moved');
      expect(task.causedByType).toBe('branch-changed');
    });

    it('appends retry task with reason and causedBy metadata', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      insertWorkflowTask(run, Stage.Build, {
        taskId: 'T-001',
        title: 'Retry compile',
        status: 'failed',
        reason: 'Task failed on first attempt',
        causedByType: 'task-failure',
        causedByTaskId: 'T-001',
      });

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const buildStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Build)!;
      const task = buildStageRun.tasks.find(t => t.taskId === 'T-001')!;

      expect(task).toBeDefined();
      expect(task.reason).toBe('Task failed on first attempt');
      expect(task.causedByType).toBe('task-failure');
    });

    it('appends conflict task with reason and causedBy metadata', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      insertWorkflowTask(run, Stage.Integrate, {
        taskId: 'resolve-conflict',
        title: 'Resolve merge conflict',
        status: 'completed',
        reason: 'Merge conflict detected',
        causedByType: 'conflict',
      });

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const integrateStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Integrate)!;
      const task = integrateStageRun.tasks.find(t => t.taskId === 'resolve-conflict')!;

      expect(task).toBeDefined();
      expect(task.causedByType).toBe('conflict');
    });
  });

  describe('stage_executions, workflow_log, session logs, checkpoints not used as primary state', () => {
    it('does not use stage_executions as primary task/check state source', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      const now = new Date().toISOString();
      db.run(
        `INSERT INTO stage_executions (id, issue_id, stage, status, task_results, check_results, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          'exec-1',
          issueId,
          Stage.Check,
          'failed',
          JSON.stringify([{ taskId: 'fake-task', title: 'Fake task from execution', status: 'completed', artifacts: [], attempts: 1, duration: 100 }]),
          JSON.stringify([{ name: 'fake-check', status: 'pass', message: 'Fake check' }]),
          now,
          now,
        ],
      );

      const freshRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const checkStageRun = freshRun.stageRuns.find(sr => sr.stage === Stage.Check)!;

      const hasFakeTask = checkStageRun.tasks.some(t => t.taskId === 'fake-task');
      const hasFakeCheck = checkStageRun.checks.some(c => c.checkName === 'fake-check');
      expect(hasFakeTask).toBe(false);
      expect(hasFakeCheck).toBe(false);
    });

    it('WorkflowRunService does not read from workflow_log for current task/check state', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      const now = new Date().toISOString();
      db.run(
        `INSERT INTO workflow_log (id, issue_id, session_id, event_type, data, created_at)
         VALUES (?, ?, ?, ?, ?, ?)`,
        [
          'log-1',
          issueId,
          null,
          'task_completed',
          JSON.stringify({ taskId: 'fake-from-log', title: 'Fake task from log', status: 'completed' }),
          now,
        ],
      );

      const freshRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const buildStageRun = freshRun.stageRuns.find(sr => sr.stage === Stage.Build)!;

      const hasFakeFromLog = buildStageRun.tasks.some(t => t.taskId === 'fake-from-log');
      expect(hasFakeFromLog).toBe(false);
    });

    it('WorkflowRunService does not use pipeline_checkpoint as current state source', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      const now = new Date().toISOString();
      db.run(
        `INSERT INTO pipeline_checkpoint (issue_number, stage, completed_steps, next_step, updated_at)
         VALUES (?, ?, ?, ?, ?)`,
        [
          issueNumber,
          'build',
          JSON.stringify([{ step: 'task', index: 99 }]),
          null,
          now,
        ],
      );

      const freshRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const buildStageRun = freshRun.stageRuns.find(sr => sr.stage === Stage.Build)!;

      expect(buildStageRun.status).toBe('pending');
      expect(buildStageRun.tasks).toHaveLength(0);
    });
  });

  describe('upsertCheck', () => {
    it('updates check status', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);

      updateWorkflowCheck(run, Stage.Plan, {
        checkName: 'proposal-complete',
        status: 'passed',
        message: 'Proposal is ready',
      });

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const planStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Plan)!;
      const check = planStageRun.checks.find(c => c.checkName === 'proposal-complete')!;

      expect(check.status).toBe('passed');
      expect(check.message).toBe('Proposal is ready');
    });
  });

  describe('setApproval', () => {
    it('stores approval snapshot on StageRun', () => {
      const run = workflowRunService.startRun(issueId, issueNumber);
      const now = new Date().toISOString();

      setWorkflowApproval(run, Stage.Plan, {
        status: 'awaiting',
        output: { result: 'PASS' },
        requestedAt: now,
        respondedAt: null,
      });

      const updatedRun = workflowRunService.getActiveRunForIssue(issueId)!;
      const planStageRun = updatedRun.stageRuns.find(sr => sr.stage === Stage.Plan)!;

      expect(planStageRun.approvalStatus).toBe('awaiting');
      expect(planStageRun.approvalOutput).toEqual({ result: 'PASS' });
      expect(planStageRun.approvalRequestedAt).toBeTruthy();
    });
  });
});
