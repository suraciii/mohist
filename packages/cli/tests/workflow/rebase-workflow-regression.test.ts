import { describe, expect, it } from 'vitest';
import {
  WorkflowRun,
  type StageDefinition,
} from '../../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../../src/workflow/definition/default-workflow';
import { Stage } from '../../src/types';

function startRun(definitions: StageDefinition[] = DEFAULT_STAGE_DEFINITIONS): WorkflowRun {
  return WorkflowRun.startWorkflow({
    id: 'run-1',
    issueId: 'issue-1',
    issueNumber: 188,
    definitions,
  }).run;
}

function scheduleRebaseTask(run: WorkflowRun, reason: string) {
  return run.scheduleRuntimeTask({
    taskId: 'rebase-branch',
    title: 'Rebase branch',
    causedBy: { type: 'branch-changed', message: reason },
  });
}

function startRunWithBuildTask(): WorkflowRun {
  const definitions: StageDefinition[] = [
    DEFAULT_STAGE_DEFINITIONS[0],
    {
      stage: Stage.Build,
      tasks: [{ id: 'T-001', title: 'Build task 1' }],
      checks: [
        { name: 'health:build', title: 'Build health gate' },
      ],
    },
    DEFAULT_STAGE_DEFINITIONS[2],
    DEFAULT_STAGE_DEFINITIONS[3],
  ];
  return startRun(definitions);
}

function completePlanTasks(run: WorkflowRun): void {
  for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
    run.completeTask(Stage.Plan, taskId, { status: 'completed' });
  }
}

function passPlanChecks(run: WorkflowRun): void {
  for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
    run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
  }
}

function advanceToCheck(run: WorkflowRun): void {
  completePlanTasks(run);
  passPlanChecks(run);
  run.approveStage(Stage.Plan, { output: { approved: true } });
  run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task 1', order: 1 }]);
  run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
  run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
}

function startCheckWithRebase(run: WorkflowRun): WorkflowRun {
  advanceToCheck(run);
  run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
  scheduleRebaseTask(run, 'Target branch moved');
  return run;
}

