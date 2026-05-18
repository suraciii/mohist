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
  for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid']) {
    run.recordCheckResult(Stage.Plan, { name: checkName, status: 'pass' });
  }
  run.recordCheckResult(Stage.Plan, {
    name: 'self-review-passed',
    status: 'pass',
    output: {
      verdict: 'PASS',
      selfReviewNotes: 'Plan self review\n<promise>PASS</promise>',
      dimensions: [{ name: 'Completeness', status: 'PASS' }],
    },
  });
  run.recordCheckResult(Stage.Plan, { name: 'health:plan', status: 'pass' });
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
  run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
  run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } });
  run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-check') });
  run.approveStage(Stage.Check, { output: { approved: true } });
}

function mergeReadyOutput(candidateHeadSha: string): Record<string, unknown> {
  return {
    kind: 'merge-ready',
    targetBranch: 'master',
    strategy: 'squash',
    baseSha: 'base-sha',
    candidateHeadSha,
    mergeBaseSha: 'base-sha',
    canMerge: true,
    conflictFiles: [],
    checkedAt: '2026-05-15T00:00:00.000Z',
  };
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

  it('runs pre-task checks before tasks and post-task checks after tasks', () => {
    const definitions: StageDefinition[] = [
      {
        stage: Stage.Build,
        tasks: [{ id: 'T-001', title: 'Build task' }],
        checks: [
          { name: 'preflight', title: 'Preflight' },
          { name: 'health:build', title: 'Build health' },
        ],
        checkPolicies: [
          { checkName: 'preflight', phase: 'pre-task' },
          { checkName: 'health:build', phase: 'post-task' },
        ],
      },
    ];
    const run = startRun(definitions);

    expect(run.nextWork()).toEqual({ kind: 'check', stage: Stage.Build, checkName: 'preflight' });
    expect(() => run.completeTask(Stage.Build, 'T-001', { status: 'completed' })).toThrow(/earlier tasks|before earlier checks/);

    const preflight = run.recordCheckResult(Stage.Build, { name: 'preflight', status: 'pass' });
    expect(preflight.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'T-001' });

    const task = run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    expect(task.nextWork).toEqual({ kind: 'check', stage: Stage.Build, checkName: 'health:build' });
  });

  it('does not infer completion when declared static tasks or checks are missing from run evidence', () => {
    const runWithMissingTask = startRun([
      {
        stage: Stage.Build,
        tasks: [{ id: 'T-001', title: 'Declared task' }],
        checks: [],
      },
    ]);
    runWithMissingTask.stageRun(Stage.Build).tasks.splice(0);

    expect(runWithMissingTask.nextWork()).toEqual({
      kind: 'blocked',
      stage: Stage.Build,
      reason: { complete: false, reason: 'missing-static-task', taskId: 'T-001' },
    });

    const runWithMissingCheck = startRun([
      {
        stage: Stage.Build,
        tasks: [],
        checks: [{ name: 'health:build', title: 'Build health gate' }],
      },
    ]);
    runWithMissingCheck.stageRun(Stage.Build).checks.splice(0);

    expect(runWithMissingCheck.nextWork()).toEqual({
      kind: 'blocked',
      stage: Stage.Build,
      reason: { complete: false, reason: 'missing-static-check', checkName: 'health:build' },
    });
  });

  it.each([
    ['missing', 'dynamic-source-missing'],
    ['invalid', 'dynamic-source-invalid'],
    ['empty', 'dynamic-source-empty'],
  ] as const)('blocks Build completion when dynamic tasks.json source is %s', (state, reason) => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [], state);
    const decision = run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

    expect(run.status).toBe('running');
    expect(decision.nextWork).toEqual({
      kind: 'blocked',
      stage: Stage.Build,
      reason: { complete: false, reason, stage: Stage.Build },
    });
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

  it('does not advance to dependent build tasks after an incomplete task', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First build task', order: 1 },
      { id: 'T-002', title: 'Second build task', order: 2, dependsOn: ['T-001'] },
    ]);

    const decision = run.completeTask(Stage.Build, 'T-001', { status: 'skipped', reason: 'process exited' });

    expect(run.status).toBe('failed');
    expect(run.failure).toMatchObject({ reason: 'task-failed', stage: Stage.Build, taskId: 'T-001' });
    expect(run.stageRun(Stage.Build).findTask('T-002').status).toBe('pending');
    expect(decision.nextWork).toEqual({ kind: 'failed', reason: run.failure });
  });

  it('reruns current stage from the first work item and clears all current-stage state', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First build task', order: 0 },
      { id: 'T-002', title: 'Second build task', order: 1, dependsOn: ['T-001'] },
    ]);
    const buildStage = run.stageRun(Stage.Build);
    buildStage.findTask('T-001').attempts = 2;
    buildStage.findTask('T-001').status = 'completed';
    buildStage.findTask('T-001').output = { text: 'done' };
    buildStage.findTask('T-001').artifacts = ['artifact-1'];
    buildStage.findTask('T-002').status = 'completed';
    buildStage.findTask('T-002').output = { text: 'also done' };
    buildStage.findCheck('health:build').status = 'passed';

    const decision = run.rerunStage(Stage.Build);

    expect(run.status).toBe('running');
    expect(run.failure).toBeNull();
    expect(run.currentStage).toBe(Stage.Build);
    expect(buildStage.status).toBe('running');
    expect(buildStage.failure).toBeNull();
    expect(buildStage.approval).toBeNull();
    expect(buildStage.findTask('T-001')).toMatchObject({
      status: 'pending',
      attempts: 0,
      output: null,
      artifacts: [],
    });
    expect(buildStage.findTask('T-002')).toMatchObject({
      status: 'pending',
      output: null,
      artifacts: [],
    });
    expect(buildStage.findCheck('health:build')).toMatchObject({
      status: 'pending',
      message: null,
      output: null,
      runCount: 0,
    });
    expect(decision.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'T-001' });
  });

  it('rerun does not reset earlier passed stages', () => {
    const definitions: StageDefinition[] = [
      {
        stage: Stage.Plan,
        tasks: [
          { id: 'proposal', title: 'Generate proposal' },
          { id: 'specs', title: 'Write specs' },
        ],
        checks: [
          { name: 'proposal-complete', title: 'Proposal complete' },
          { name: 'specs-complete', title: 'Specs complete' },
        ],
        requiresApproval: true,
        approvalCheckName: 'user-approval',
      },
      {
        stage: Stage.Build,
        tasks: [{ id: 'T-001', title: 'Build task' }],
        checks: [{ name: 'build-check', title: 'Build check' }],
      },
    ];

    const run = startRun(definitions);
    run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
    run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
    run.recordCheckResult(Stage.Plan, { name: 'proposal-complete', status: 'pass' });
    run.recordCheckResult(Stage.Plan, { name: 'specs-complete', status: 'pass' });
    run.approveStage(Stage.Plan, { output: { approved: true } });
    expect(run.stageRun(Stage.Plan).status).toBe('passed');
    expect(run.currentStage).toBe(Stage.Build);

    run.completeTask(Stage.Build, 'T-001', { status: 'failed', reason: 'build failed' });
    expect(run.currentStage).toBe(Stage.Build);
    expect(run.status).toBe('failed');

    run.rerunStage(Stage.Build);

    expect(run.currentStage).toBe(Stage.Build);
    expect(run.status).toBe('running');
    expect(run.stageRun(Stage.Plan).status).toBe('passed');
  });

  it('plan rerun makes the first Plan work pending even when prior artifact files exist', () => {
    const run = startRun();
    completePlanTasks(run);
    passPlanChecks(run);

    const planStage = run.stageRun(Stage.Plan);
    expect(planStage.status).toBe('awaiting-approval');
    expect(planStage.findTask('proposal').status).toBe('completed');
    expect(planStage.findTask('specs').status).toBe('completed');
    expect(planStage.findTask('design').status).toBe('completed');
    expect(planStage.findTask('tasks').status).toBe('completed');
    expect(planStage.findTask('self-review').status).toBe('completed');

    run.rerunStage(Stage.Plan);

    expect(run.status).toBe('running');
    expect(run.currentStage).toBe(Stage.Plan);
    expect(planStage.status).toBe('running');
    expect(planStage.failure).toBeNull();
    expect(planStage.approval).toBeNull();
    expect(planStage.findTask('proposal')).toMatchObject({ status: 'pending', attempts: 0 });
    expect(planStage.findTask('specs')).toMatchObject({ status: 'pending' });
    expect(planStage.findTask('design')).toMatchObject({ status: 'pending' });
    expect(planStage.findTask('tasks')).toMatchObject({ status: 'pending' });
    expect(planStage.findTask('self-review')).toMatchObject({ status: 'pending' });
  });

  it('rerun does not reset earlier passed stages', () => {
    const run = startRun();
    advanceToIntegrate(run);

    const planStage = run.stageRun(Stage.Plan);
    const buildStage = run.stageRun(Stage.Build);
    const checkStage = run.stageRun(Stage.Check);
    expect(planStage.status).toBe('passed');
    expect(buildStage.status).toBe('passed');
    expect(checkStage.status).toBe('passed');

    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'failed', reason: 'spec sync failed' });
    expect(run.currentStage).toBe(Stage.Integrate);
    expect(run.status).toBe('failed');

    run.rerunStage(Stage.Integrate);

    expect(run.currentStage).toBe(Stage.Integrate);
    expect(run.status).toBe('running');
    expect(run.stageRun(Stage.Plan).status).toBe('passed');
    expect(run.stageRun(Stage.Build).status).toBe('passed');
    expect(run.stageRun(Stage.Check).status).toBe('passed');
  });

  it('rerun resets all current-stage tasks from the first work item, not just from first incomplete', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First build task', order: 0 },
      { id: 'T-002', title: 'Second build task', order: 1, dependsOn: ['T-001'] },
    ]);
    const buildStage = run.stageRun(Stage.Build);
    buildStage.findTask('T-001').attempts = 2;
    buildStage.findTask('T-001').status = 'completed';
    buildStage.findTask('T-001').output = { text: 'done' };
    buildStage.findTask('T-002').status = 'completed';
    buildStage.findTask('T-002').attempts = 1;
    buildStage.findTask('T-002').output = { text: 'also done' };
    buildStage.findCheck('health:build').status = 'passed';

    const decision = run.rerunStage(Stage.Build);

    expect(run.status).toBe('running');
    expect(buildStage.findTask('T-001')).toMatchObject({ status: 'pending', attempts: 0, output: null, artifacts: [] });
    expect(buildStage.findTask('T-002')).toMatchObject({ status: 'pending', attempts: 0, output: null, artifacts: [] });
    expect(buildStage.findCheck('health:build')).toMatchObject({ status: 'pending', runCount: 0 });
    expect(decision.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'T-001' });
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

  it('invalidates stale review after review findings are fixed', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed', artifacts: ['ai-review'] });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });

    const firstFailure = run.recordCheckResult(Stage.Check, {
      name: 'review-passed',
      status: 'fail',
      message: 'Review failed',
      output: { verdict: 'FAIL', reviewReport: 'old report' },
    });

    expect(firstFailure.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'fix-review-findings' });
    const fix = run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed' });

    expect(fix.events).toContainEqual({
      type: 'task-invalidated',
      stage: Stage.Check,
      taskId: 'ai-review',
      reason: 'Review findings changed code; re-run AI review before rechecking',
    });
    expect(fix.events).toContainEqual({
      type: 'check-invalidated',
      stage: Stage.Check,
      checkName: 'review-passed',
      reason: 'Review findings changed code; re-run AI review before rechecking',
    });
    expect(run.stageRun(Stage.Check).findTask('ai-review')).toMatchObject({
      status: 'pending',
      attempts: 0,
      artifacts: [],
      output: null,
    });
    expect(run.stageRun(Stage.Check).findCheck('review-passed')).toMatchObject({
      status: 'pending',
      message: null,
      output: null,
      runCount: 1,
    });
    expect(run.stageRun(Stage.Check).findCheck('merge-ready')).toMatchObject({
      status: 'pending',
      message: null,
      output: null,
    });
    expect(fix.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'ai-review' });
  });

  it('retries a failed task and resets same-stage downstream work while preserving earlier completed tasks', () => {
    const run = startRun();

    completePlanTasks(run);
    passPlanChecks(run);
    run.approveStage(Stage.Plan, { output: { approved: true } });
    run.materializeTasks(Stage.Build, [
      { id: 'T-001', title: 'First build task', order: 0 },
      { id: 'T-002', title: 'Second build task', order: 1, dependsOn: ['T-001'] },
      { id: 'T-003', title: 'Third build task', order: 2, dependsOn: ['T-002'] },
    ]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.completeTask(Stage.Build, 'T-002', { status: 'failed', reason: 'compilation error' });
    expect(run.status).toBe('failed');
    expect(run.failure?.taskId).toBe('T-002');

    const retry = run.retryStage(Stage.Build);

    expect(run.status).toBe('running');
    expect(run.failure).toBeNull();
    expect(run.currentStage).toBe(Stage.Build);
    expect(run.stageRun(Stage.Build).findTask('T-001')).toMatchObject({ status: 'completed' });
    expect(run.stageRun(Stage.Build).findTask('T-002')).toMatchObject({ status: 'pending' });
    expect(run.stageRun(Stage.Build).findTask('T-003')).toMatchObject({ status: 'pending' });
    expect(retry.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'T-002' });
  });

  it('retries a failed check and resets downstream checks while preserving completed tasks', () => {
    const definitions: StageDefinition[] = [
      {
        stage: Stage.Build,
        tasks: [{ id: 'T-001', title: 'Build task', order: 0 }],
        checks: [
          { name: 'first-check', title: 'First check' },
          { name: 'second-check', title: 'Second check' },
        ],
      },
    ];
    const run = startRun(definitions);

    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'first-check', status: 'pass' });
    run.recordCheckResult(Stage.Build, { name: 'second-check', status: 'fail', message: 'second check failed' });

    expect(run.status).toBe('failed');
    expect(run.failure?.checkName).toBe('second-check');

    const retry = run.retryStage(Stage.Build);

    expect(run.status).toBe('running');
    expect(run.failure).toBeNull();
    expect(run.stageRun(Stage.Build).findTask('T-001')).toMatchObject({ status: 'completed' });
    expect(run.stageRun(Stage.Build).findCheck('first-check')).toMatchObject({ status: 'passed' });
    expect(run.stageRun(Stage.Build).findCheck('second-check')).toMatchObject({ status: 'pending' });
  });

  it('retries a failed check and resets caused-by repair tasks while preserving unrelated completed tasks', () => {
    const definitions: StageDefinition[] = [
      {
        stage: Stage.Build,
        tasks: [{ id: 'T-001', title: 'First task' }],
        checks: [
          { name: 'first-check', title: 'First check' },
          { name: 'second-check', title: 'Second check' },
        ],
        checkFailurePolicies: [
          { checkName: 'first-check', fixTaskId: 'fix-first', fixTaskTitle: 'Fix first', maxAttempts: 1 },
          { checkName: 'second-check', fixTaskId: 'fix-second', fixTaskTitle: 'Fix second', maxAttempts: 1 },
        ],
      },
    ];
    const run = startRun(definitions);

    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'first-check', status: 'pass' });
    run.recordCheckResult(Stage.Build, { name: 'second-check', status: 'fail', message: 'second check failed' });
    run.completeTask(Stage.Build, 'fix-second', { status: 'completed' });
    expect(run.status).toBe('running');

    run.recordCheckResult(Stage.Build, { name: 'second-check', status: 'fail', message: 'still failing after repair' });
    expect(run.status).toBe('failed');
    expect(run.failure?.checkName).toBe('second-check');

    const retry = run.retryStage(Stage.Build);

    expect(run.status).toBe('running');
    expect(run.failure).toBeNull();
    expect(run.stageRun(Stage.Build).findCheck('first-check')).toMatchObject({ status: 'passed' });
    expect(run.stageRun(Stage.Build).findCheck('second-check')).toMatchObject({ status: 'pending' });
  });

  it('retries a failed check stage by re-running ai-review after review findings were fixed', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed', artifacts: ['ai-review'] });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, {
      name: 'review-passed',
      status: 'fail',
      message: 'Review failed',
      output: { verdict: 'FAIL', reviewReport: 'old report' },
    });
    run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed' });

    const checkStage = run.stageRun(Stage.Check);
    checkStage.findTask('ai-review').status = 'completed';
    checkStage.findTask('ai-review').artifacts = ['ai-review'];
    const staleReview = checkStage.findCheck('review-passed');
    staleReview.status = 'failed';
    staleReview.message = 'Review failed again from stale report';
    staleReview.output = { verdict: 'FAIL', reviewReport: 'old report' };
    checkStage.status = 'failed';
    checkStage.failure = {
      reason: 'check-unrepaired',
      stage: Stage.Check,
      checkName: 'review-passed',
      message: 'Review failed again from stale report',
    };
    run.status = 'failed';
    run.failure = checkStage.failure;
    expect(run.status).toBe('failed');

    const retry = run.retryStage(Stage.Check);

    expect(run.stageRun(Stage.Check).findTask('fix-review-findings')).toMatchObject({ status: 'completed' });
    expect(run.stageRun(Stage.Check).findTask('ai-review')).toMatchObject({ status: 'pending' });
    expect(run.stageRun(Stage.Check).findCheck('review-passed')).toMatchObject({
      status: 'pending',
      message: null,
      output: null,
    });
    expect(run.stageRun(Stage.Check).findCheck('health:check')).toMatchObject({
      status: 'pending',
      message: null,
      output: null,
    });
    expect(retry.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'ai-review' });
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
    expect(run.stageRun(Stage.Plan).approval?.output).toEqual({
      result: 'PASS',
      selfReviewNotes: 'Plan self review\n<promise>PASS</promise>',
      dimensions: [{ name: 'Completeness', status: 'PASS' }],
    });
  });

  it('Check approval becomes available after review and merge checks pass', () => {
    const run = startRun();

    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } });
    const mergeReady = run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-check') });

    expect(mergeReady.nextWork).toEqual({ kind: 'await-approval', stage: Stage.Check });
    expect(run.stageRun(Stage.Check).status).toBe('awaiting-approval');
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

  it('approveStage returns the exact completion guard blocker without accepting approval', () => {
    const run = startRun();

    completePlanTasks(run);
    passPlanChecks(run);
    const planStage = run.stageRun(Stage.Plan);
    const designCheck = planStage.checks.find(check => check.name === 'design-complete');
    expect(designCheck).toBeDefined();
    designCheck!.status = 'pending';

    const decision = run.approveStage(Stage.Plan, { output: { approved: true } });

    expect(decision.nextWork).toEqual({
      kind: 'blocked',
      stage: Stage.Plan,
      reason: { complete: false, reason: 'static-check-not-passed', checkName: 'design-complete' },
    });
    expect(planStage.approval?.status).toBe('awaiting');
    expect(planStage.approval?.output).toEqual({
      result: 'PASS',
      selfReviewNotes: 'Plan self review\n<promise>PASS</promise>',
      dimensions: [{ name: 'Completeness', status: 'PASS' }],
    });
    expect(run.currentStage).toBe(Stage.Plan);
  });

  it('requests Check approval with merge-ready and verification evidence', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, {
      name: 'health:check',
      status: 'pass',
      output: {
        command: 'npm test',
        duration: 42,
        summary: 'tests passed',
        logExcerpt: 'ok',
        candidateHeadSha: 'candidate-sha',
        baseSha: 'base-sha',
      },
    });
    run.recordCheckResult(Stage.Check, {
      name: 'review-passed',
      status: 'pass',
      output: { verdict: 'PASS', reviewReport: 'PASS report', snapshotSha: 'candidate-sha' },
    });
    run.recordCheckResult(Stage.Check, {
      name: 'merge-ready',
      status: 'pass',
      output: {
        ...mergeReadyOutput('candidate-sha'),
      },
    });

    const approvalOutput = run.stageRun(Stage.Check).approval?.output as Record<string, unknown>;

    expect(run.stageRun(Stage.Check).status).toBe('awaiting-approval');
    expect(approvalOutput.mergeReadySnapshot).toMatchObject({
      kind: 'merge-ready',
      candidateHeadSha: 'candidate-sha',
      canMerge: true,
    });
    expect(approvalOutput.verificationEvidence).toMatchObject({
      checkName: 'health:check',
      candidateHeadSha: 'candidate-sha',
      baseSha: 'base-sha',
    });
  });

  it('does not request Check approval without authoritative review snapshot evidence', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, {
      name: 'health:check',
      status: 'pass',
      output: { candidateHeadSha: 'candidate-sha' },
    });
    const review = run.recordCheckResult(Stage.Check, {
      name: 'review-passed',
      status: 'pass',
      output: { verdict: 'PASS', reviewReport: 'PASS report' },
    });

    expect(run.stageRun(Stage.Check).approval).toBeNull();
    expect(review.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'check:converge-review-snapshot' });
  });

  it('does not request Check approval when review and merge evidence refer to different candidates', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, {
      name: 'health:check',
      status: 'pass',
      output: { candidateHeadSha: 'candidate-new' },
    });
    run.recordCheckResult(Stage.Check, {
      name: 'review-passed',
      status: 'pass',
      output: { verdict: 'PASS', reviewReport: 'PASS report', snapshotSha: 'candidate-old' },
    });
    const decision = run.recordCheckResult(Stage.Check, {
      name: 'merge-ready',
      status: 'pass',
      output: mergeReadyOutput('candidate-new'),
    });

    expect(run.stageRun(Stage.Check).approval).toBeNull();
    expect(decision.nextWork).toEqual({
      kind: 'blocked',
      stage: Stage.Check,
      reason: { complete: false, reason: 'check-review-evidence-stale', stage: Stage.Check },
    });
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

  it('retryStage after plan approval rejection resets completed plan tasks for rework', () => {
    const run = startRun();

    completePlanTasks(run);
    passPlanChecks(run);
    run.rejectStage(Stage.Plan, { output: 'rewrite plan around user story and domain events' });
    expect(run.status).toBe('failed');

    run.retryStage(Stage.Plan);

    const plan = run.stageRun(Stage.Plan);
    expect(run.status).toBe('running');
    expect(plan.tasks.map(task => task.status)).toEqual(['pending', 'pending', 'pending', 'pending', 'pending']);
    expect(plan.nextTask()?.id).toBe('proposal');
  });

  it('completes stages by configured order and completes the workflow after the final stage', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } });
    run.completeTask(Stage.Integrate, 'integrate:merge', { status: 'completed', output: { landedSha: 'landed' } });
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

  it('does not complete Integrate when archive delivery evidence is missing', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:merge', { status: 'completed', output: { landedSha: 'landed' } });
    const decision = run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'pass' });

    expect(run.status).toBe('running');
    expect(decision.nextWork).toEqual({
      kind: 'blocked',
      stage: Stage.Integrate,
      reason: { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:archive-change' },
    });
  });

  it('does not complete Integrate when merge delivery evidence is missing', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } });
    run.completeTask(Stage.Integrate, 'integrate:merge', { status: 'completed' });
    const decision = run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'pass' });

    expect(run.status).toBe('running');
    expect(decision.nextWork).toEqual({
      kind: 'blocked',
      stage: Stage.Integrate,
      reason: { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:merge' },
    });
  });

  it('does not complete Integrate when merge delivery only records the target branch', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } });
    run.completeTask(Stage.Integrate, 'integrate:merge', {
      status: 'completed',
      output: { targetBranch: 'main' },
    });
    const decision = run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'pass' });

    expect(run.status).toBe('running');
    expect(decision.nextWork).toEqual({
      kind: 'blocked',
      stage: Stage.Integrate,
      reason: { complete: false, reason: 'integrate-delivery-evidence-missing', stage: Stage.Integrate, taskId: 'integrate:merge' },
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

  describe('check repair exhaustion', () => {
    it('does not schedule another fix-review-findings when retrying exhausted Check review', () => {
      const run = startRun();
      advanceToBuild(run);
      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
      run.completeTask(Stage.Check, 'ai-review', { status: 'completed', artifacts: ['ai-review'] });

      run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
      run.recordCheckResult(Stage.Check, {
        name: 'review-passed',
        status: 'fail',
        message: 'Review failed first time',
        output: { verdict: 'FAIL' },
      });

      const fixTask = run.stageRun(Stage.Check).findTask('fix-review-findings');
      expect(fixTask).toBeDefined();
      expect(fixTask.status).toBe('pending');

      run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed' });

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findTask('ai-review').status = 'completed';
      checkStage.findCheck('review-passed').status = 'failed';
      checkStage.findCheck('review-passed').output = { verdict: 'FAIL', summary: 'Still failing after repair' };
      checkStage.status = 'failed';
      checkStage.failure = {
        reason: 'check-unrepaired',
        stage: Stage.Check,
        checkName: 'review-passed',
        message: 'Still failing after repair',
      };
      run.status = 'failed';
      run.failure = checkStage.failure;

      const fixTaskCountBefore = run.stageRun(Stage.Check).tasks.filter(t => t.id.startsWith('fix-review-findings')).length;

      const retry = run.retryStage(Stage.Check);

      const fixTaskCountAfter = run.stageRun(Stage.Check).tasks.filter(t => t.id.startsWith('fix-review-findings')).length;
      expect(fixTaskCountAfter).toBe(fixTaskCountBefore);
      expect(retry.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'ai-review' });
    });

    it('does not resurrect old repair tasks when rerunning Check', () => {
      const run = startRun();
      advanceToBuild(run);
      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
      run.completeTask(Stage.Check, 'ai-review', { status: 'completed', artifacts: ['ai-review'] });

      run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
      run.recordCheckResult(Stage.Check, {
        name: 'review-passed',
        status: 'fail',
        message: 'Review failed',
        output: { verdict: 'FAIL' },
      });
      run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed' });

      const rerun = run.rerunStage(Stage.Check);
      const checkStage = run.stageRun(Stage.Check);

      expect(checkStage.tasks.map(task => task.id)).not.toContain('fix-review-findings');
      expect(rerun.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'ai-review' });
    });

    it('records review-passed check result after repair completion and re-run', () => {
      const run = startRun();
      advanceToBuild(run);
      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
      run.completeTask(Stage.Check, 'ai-review', { status: 'completed', artifacts: ['ai-review'] });

      run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
      run.recordCheckResult(Stage.Check, {
        name: 'review-passed',
        status: 'fail',
        message: 'Initial review failed',
        output: { verdict: 'FAIL', summary: 'First failure' },
      });

      run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed', artifacts: ['ai-review'] });

      run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
      const decisionAfterRepair = run.recordCheckResult(Stage.Check, {
        name: 'review-passed',
        status: 'pass',
        output: { verdict: 'PASS', snapshotSha: 'abc123' },
      });

      expect(run.stageRun(Stage.Check).findCheck('review-passed').status).toBe('passed');
      expect(decisionAfterRepair.events).toContainEqual(
        expect.objectContaining({ type: 'check-recorded', checkName: 'review-passed', status: 'passed' }),
      );
    });
  });

  describe('canRetryStage predicate', () => {
    it('reports retryable when WorkflowRun is failed at its current stage', () => {
      const run = startRun();
      completePlanTasks(run);
      passPlanChecks(run);
      run.rejectStage(Stage.Plan, { output: 'needs rework' });

      expect(run.status).toBe('failed');
      expect(run.currentStage).toBe(Stage.Plan);
      expect(run.stageRun(Stage.Plan).status).toBe('failed');
      expect(run.canRetryStage(Stage.Plan)).toBe(true);
    });

    it('reports non-retryable when WorkflowRun is not failed', () => {
      const run = startRun();
      expect(run.status).toBe('running');
      expect(run.canRetryStage(Stage.Plan)).toBe(false);
    });

    it('reports non-retryable when failed WorkflowRun currentStage differs from requested stage', () => {
      const run = startRun();
      completePlanTasks(run);
      passPlanChecks(run);
      run.rejectStage(Stage.Plan, { output: 'needs rework' });
      expect(run.currentStage).toBe(Stage.Plan);

      expect(run.canRetryStage(Stage.Build)).toBe(false);
    });

    it('does not mutate the stored WorkflowRun', () => {
      const run = startRun();
      completePlanTasks(run);
      passPlanChecks(run);
      run.rejectStage(Stage.Plan, { output: 'needs rework' });

      const snapshotBefore = run.snapshot();
      run.canRetryStage(Stage.Plan);
      const snapshotAfter = run.snapshot();

      expect(snapshotBefore.status).toBe(snapshotAfter.status);
      expect(snapshotBefore.currentStage).toBe(snapshotAfter.currentStage);
      expect(snapshotBefore.stageRuns.map(s => s.status)).toEqual(snapshotAfter.stageRuns.map(s => s.status));
    });

    it('reports non-retryable for a stage that is failed but not the current stage', () => {
      const run = startRun();
      advanceToBuild(run);
      run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
      run.completeTask(Stage.Build, 'T-001', { status: 'failed', reason: 'crashed' });

      expect(run.status).toBe('failed');
      expect(run.currentStage).toBe(Stage.Build);
      expect(run.canRetryStage(Stage.Plan)).toBe(false);
    });
  });

  it('repair tasks have causedBy metadata with type check-failure', () => {
    const definitions: StageDefinition[] = [
      {
        stage: Stage.Build,
        tasks: [],
        checks: [{ name: 'health-check', title: 'Health check' }],
        repairPolicies: [
          { checkName: 'health-check', fixTaskId: 'fix-health', fixTaskTitle: 'Fix health', maxAttempts: 1 },
        ],
      },
    ];
    const run = startRun(definitions);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });

    const decision = run.recordCheckResult(Stage.Build, { name: 'health-check', status: 'fail', message: 'health degraded' });
    const fixTask = run.stageRun(Stage.Build).findTask('fix-health');

    expect(fixTask.causedBy).toEqual({ type: 'check-failure', checkName: 'health-check', message: 'health degraded' });
    expect(decision.events).toContainEqual(expect.objectContaining({ type: 'fix-task-scheduled', taskId: 'fix-health' }));
  });

  it('rebase-branch failure blocks later work through task failure semantics', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } });
    run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-check') });
    run.scheduleRebaseTask('target branch moved');

    const decision = run.completeTask(Stage.Check, 'rebase-branch', { status: 'failed', reason: 'rebase conflict' });

    expect(run.status).toBe('failed');
    expect(run.failure?.reason).toBe('task-failed');
    expect(run.failure?.taskId).toBe('rebase-branch');
    expect(decision.nextWork.kind).toBe('failed');
  });

  it('rebase-branch with shaChanged=true invalidates review-dependent state', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-old' } });
    run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-old') });
    run.scheduleRebaseTask('target branch moved');
    run.completeTask(Stage.Check, 'rebase-branch', { status: 'completed', output: { shaChanged: true, beforeBaseSha: 'a', afterBaseSha: 'b', beforeHeadSha: 'c', afterHeadSha: 'd' } });

    expect(run.stageRun(Stage.Check).findTask('ai-review').status).toBe('pending');
    expect(run.stageRun(Stage.Check).findCheck('review-passed').status).toBe('pending');
    expect(run.stageRun(Stage.Check).findCheck('merge-ready').status).toBe('pending');
  });

  it('rebase-branch invalidates review-dependent state from service-call wrapped output', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-old' } });
    run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-old') });
    run.scheduleRebaseTask('target branch moved');
    run.completeTask(Stage.Check, 'rebase-branch', {
      status: 'completed',
      output: {
        kind: 'service-call-task',
        success: true,
        result: { shaChanged: true, beforeBaseSha: 'a', afterBaseSha: 'b', beforeHeadSha: 'c', afterHeadSha: 'd' },
      },
    });

    expect(run.stageRun(Stage.Check).findTask('ai-review').status).toBe('pending');
    expect(run.stageRun(Stage.Check).findCheck('review-passed').status).toBe('pending');
    expect(run.stageRun(Stage.Check).findCheck('merge-ready').status).toBe('pending');
  });

  it('rebase-branch with shaChanged=false preserves review-dependent state', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-same' } });
    run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-same') });
    run.scheduleRebaseTask('target branch backported');
    run.completeTask(Stage.Check, 'rebase-branch', { status: 'completed', output: { shaChanged: false } });

    expect(run.stageRun(Stage.Check).findTask('ai-review').status).toBe('completed');
    expect(run.stageRun(Stage.Check).findCheck('review-passed').status).toBe('passed');
    expect(run.stageRun(Stage.Check).findCheck('merge-ready').status).toBe('passed');
  });

  it('integrate merge freezes delivery metadata from service-call wrapped output', () => {
    const run = startRun();
    advanceToIntegrate(run);

    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:merge', {
      status: 'completed',
      output: {
        kind: 'service-call-task',
        success: true,
        result: {
          targetBranch: 'main',
          baseSha: 'base-sha',
          candidateHeadSha: 'candidate-sha',
          landedSha: 'landed-sha',
          rebased: true,
        },
      },
    });

    expect(run.stageRun(Stage.Integrate).freezePoint?.delivery).toEqual({
      targetBranch: 'main',
      baseSha: 'base-sha',
      candidateHeadSha: 'candidate-sha',
      landedSha: 'landed-sha',
      rebased: true,
    });
  });

  it('rebase-branch with shaChanged=false restores awaiting approval in Plan', () => {
    const run = startRun();
    completePlanTasks(run);
    passPlanChecks(run);

    expect(run.stageRun(Stage.Plan).status).toBe('awaiting-approval');
    expect(run.stageRun(Stage.Plan).approval?.status).toBe('awaiting');

    run.scheduleRebaseTask('target branch backported');

    const decision = run.completeTask(Stage.Plan, 'rebase-branch', {
      status: 'completed',
      output: { shaChanged: false },
    });

    expect(run.stageRun(Stage.Plan).status).toBe('awaiting-approval');
    expect(run.stageRun(Stage.Plan).approval?.status).toBe('awaiting');
    expect(decision.nextWork).toEqual({ kind: 'await-approval', stage: Stage.Plan });
  });

  it('rebase-branch with shaChanged=false restores awaiting approval in Check', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-same' } });
    run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-same') });

    expect(run.stageRun(Stage.Check).status).toBe('awaiting-approval');
    expect(run.stageRun(Stage.Check).approval?.status).toBe('awaiting');

    run.scheduleRebaseTask('target branch backported');

    const decision = run.completeTask(Stage.Check, 'rebase-branch', {
      status: 'completed',
      output: { shaChanged: false },
    });

    expect(run.stageRun(Stage.Check).status).toBe('awaiting-approval');
    expect(run.stageRun(Stage.Check).approval?.status).toBe('awaiting');
    expect(decision.nextWork).toEqual({ kind: 'await-approval', stage: Stage.Check });
  });

  it('approval is not scheduled as a repair task', () => {
    const run = startRun();
    completePlanTasks(run);
    passPlanChecks(run);

    expect(run.stageRun(Stage.Plan).status).toBe('awaiting-approval');
    expect(run.stageRun(Stage.Plan).tasks.find(t => t.id === 'user-approval')).toBeUndefined();
  });

  it('updates Build work source evidence when tasks are materialized after an earlier missing source result', () => {
    const run = startRun();
    advanceToBuild(run);

    expect(run.materializeTasks(Stage.Build, [], 'missing').nextWork).toEqual({
      kind: 'check',
      stage: Stage.Build,
      checkName: 'health:build',
    });
    expect(run.stageRun(Stage.Build).buildWorkSourceState).toMatchObject({
      evaluated: true,
      missing: true,
    });

    const decision = run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);

    expect(run.stageRun(Stage.Build).buildWorkSourceState).toMatchObject({
      evaluated: true,
      tasks: [{ id: 'T-001', title: 'Build task', order: 0 }],
    });
    expect(decision.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'T-001' });
  });

  it('keeps runtime-added rebase work out of static stage definitions but required once appended', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

    const checkDefinition = DEFAULT_STAGE_DEFINITIONS.find(definition => definition.stage === Stage.Check)!;
    expect(checkDefinition.tasks.map(task => task.id)).toEqual(['ai-review']);

    run.scheduleRebaseTask('Target branch moved before review');
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });

    const rebaseTask = run.stageRun(Stage.Check).findTask('rebase-branch');
    expect(rebaseTask.causedBy).toEqual({
      type: 'branch-changed',
      message: 'Target branch moved before review',
    });
    expect(run.stageRun(Stage.Check).status).toBe('running');
    expect(run.nextWork()).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'rebase-branch' });

    run.completeTask(Stage.Check, 'rebase-branch', { status: 'completed', output: { shaChanged: false } });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } });
    const decision = run.recordCheckResult(Stage.Check, { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-check') });

    expect(decision.nextWork).toEqual({ kind: 'await-approval', stage: Stage.Check });
    expect(run.stageRun(Stage.Check).status).toBe('awaiting-approval');
  });

  it('policy-driven review invalidation uses configured reason text', () => {
    const run = startRun();
    advanceToBuild(run);
    run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 0 }]);
    run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
    run.completeTask(Stage.Check, 'ai-review', { status: 'completed', artifacts: ['ai-review'] });
    run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
    run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'fail', message: 'Review failed' });
    const fixDecision = run.completeTask(Stage.Check, 'fix-review-findings', { status: 'completed' });

    expect(run.stageRun(Stage.Check).findTask('ai-review')).toMatchObject({ status: 'pending' });
    expect(fixDecision.events).toContainEqual(expect.objectContaining({
      type: 'task-invalidated',
      stage: Stage.Check,
      taskId: 'ai-review',
      reason: 'Review findings changed code; re-run AI review before rechecking',
    }));
  });

  it('Integrate post-merge health failure remains non-repairable after merge freeze', () => {
    const run = startRun();
    advanceToIntegrate(run);
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:merge', { status: 'completed', output: { landedSha: 'sha' } });
    run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'fail', message: 'tests failed' });

    expect(run.status).toBe('failed');
    expect(run.failure?.reason).toBe('post-merge-health-failed');
    expect(run.stageRun(Stage.Integrate).tasks.some(t => t.id === 'fix-integrate-health')).toBe(false);
  });
});
