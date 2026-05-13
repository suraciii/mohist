import { describe, expect, it } from 'vitest';
import {
  DEFAULT_STAGE_DEFINITIONS,
  WorkflowDomainError,
  WorkflowRun,
  type StageDefinition,
} from '../src/workflow/domain';
import { Stage } from '../src/types';

function startRun(definitions: StageDefinition[] = DEFAULT_STAGE_DEFINITIONS): WorkflowRun {
  return WorkflowRun.startWorkflow({
    id: 'run-1',
    issueId: 'issue-1',
    issueNumber: 188,
    definitions,
  }).run;
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

function advanceToBuild(run: WorkflowRun): void {
  completePlanTasks(run);
  passPlanChecks(run);
  run.approveStage(Stage.Plan, { output: { approved: true } });
}

function advanceToIntegrate(run: WorkflowRun): void {
  advanceToBuild(run);
  run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
  run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
  run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
  run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
  run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } });
  run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass' });
  run.approveStage(Stage.Check, { output: { approved: true } });
}

describe('WorkflowRun domain aggregate', () => {
  it('starts at the first configured stage without hardcoded backlog-to-plan logic', () => {
    const definitions: StageDefinition[] = [
      {
        stage: Stage.Build,
        tasks: [{ id: 'T-001', title: 'First build task' }],
        checks: [],
      },
      {
        stage: Stage.Check,
        tasks: [],
        checks: [],
      },
    ];

    const { run, decision } = WorkflowRun.startWorkflow({
      id: 'run-1',
      issueId: 'issue-1',
      issueNumber: 188,
      definitions,
    });

    expect(run.status).toBe('running');
    expect(run.currentStage).toBe(Stage.Build);
    expect(run.stageRun(Stage.Build).status).toBe('running');
    expect(run.stageRun(Stage.Check).status).toBe('pending');
    expect(decision.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'T-001' });
  });

  it('enforces stage admission and one active stage', () => {
    const run = startRun();

    expect(() => run.completeTask(Stage.Build, 'T-001', { status: 'completed' })).toThrow(WorkflowDomainError);
    expect(run.stageRuns.filter(stageRun => stageRun.status === 'running')).toHaveLength(1);
    expect(run.stageRun(Stage.Plan).status).toBe('running');
  });

  it('rejects out-of-order task completion', () => {
    const run = startRun();

    expect(() => run.completeTask(Stage.Plan, 'specs', { status: 'completed' })).toThrow(/earlier tasks/);
    expect(run.stageRun(Stage.Plan).findTask('proposal').status).toBe('pending');
    expect(run.stageRun(Stage.Plan).findTask('specs').status).toBe('pending');
  });

  it('enters checks only after all required tasks are terminal', () => {
    const run = startRun();

    expect(() => run.recordCheckResult(Stage.Plan, { name: 'proposal-complete', status: 'pass' })).toThrow(/before tasks/);

    completePlanTasks(run);

    const decision = run.recordCheckResult(Stage.Plan, { name: 'proposal-complete', status: 'pass' });
    expect(decision.nextWork).toEqual({ kind: 'check', stage: Stage.Plan, checkName: 'specs-complete' });
  });

  it('fails the stage and workflow with task-failed when a task fails', () => {
    const run = startRun();
    const decision = run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'agent crashed' });

    expect(run.status).toBe('failed');
    expect(run.failure?.reason).toBe('task-failed');
    expect(run.failure?.taskId).toBe('proposal');
    expect(run.stageRun(Stage.Plan).failure?.reason).toBe('task-failed');
    expect(decision.nextWork).toEqual({ kind: 'failed', reason: run.failure });
  });

  it('records pass, fail, error, and pending check results', () => {
    const definitions: StageDefinition[] = [
      {
        stage: Stage.Build,
        tasks: [],
        checks: [
          { name: 'pending-check', title: 'Pending check' },
          { name: 'passing-check', title: 'Passing check' },
          { name: 'failing-check', title: 'Failing check' },
        ],
        checkFailurePolicies: [
          { checkName: 'failing-check', fixTaskId: 'fix-failing-check', fixTaskTitle: 'Fix failing check', maxAttempts: 1 },
        ],
      },
    ];
    const run = startRun(definitions);

    run.recordCheckResult(Stage.Build, { name: 'pending-check', status: 'pending', message: 'still running' });
    expect(run.stageRun(Stage.Build).findCheck('pending-check').status).toBe('pending');

    run.recordCheckResult(Stage.Build, { name: 'pending-check', status: 'error', message: 'transient error' });
    expect(run.status).toBe('failed');
    expect(run.stageRun(Stage.Build).findCheck('pending-check').status).toBe('error');

    const secondRun = startRun(definitions);
    secondRun.recordCheckResult(Stage.Build, { name: 'pending-check', status: 'pass' });
    secondRun.recordCheckResult(Stage.Build, { name: 'passing-check', status: 'pass' });
    secondRun.recordCheckResult(Stage.Build, { name: 'failing-check', status: 'fail', message: 'needs repair' });
    expect(secondRun.stageRun(Stage.Build).findCheck('passing-check').status).toBe('passed');
    expect(secondRun.stageRun(Stage.Build).findCheck('failing-check').status).toBe('pending');
    expect(secondRun.stageRun(Stage.Build).findTask('fix-failing-check').causedBy).toEqual({
      type: 'check-failure',
      checkName: 'failing-check',
      message: 'needs repair',
    });
  });

  it('schedules repair tasks by policy and reruns the failed check after the fix task', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });

    const decision = run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'fail', message: 'typecheck failed' });
    const fixTask = run.stageRun(Stage.Build).findTask('fix-build-health');

    expect(fixTask.status).toBe('pending');
    expect(fixTask.causedBy).toEqual({ type: 'check-failure', checkName: 'health:build', message: 'typecheck failed' });
    expect(decision.events).toContainEqual({
      type: 'fix-task-scheduled',
      stage: Stage.Build,
      taskId: 'fix-build-health',
      causedBy: fixTask.causedBy,
    });
    expect(decision.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'fix-build-health' });

    run.completeTask(Stage.Build, 'fix-build-health', { status: 'completed' });
    expect(run.nextWork()).toEqual({ kind: 'check', stage: Stage.Build, checkName: 'health:build' });
  });

  it('fails unrepaired checks with check-unrepaired and traceable metadata', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'fail', message: 'first failure' });
    run.completeTask(Stage.Build, 'fix-build-health', { status: 'completed' });

    const decision = run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'fail', message: 'still failing' });

    expect(run.status).toBe('failed');
    expect(run.failure).toEqual({
      reason: 'check-unrepaired',
      stage: Stage.Build,
      checkName: 'health:build',
      message: 'still failing',
      causedBy: { type: 'check-failure', checkName: 'health:build', message: 'still failing' },
    });
    expect(decision.nextWork).toEqual({ kind: 'failed', reason: run.failure });
  });

  it('requests approval only after tasks and checks pass', () => {
    const run = startRun();
    completePlanTasks(run);
    passPlanChecks(run);

    expect(run.stageRun(Stage.Plan).status).toBe('awaiting-approval');
    expect(run.nextWork()).toEqual({ kind: 'await-approval', stage: Stage.Plan });
    expect(run.stageRun(Stage.Plan).approval?.status).toBe('awaiting');
  });

  it('approveStage only works while awaiting approval and preserves approval output', () => {
    const run = startRun();

    expect(() => run.approveStage(Stage.Plan, { output: { early: true } })).toThrow(/not awaiting approval/);

    completePlanTasks(run);
    passPlanChecks(run);
    const decision = run.approveStage(Stage.Plan, { output: { approved: true, note: 'ship it' } });

    expect(run.stageRun(Stage.Plan).approval?.status).toBe('approved');
    expect(run.stageRun(Stage.Plan).approval?.output).toEqual({ approved: true, note: 'ship it' });
    expect(run.currentStage).toBe(Stage.Build);
    expect(decision.nextWork).toEqual({ kind: 'check', stage: Stage.Build, checkName: 'health:build' });
  });

  it('rejectStage only works while awaiting approval, preserves output, and fails with approval-rejected', () => {
    const run = startRun();

    expect(() => run.rejectStage(Stage.Plan, { output: 'too risky' })).toThrow(/not awaiting approval/);

    completePlanTasks(run);
    passPlanChecks(run);
    const decision = run.rejectStage(Stage.Plan, { output: { reason: 'missing test plan' } });

    expect(run.status).toBe('failed');
    expect(run.stageRun(Stage.Plan).approval?.status).toBe('rejected');
    expect(run.stageRun(Stage.Plan).approval?.output).toEqual({ reason: 'missing test plan' });
    expect(run.failure?.reason).toBe('approval-rejected');
    expect(decision.nextWork).toEqual({ kind: 'failed', reason: run.failure });
  });

  it('completes stages by configured order and completes the workflow after the final stage', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:merge', { status: 'completed' });
    const decision = run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'pass' });

    expect(run.status).toBe('passed');
    expect(run.stageRun(Stage.Integrate).status).toBe('passed');
    expect(decision.nextWork).toEqual({ kind: 'complete' });
  });

  it('records Integrate delivery metadata and freezes after merge completion', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed' });
    const decision = run.completeTask(Stage.Integrate, 'integrate:merge', {
      status: 'completed',
      output: {
        targetBranch: 'main',
        baseSha: 'base',
        candidateHeadSha: 'candidate',
        landedSha: 'landed',
        rebased: true,
      },
    });

    expect(run.stageRun(Stage.Integrate).freezePoint).toMatchObject({
      taskId: 'integrate:merge',
      delivery: {
        targetBranch: 'main',
        baseSha: 'base',
        candidateHeadSha: 'candidate',
        landedSha: 'landed',
        rebased: true,
      },
    });
    expect(decision.events).toContainEqual({
      type: 'integrate-frozen',
      stage: Stage.Integrate,
      freezePoint: run.stageRun(Stage.Integrate).freezePoint,
    });
  });

  it('fails post-merge health with post-merge-health-failed and does not schedule fixes after freeze', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:merge', {
      status: 'completed',
      output: { landedSha: 'landed' },
    });

    const decision = run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'fail', message: 'post merge test failed' });

    expect(run.status).toBe('failed');
    expect(run.failure?.reason).toBe('post-merge-health-failed');
    expect(run.stageRun(Stage.Integrate).tasks.map(task => task.id)).not.toContain('fix-integrate-health');
    expect(decision.nextWork).toEqual({ kind: 'failed', reason: run.failure });
  });

  it('does not roll back or advance after terminal failure', () => {
    const run = startRun();
    run.completeTask(Stage.Plan, 'proposal', { status: 'failed', reason: 'cannot produce proposal' });

    expect(() => run.completeTask(Stage.Plan, 'specs', { status: 'completed' })).toThrow(/WorkflowRun is failed/);
    expect(() => run.completeTask(Stage.Build, 'T-001', { status: 'completed' })).toThrow(/WorkflowRun is failed/);
    expect(run.currentStage).toBe(Stage.Plan);
    expect(run.stageRun(Stage.Build).status).toBe('pending');
  });
});