describe('Rebase workflow regression: T-005', () => {
  describe('scheduleRebaseTask idempotency', () => {
    it('duplicate click does not schedule a second rebase-branch task', () => {
      const run = startRunWithBuildTask();
      completePlanTasks(run);
      passPlanChecks(run);
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      const decision1 = scheduleRebaseTask(run, 'Target branch moved');
      expect(decision1.events).toHaveLength(0);

      const checkStage = run.stageRun(Stage.Check);
      const rebaseTasks = checkStage.tasks.filter(t => t.id === 'rebase-branch');
      expect(rebaseTasks).toHaveLength(1);

      const decision2 = scheduleRebaseTask(run, 'Target branch moved');
      expect(decision2.events).toHaveLength(0);

      const rebaseTasksAfter = checkStage.tasks.filter(t => t.id === 'rebase-branch');
      expect(rebaseTasksAfter).toHaveLength(1);
    });

    it('completed rebase-branch can be rescheduled after it becomes terminal', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());
      const rebaseTask = run.stageRun(Stage.Check).findTask('rebase-branch');
      rebaseTask.status = 'completed';

      scheduleRebaseTask(run, 'main branch advanced');
      const rebaseTasks = run.stageRun(Stage.Check).tasks.filter(t => t.id === 'rebase-branch');
      expect(rebaseTasks).toHaveLength(2);
    });

    it('failed rebase-branch can be rescheduled', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());
      const rebaseTask = run.stageRun(Stage.Check).findTask('rebase-branch');
      rebaseTask.status = 'failed';
      rebaseTask.reason = 'Rebase conflict';

      scheduleRebaseTask(run, 'main branch advanced');
      const rebaseTasks = run.stageRun(Stage.Check).tasks.filter(t => t.id === 'rebase-branch');
      expect(rebaseTasks).toHaveLength(2);
    });
  });

  describe('approval-stage reopen on rebase scheduling', () => {
    it('rebase scheduling reopens awaiting-approval stage to running', () => {
      const run = startRunWithBuildTask();
      completePlanTasks(run);
      passPlanChecks(run);

      expect(run.stageRun(Stage.Plan).status).toBe('awaiting-approval');

      scheduleRebaseTask(run, 'main branch advanced');

      expect(run.stageRun(Stage.Plan).status).toBe('running');
    });

    it('rebase task is returned by nextWork() after approval stage reopens', () => {
      const run = startRunWithBuildTask();
      completePlanTasks(run);
      passPlanChecks(run);

      scheduleRebaseTask(run, 'Target branch moved');

      const nextWork = run.nextWork();
      expect(nextWork).toEqual({ kind: 'task', stage: Stage.Plan, taskId: 'rebase-branch' });
    });

    it('rebase scheduling preserves approval state until invalidation facts are reported', () => {
      const run = startRunWithBuildTask();
      completePlanTasks(run);
      passPlanChecks(run);

      expect(run.stageRun(Stage.Plan).approval?.status).toBe('awaiting');

      scheduleRebaseTask(run, 'Target branch moved');

      expect(run.stageRun(Stage.Plan).approval?.status).toBe('awaiting');
    });
  });

  describe('nextWork() returning rebase-branch', () => {
    it('nextWork() returns rebase-branch task in earliest non-terminal position', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findTask('ai-review').status = 'completed';

      const nextWork = run.nextWork();
      expect(nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'rebase-branch' });
    });

    it('later tasks do not run until rebase-branch is terminal', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findTask('ai-review').status = 'completed';

      const nextWork1 = run.nextWork();
      expect(nextWork1.kind).toBe('task');
      expect((nextWork1 as any).taskId).toBe('rebase-branch');

      checkStage.findTask('rebase-branch').status = 'completed';

      const nextWork2 = run.nextWork();
      expect(nextWork2).toEqual({ kind: 'check', stage: Stage.Check, checkName: 'health:check' });
    });
  });

  describe('rebase-branch task failure blocking', () => {
    it('failed rebase-branch causes current stage to fail', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'failed',
        reason: 'Rebase conflict: src/conflict.ts',
      });

      expect(run.status).toBe('failed');
      expect(run.failure?.reason).toBe('task-failed');
      expect(run.failure?.taskId).toBe('rebase-branch');
      expect(decision.nextWork.kind).toBe('failed');
    });

    it('later tasks do not execute after rebase-branch fails', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      run.completeTask(Stage.Check, 'rebase-branch', { status: 'failed', reason: 'Rebase failed' });

      const checkStage = run.stageRun(Stage.Check);
      expect(checkStage.findCheck('review-passed').status).toBe('pending');
    });

    it('checks do not run after rebase-branch fails', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      run.completeTask(Stage.Check, 'rebase-branch', { status: 'failed', reason: 'Rebase failed' });

      expect(run.nextWork().kind).toBe('failed');
    });
  });

  describe('shaChanged=false preserves review/check state', () => {
    it('rebase-branch with shaChanged=false does not invalidate review checks', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        reason: 'Branch is up to date',
        output: {
          rebased: false,
          baseBranch: 'main',
          beforeBaseSha: 'abc123',
          afterBaseSha: 'abc123',
          beforeHeadSha: 'def456',
          afterHeadSha: 'def456',
          shaChanged: false,
          conflicts: [],
        },
      });

      expect(checkStage.findTask('ai-review').status).toBe('completed');
      expect(checkStage.findCheck('review-passed').status).toBe('passed');
      expect(checkStage.findCheck('merge-ready').status).toBe('passed');
      expect(decision.events.filter((e: any) => e.type === 'check-invalidated')).toHaveLength(0);
    });

    it('shaChanged=false does not invalidate checks that are already passing', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        reason: 'Branch is up to date',
        output: {
          rebased: false,
          baseBranch: 'main',
          beforeBaseSha: 'abc123',
          afterBaseSha: 'abc123',
          beforeHeadSha: 'def456',
          afterHeadSha: 'def456',
          shaChanged: false,
          conflicts: [],
        },
      });

      expect(checkStage.findTask('ai-review').status).toBe('completed');
      expect(checkStage.findCheck('review-passed').status).toBe('passed');
      expect(checkStage.findCheck('merge-ready').status).toBe('passed');
      expect(decision.events.filter((e: any) => e.type === 'check-invalidated')).toHaveLength(0);
    });
  });

  describe('shaChanged=true invalidates review/check state', () => {
    it('rebase-branch with shaChanged=true invalidates ai-review, review-passed, merge-ready', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        events: ['code.changed'],
        output: {
          rebased: true,
          baseBranch: 'main',
          beforeBaseSha: 'abc123',
          afterBaseSha: 'def456',
          beforeHeadSha: 'ghi789',
          afterHeadSha: 'jkl012',
          shaChanged: true,
          conflicts: [],
        },
      });

      expect(checkStage.findTask('ai-review').status).toBe('pending');
      expect(checkStage.findCheck('review-passed').status).toBe('pending');
      expect(checkStage.findCheck('merge-ready').status).toBe('pending');

      const shaChangedEvent = decision.events.find((e: any) => e.type === 'task-invalidated' && e.taskId === 'ai-review:1');
      expect(shaChangedEvent).toBeDefined();
    });

    it('shaChanged=true triggers events for invalidated checks', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        events: ['code.changed'],
        output: {
          rebased: true,
          shaChanged: true,
          beforeBaseSha: 'abc',
          afterBaseSha: 'def',
          beforeHeadSha: 'ghi',
          afterHeadSha: 'jkl',
        },
      });

      const taskInvalidatedEvents = decision.events.filter((e: any) => e.type === 'task-invalidated');
      expect(taskInvalidatedEvents).toHaveLength(1);
      expect(taskInvalidatedEvents[0].taskId).toBe('ai-review:1');

      const checkInvalidatedEvents = decision.events.filter((e: any) => e.type === 'check-invalidated');
      expect(checkInvalidatedEvents).toHaveLength(3);
      const invalidatedCheckNames = checkInvalidatedEvents.map((e: any) => e.checkName);
      expect(invalidatedCheckNames).toContain('health:check');
      expect(invalidatedCheckNames).toContain('review-passed');
      expect(invalidatedCheckNames).toContain('merge-ready');
    });

    it('shaChanged=true with awaiting approval clears stale approval state', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';
      checkStage.approval = { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' };

      run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        events: ['code.changed'],
        output: {
          rebased: true,
          shaChanged: true,
        },
      });

      expect(checkStage.approval).toBeNull();
      expect(checkStage.status).toBe('running');
    });
  });

  describe('rebase-branch visibility in stage task list', () => {
    it('rebase-branch appears in current stage task list after scheduling', () => {
      const run = startRunWithBuildTask();
      completePlanTasks(run);
      passPlanChecks(run);
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      scheduleRebaseTask(run, 'main branch advanced');

      const checkStage = run.stageRun(Stage.Check);
      const rebaseTask = checkStage.tasks.find(t => t.id === 'rebase-branch');
      expect(rebaseTask).toBeDefined();
      expect(rebaseTask?.title).toBe('Rebase branch');
      expect(rebaseTask?.status).toBe('pending');
    });

    it('rebase-branch has branch-changed causedBy metadata', () => {
      const run = startRunWithBuildTask();
      completePlanTasks(run);
      passPlanChecks(run);
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      scheduleRebaseTask(run, 'Target branch moved');

      const rebaseTask = run.stageRun(Stage.Check).findTask('rebase-branch');
      expect(rebaseTask.causedBy).toEqual({
        type: 'branch-changed',
        message: 'Target branch moved',
      });
    });

    it('rebase-branch terminal status transitions are visible', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const rebaseTask = run.stageRun(Stage.Check).findTask('rebase-branch');
      expect(rebaseTask.status).toBe('pending');

      rebaseTask.status = 'running';
      expect(rebaseTask.status).toBe('running');

      rebaseTask.status = 'completed';
      expect(rebaseTask.status).toBe('completed');
    });
  });

  describe('rebase-branch output contract', () => {
    it('completeTask with rebase output records shaChanged fact', () => {
      const run = startCheckWithRebase(startRunWithBuildTask());

      const output = {
        rebased: true,
        baseBranch: 'main',
        beforeBaseSha: 'abc123',
        afterBaseSha: 'def456',
        beforeHeadSha: 'ghi789',
        afterHeadSha: 'jkl012',
        shaChanged: true,
        conflicts: [],
      };

      run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        output,
      });

      const rebaseTask = run.stageRun(Stage.Check).findTask('rebase-branch');
      expect(rebaseTask.output).toMatchObject({
        rebased: true,
        shaChanged: true,
      });
    });
  });
});
