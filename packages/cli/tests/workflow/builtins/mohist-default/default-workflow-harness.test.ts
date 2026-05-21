import { afterEach, describe, expect, it } from 'vitest';
import { IssueStatus, MergeState, Stage } from '../../../../src/types';
import { DefaultWorkflowHarness, type DefaultWorkflowScenario } from './harness';

describe('default workflow external-system harness', () => {
  let harnesses: DefaultWorkflowHarness[] = [];

  afterEach(() => {
    for (const harness of harnesses) harness.cleanup();
    harnesses = [];
  });

  function createHarness(scenario: DefaultWorkflowScenario = {}): DefaultWorkflowHarness {
    const harness = new DefaultWorkflowHarness(scenario);
    harnesses.push(harness);
    return harness;
  }

  it('runs the default workflow from Plan to Done through fake external systems', async () => {
    const harness = createHarness();

    const planBoundary = await harness.runUntilBoundary();
    expect(planBoundary).toMatchObject({ completed: false, stage: Stage.Plan, message: 'Awaiting plan approval' });
    expect(harness.world.agentCalls).toEqual(['proposal', 'specs', 'design', 'tasks', 'self-review']);
    expect(harness.workflowRunService.getLatestRunForIssue(harness.issue.id)?.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)?.approvalStatus).toBe('awaiting');

    harness.approve(Stage.Plan);
    const checkBoundary = await harness.runUntilBoundary();
    expect(checkBoundary).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });
    expect(harness.world.taskCalls).toEqual(expect.arrayContaining(['T-001', 'T-002', 'ai-review']));
    expect(harness.world.checkCalls).toEqual(expect.arrayContaining(['health:build', 'health:check']));

    harness.approve(Stage.Check);
    const done = await harness.runUntilBoundary();
    expect(done).toEqual({ completed: true, stage: Stage.Integrate, message: 'Pipeline completed' });
    expect(harness.world.checkCalls).toContain('health:integrate');

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    expect(latest.status).toBe('passed');
    expect(latest.stageRuns.map(stageRun => [stageRun.stage, stageRun.status])).toEqual([
      [Stage.Plan, 'passed'],
      [Stage.Build, 'passed'],
      [Stage.Check, 'passed'],
      [Stage.Integrate, 'passed'],
    ]);
    expect(harness.issueRepo.findById(harness.issue.id)).toMatchObject({
      stage: Stage.Done,
      status: IssueStatus.Completed,
      mergeState: MergeState.Merged,
    });
    expect(harness.world.serviceCalls).toEqual([
      'integrate:spec-sync',
      'integrate:archive-change',
      'integrate:merge',
    ]);
  });

  it('exercises review failure, auto-fix, code.changed reset, and re-review before Check approval', async () => {
    const harness = createHarness({ reviewFailuresBeforePass: 1 });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    const checkBoundary = await harness.runUntilBoundary();
    expect(checkBoundary).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });
    expect(harness.world.agentCalls.filter(taskId => taskId === 'ai-review')).toHaveLength(2);
    expect(harness.world.agentCalls).toContain('fix-review-findings');

    const checkRun = stageRun(harness, Stage.Check);
    expect(checkRun.tasks.filter(task => task.taskId === 'ai-review')).toHaveLength(1);
    expect(checkRun.tasks.find(task => task.taskId === 'ai-review')).toMatchObject({ status: 'completed', attempts: 1 });
    expect(checkRun.tasks.find(task => task.taskId === 'fix-review-findings')).toMatchObject({
      status: 'completed',
      events: ['code.changed'],
    });
    const reviewPassed = checkRun.checks.find(check => check.checkName === 'review-passed');
    expect(reviewPassed).toMatchObject({ status: 'passed' });
    expect(reviewPassed?.output).toMatchObject({
      structuredResult: {
        verdict: 'PASS',
        repairedItemIds: ['F-001'],
        verification: [
          expect.objectContaining({
            checkName: 'repair:F-001',
            command: 'npm test -- tests/workflow/builtins/workflows/mohist-default/default-workflow-harness.test.ts',
            status: 'pass',
          }),
        ],
      },
    });

    harness.approve(Stage.Check);
    expect((await harness.runUntilBoundary()).completed).toBe(true);
    expect(harness.issueRepo.findById(harness.issue.id)).toMatchObject({
      stage: Stage.Done,
      status: IssueStatus.Completed,
    });
  });

  it('runs two review repair cycles before reaching Check approval when the default retry limit allows it', async () => {
    const harness = createHarness({ reviewFailuresBeforePass: 2 });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    const checkBoundary = await harness.runUntilBoundary();
    expect(checkBoundary).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });

    const checkRun = stageRun(harness, Stage.Check);
    expect(harness.world.agentCalls.filter(taskId => taskId === 'ai-review')).toHaveLength(3);
    expect(checkRun.tasks.find(task => task.taskId === 'ai-review')).toMatchObject({ status: 'completed', attempts: 1 });
    expect(checkRun.tasks.filter(task => baseRuntimeTaskId(task.taskId) === 'fix-review-findings')).toHaveLength(2);
    expect(checkRun.checks.find(check => check.checkName === 'review-passed')).toMatchObject({ status: 'passed' });
  });

  it('fails Check after the default review repair attempts are exhausted', async () => {
    const harness = createHarness({ reviewFailuresBeforePass: 3 });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    const failed = await harness.runUntilBoundary();
    expect(failed.completed).toBe(false);
    expect(failed.stage).toBe(Stage.Check);
    expect(failed.message).toContain('<promise>PASS</promise> not found');

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    const checkRun = stageRun(harness, Stage.Check);
    expect(latest.status).toBe('failed');
    expect(checkRun.status).toBe('failed');
    expect(harness.world.agentCalls.filter(taskId => taskId === 'ai-review')).toHaveLength(3);
    expect(checkRun.tasks.filter(task => baseRuntimeTaskId(task.taskId) === 'fix-review-findings')).toHaveLength(2);
    expect(checkRun.checks.find(check => check.checkName === 'review-passed')).toMatchObject({ status: 'failed' });
    expect(harness.issueRepo.findById(harness.issue.id)).toMatchObject({
      stage: Stage.Check,
      status: IssueStatus.Blocked,
    });
  });

  it('does not enter Build until Plan approval is recorded', async () => {
    const harness = createHarness();

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Plan, message: 'Awaiting plan approval' });
    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Plan, message: 'Awaiting plan approval' });
    expect(harness.world.taskCalls).toEqual(['proposal', 'specs', 'design', 'tasks', 'self-review']);
    expect(harness.issueRepo.findById(harness.issue.id)).toMatchObject({
      stage: Stage.Plan,
      status: IssueStatus.Active,
    });
  });

  it('repairs a Build health failure from the default Build check policy before entering Check', async () => {
    const harness = createHarness({ healthFailuresBeforePass: { 'health:build': 1 } });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });

    const buildRun = stageRun(harness, Stage.Build);
    expect(buildRun.status).toBe('passed');
    expect(buildRun.tasks.find(task => task.taskId === 'fix-build-health')).toMatchObject({ status: 'completed' });
    expect(buildRun.checks.find(check => check.checkName === 'health:build')).toMatchObject({ status: 'passed' });
    expect(harness.world.agentCalls).toContain('fix-build-health');
    expect(harness.world.checkCalls.filter(checkName => checkName === 'health:build')).toHaveLength(2);
  });

  it('fails when a default repair task fails instead of continuing the stage', async () => {
    const harness = createHarness({
      healthFailuresBeforePass: { 'health:build': 1 },
      failAgentTasks: { 'fix-build-health': 'Unable to repair build health' },
    });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Build, message: 'Unable to repair build health' });

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    const buildRun = stageRun(harness, Stage.Build);
    expect(latest.status).toBe('failed');
    expect(buildRun.status).toBe('failed');
    expect(buildRun.tasks.find(task => task.taskId === 'fix-build-health')).toMatchObject({
      status: 'failed',
      reason: 'Unable to repair build health',
    });
    expect(buildRun.checks.find(check => check.checkName === 'health:build')).toMatchObject({ status: 'pending' });
    expect(harness.issueRepo.findById(harness.issue.id)).toMatchObject({
      stage: Stage.Build,
      status: IssueStatus.Blocked,
    });
  });

  it('repairs failed Plan self-review, emits plan.changed, and reruns self-review before Plan approval', async () => {
    const harness = createHarness({ markerFailuresBeforePass: { 'self-review-passed': 1 } });

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Plan, message: 'Awaiting plan approval' });

    const planRun = stageRun(harness, Stage.Plan);
    expect(planRun.status).toBe('awaiting-approval');
    expect(harness.world.agentCalls.filter(taskId => taskId === 'self-review')).toHaveLength(2);
    expect(planRun.tasks.find(task => task.taskId === 'self-review')).toMatchObject({ status: 'completed', attempts: 1 });
    expect(planRun.tasks.find(task => task.taskId === 'fix-plan-review')).toMatchObject({
      status: 'completed',
      events: ['plan.changed'],
    });
    expect(planRun.checks.find(check => check.checkName === 'self-review-passed')).toMatchObject({ status: 'passed' });
    expect(planRun.approvalStatus).toBe('awaiting');
  });

  it('blocks Plan approval when a required Plan artifact is missing', async () => {
    const harness = createHarness({ omitArtifacts: ['proposal.md'] });

    const failed = await harness.runUntilBoundary();
    expect(failed.completed).toBe(false);
    expect(failed.stage).toBe(Stage.Plan);
    expect(failed.message).toContain('proposal.md');

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    const planRun = stageRun(harness, Stage.Plan);
    expect(latest.status).toBe('failed');
    expect(planRun.status).toBe('failed');
    expect(planRun.checks.find(check => check.checkName === 'proposal-complete')).toMatchObject({ status: 'failed' });
    expect(planRun.approvalStatus).toBeNull();
  });

  it('stops Build when an OpenSpec materialized agent task fails and does not run downstream task or Build health check', async () => {
    const harness = createHarness({ failAgentTasks: { 'T-001': 'Implementation failed' } });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Build, message: 'Implementation failed' });

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    const buildRun = stageRun(harness, Stage.Build);
    expect(latest.status).toBe('failed');
    expect(buildRun.status).toBe('failed');
    expect(buildRun.tasks.find(task => task.taskId === 'T-001')).toMatchObject({
      status: 'failed',
      reason: 'Implementation failed',
    });
    expect(buildRun.tasks.find(task => task.taskId === 'T-002')).toMatchObject({ status: 'pending' });
    expect(harness.world.checkCalls).not.toContain('health:build');
  });

  it('respects Build task dependencies from tasks.json before running Build health', async () => {
    const harness = createHarness();

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Check);

    const t1Index = harness.world.taskCalls.indexOf('T-001');
    const t2Index = harness.world.taskCalls.indexOf('T-002');
    const healthIndex = harness.world.checkCalls.indexOf('health:build');
    expect(t1Index).toBeGreaterThanOrEqual(0);
    expect(t2Index).toBeGreaterThan(t1Index);
    expect(healthIndex).toBeGreaterThanOrEqual(0);
    expect(harness.world.taskCalls.indexOf('ai-review')).toBeGreaterThan(t2Index);
  });

  it('repairs Check health failure, raises code.changed, reruns AI review, and clears stale checks', async () => {
    const harness = createHarness({ healthFailuresBeforePass: { 'health:check': 1 } });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });

    const checkRun = stageRun(harness, Stage.Check);
    expect(harness.world.agentCalls.filter(taskId => taskId === 'ai-review')).toHaveLength(2);
    expect(checkRun.tasks.find(task => task.taskId === 'fix-check-health')).toMatchObject({
      status: 'completed',
      events: ['code.changed'],
    });
    expect(checkRun.checks.find(check => check.checkName === 'health:check')).toMatchObject({ status: 'passed' });
    expect(checkRun.checks.find(check => check.checkName === 'review-passed')).toMatchObject({ status: 'passed' });
  });

  it('repairs merge readiness and reruns merge-ready before Check approval', async () => {
    const harness = createHarness({ mergeReadyFailuresBeforePass: 1 });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });

    const checkRun = stageRun(harness, Stage.Check);
    expect(checkRun.tasks.find(task => task.taskId === 'fix-merge-readiness')).toMatchObject({ status: 'completed' });
    expect(checkRun.checks.find(check => check.checkName === 'merge-ready')).toMatchObject({ status: 'passed' });
  });

  it('invalidates Check approval when merge-readiness repair raises code.changed', async () => {
    const harness = createHarness({
      mergeReadyFailuresBeforePass: 1,
      mergeReadinessRepairRaisesCodeChanged: true,
    });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Check, message: 'Awaiting check approval' });

    const checkRun = stageRun(harness, Stage.Check);
    expect(harness.world.agentCalls.filter(taskId => taskId === 'ai-review')).toHaveLength(2);
    expect(checkRun.tasks.find(task => task.taskId === 'fix-merge-readiness')).toMatchObject({
      status: 'completed',
      events: ['code.changed'],
    });
    expect(checkRun.approvalStatus).toBe('awaiting');
  });

  it('stops Integrate when spec sync fails and does not run archive or merge', async () => {
    const harness = createHarness({ failServices: { 'integrate:spec-sync': 'Spec sync failed' } });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);
    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Check);
    harness.approve(Stage.Check);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Integrate, message: 'Spec sync failed' });

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    const integrateRun = stageRun(harness, Stage.Integrate);
    expect(latest.status).toBe('failed');
    expect(integrateRun.status).toBe('failed');
    expect(harness.world.serviceCalls).toEqual(['integrate:spec-sync']);
    expect(integrateRun.tasks.find(task => task.taskId === 'integrate:archive-change')).toMatchObject({ status: 'pending' });
    expect(integrateRun.tasks.find(task => task.taskId === 'integrate:merge')).toMatchObject({ status: 'pending' });
  });

  it('fails post-delivery Integrate health without rerunning delivery side-effect tasks', async () => {
    const harness = createHarness({ healthFailuresBeforePass: { 'health:integrate': 1 } });

    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Plan);
    harness.approve(Stage.Plan);
    expect((await harness.runUntilBoundary()).stage).toBe(Stage.Check);
    harness.approve(Stage.Check);

    expect(await harness.runUntilBoundary()).toMatchObject({ completed: false, stage: Stage.Integrate, message: 'health:integrate failed' });

    const latest = harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!;
    const integrateRun = stageRun(harness, Stage.Integrate);
    expect(latest.status).toBe('failed');
    expect(integrateRun.status).toBe('failed');
    expect(harness.world.serviceCalls).toEqual([
      'integrate:spec-sync',
      'integrate:archive-change',
      'integrate:merge',
    ]);
    expect(integrateRun.tasks.some(task => task.taskId === 'fix-integrate-health')).toBe(false);
    expect(harness.world.checkCalls.filter(checkName => checkName === 'health:integrate')).toHaveLength(1);
    expect(integrateRun.checks.find(check => check.checkName === 'health:integrate')).toMatchObject({ status: 'failed' });
  });
});

function stageRun(harness: DefaultWorkflowHarness, stage: Stage) {
  return harness.workflowRunService.getLatestRunForIssue(harness.issue.id)!.stageRuns.find(stageRun => stageRun.stage === stage)!;
}

function baseRuntimeTaskId(taskId: string): string {
  return taskId.replace(/:\d+$/, '');
}
