import { describe, expect, it, vi } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../src/types';
import { WorkflowRun, type WorkflowRunSnapshot } from '../src/workflow/domain';
import { BaseStageRunner } from '../src/workflow/base-stage-runner';
import type { Check } from '../src/workflow/checks';
import type { StageContext, StageTaskResult } from '../src/workflow/stage-context';

type IntegrateOutcome = 'pass' | 'spec-sync-fail' | 'archive-fail' | 'merge-fail' | 'health-fail';

function makeIssue(): Issue {
  return {
    id: 'issue-1',
    number: 188,
    title: 'Integrate WorkflowRun',
    body: '',
    stage: Stage.Integrate,
    status: IssueStatus.Active,
    projectId: 'project-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

function advanceToIntegrate(): WorkflowRun {
  const { run } = WorkflowRun.startWorkflow({ id: 'run-1', issueId: 'issue-1', issueNumber: 188 });
  for (const task of run.stageRun(Stage.Plan).tasks.map(task => task.id)) {
    run.completeTask(Stage.Plan, task, { status: 'completed' });
  }
  for (const check of run.stageRun(Stage.Plan).checks.map(check => check.name)) {
    run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
  }
  run.approveStage(Stage.Plan);
  run.materializeTasks(Stage.Build, [{ id: 'T-001', title: 'Build task', order: 1 }]);
  run.completeTask(Stage.Build, 'T-001', { status: 'completed' });
  run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });
  run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
  run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
  run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } });
  run.recordCheckResult(Stage.Check, {
    name: 'merge-ready',
    status: 'pass',
    output: {
      kind: 'merge-ready',
      targetBranch: 'main',
      strategy: 'squash',
      baseSha: 'base-sha',
      candidateHeadSha: 'sha-check',
      mergeBaseSha: 'base-sha',
      canMerge: true,
      conflictFiles: [],
      checkedAt: '2026-05-15T00:00:00.000Z',
    },
  });
  run.approveStage(Stage.Check);
  return run;
}

function makeService(run: WorkflowRun) {
  return {
    startWorkflow: vi.fn(),
    resumeDecision: vi.fn(() => ({ run, nextWork: run.nextWork() })),
    materializeTasks: vi.fn(({ stage, tasks }) => ({ run, decision: run.materializeTasks(stage, tasks) })),
    completeTask: vi.fn(({ stage, taskId, result }) => ({ run, decision: run.completeTask(stage, taskId, result) })),
    recordCheckResult: vi.fn(({ stage, result }) => ({ run, decision: run.recordCheckResult(stage, result) })),
    approveStage: vi.fn(({ stage, approval }) => ({ run, decision: run.approveStage(stage, approval) })),
    retryStage: vi.fn(({ stage }) => ({ run, decision: run.retryStage(stage) })),
  };
}

class AggregateIntegrateRunner extends BaseStageRunner {
  readonly executed: string[] = [];

  constructor(private readonly outcome: IntegrateOutcome) {
    super();
  }

  canHandle(stage: Stage): boolean {
    return stage === Stage.Integrate;
  }

  protected async executeTasks(): Promise<unknown> {
    return null;
  }

  protected getChecks(): Check[] {
    return [
      {
        name: 'health:integrate',
        run: async () => {
          this.executed.push('health:integrate');
          if (this.outcome === 'health-fail') {
            return { name: 'health:integrate', status: 'fail', message: 'post merge health failed', output: { command: 'npm run build' } };
          }
          return { name: 'health:integrate', status: 'pass', output: { command: 'npm run build' } };
        },
      } as Check,
    ];
  }

  protected override async executeReportedTask(_ctx: StageContext, taskId: string): Promise<StageTaskResult | null> {
    this.executed.push(taskId);
    if (taskId === 'integrate:spec-sync') {
      return this.result(taskId, this.outcome === 'spec-sync-fail' ? 'failed' : 'completed', {
        step: 'integrate:spec-sync',
        capabilities: ['workflow-run'],
        counts: { added: 1, modified: 0, removed: 0, renamed: 0 },
        targetFiles: ['openspec/specs/workflow-run/spec.md'],
        valid: this.outcome !== 'spec-sync-fail',
        error: this.outcome === 'spec-sync-fail' ? 'spec sync failed' : undefined,
      });
    }
    if (taskId === 'integrate:archive-change') {
      return this.result(taskId, this.outcome === 'archive-fail' ? 'failed' : 'completed', {
        step: 'integrate:archive-change',
        archivePath: 'openspec/changes/archive/2026-05-12-188-workflowrun',
        success: this.outcome !== 'archive-fail',
        error: this.outcome === 'archive-fail' ? 'archive failed' : undefined,
      });
    }
    if (taskId === 'integrate:merge') {
      return this.result(taskId, this.outcome === 'merge-fail' ? 'failed' : 'completed', {
        step: 'integrate:merge',
        targetBranch: 'main',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-sha',
        landedSha: this.outcome === 'merge-fail' ? undefined : 'landed-sha',
        rebased: true,
        error: this.outcome === 'merge-fail' ? 'merge failed' : undefined,
      });
    }
    return null;
  }

  protected getNextStage(): Stage {
    return Stage.Done;
  }

  private result(taskId: string, status: 'completed' | 'failed', output: Record<string, unknown>): StageTaskResult {
    return {
      taskId,
      title: taskId,
      status,
      artifacts: [],
      output,
      attempts: 1,
      duration: 1,
      reason: typeof output.error === 'string' ? output.error : undefined,
    };
  }
}

async function runAggregateIntegrate(outcome: IntegrateOutcome): Promise<{ run: WorkflowRun; runner: AggregateIntegrateRunner }> {
  const run = advanceToIntegrate();
  const service = makeService(run);
  const runner = new AggregateIntegrateRunner(outcome);
  const issue = makeIssue();

  while (true) {
    const work = run.nextWork();
    if (work.kind === 'complete' || work.kind === 'failed' || work.kind === 'await-approval') break;
    await runner.run({
      issue,
      acpOptions: {},
      artifactManager: { getChangeDir: vi.fn(), createChangeDir: vi.fn(), readArtifact: vi.fn(), writeArtifact: vi.fn(), exists: vi.fn(), readTasks: vi.fn(), updateTaskPasses: vi.fn(), syncTasksToStageState: vi.fn(), archiveChange: vi.fn() } as never,
      worktreeManager: {} as never,
      projectRepo: {} as never,
      eventBus: { emit: vi.fn() } as never,
      checkpointManager: {} as never,
      issueRepo: { updateStage: vi.fn(), setApprovalState: vi.fn(), clearApprovalState: vi.fn(), updateStatus: vi.fn(), findById: vi.fn(), setMergeState: vi.fn() } as never,
      workflowApplicationService: service,
      requestedWork: work,
    });
  }

  return { run, runner };
}

function integrateSnapshot(run: WorkflowRun): WorkflowRunSnapshot['stageRuns'][number] {
  return run.snapshot().stageRuns.find(stage => stage.stage === Stage.Integrate)!;
}

describe('Integrate WorkflowRun aggregate delivery', () => {
  it('stops after spec-sync failure and does not run archive, merge, or health', async () => {
    const { run, runner } = await runAggregateIntegrate('spec-sync-fail');

    expect(runner.executed).toEqual(['integrate:spec-sync']);
    expect(run.failure).toMatchObject({ reason: 'task-failed', stage: Stage.Integrate, taskId: 'integrate:spec-sync' });
    expect(integrateSnapshot(run).tasks.map(task => [task.id, task.status])).toEqual([
      ['integrate:spec-sync', 'failed'],
      ['integrate:archive-change', 'pending'],
      ['integrate:merge', 'pending'],
    ]);
  });

  it('stops after archive failure and does not run merge or health', async () => {
    const { run, runner } = await runAggregateIntegrate('archive-fail');

    expect(runner.executed).toEqual(['integrate:spec-sync', 'integrate:archive-change']);
    expect(run.failure).toMatchObject({ reason: 'task-failed', stage: Stage.Integrate, taskId: 'integrate:archive-change' });
    expect(integrateSnapshot(run).tasks.find(task => task.id === 'integrate:merge')?.status).toBe('pending');
  });

  it('stops after merge failure and does not run health', async () => {
    const { run, runner } = await runAggregateIntegrate('merge-fail');

    expect(runner.executed).toEqual(['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge']);
    expect(run.failure).toMatchObject({ reason: 'task-failed', stage: Stage.Integrate, taskId: 'integrate:merge' });
    expect(integrateSnapshot(run).checks.find(check => check.name === 'health:integrate')?.status).toBe('pending');
  });

  it('records merge delivery metadata, freezes, and exposes landed sha through task output', async () => {
    const { run, runner } = await runAggregateIntegrate('pass');
    const integrate = integrateSnapshot(run);
    const mergeTask = integrate.tasks.find(task => task.id === 'integrate:merge')!;

    expect(runner.executed).toEqual(['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge', 'health:integrate']);
    expect(mergeTask.output).toMatchObject({
      targetBranch: 'main',
      baseSha: 'base-sha',
      candidateHeadSha: 'candidate-sha',
      landedSha: 'landed-sha',
      rebased: true,
    });
    expect(integrate.freezePoint).toMatchObject({
      taskId: 'integrate:merge',
      delivery: {
        targetBranch: 'main',
        baseSha: 'base-sha',
        candidateHeadSha: 'candidate-sha',
        landedSha: 'landed-sha',
        rebased: true,
      },
    });
    expect(run.status).toBe('passed');
  });

  it('records health pass as a check after freeze', async () => {
    const { run } = await runAggregateIntegrate('pass');
    const health = integrateSnapshot(run).checks.find(check => check.name === 'health:integrate')!;

    expect(health.status).toBe('passed');
    expect(health.output).toEqual({ command: 'npm run build' });
  });

  it('turns post-delivery check failure into manual intervention and never schedules fix-integrate-health', async () => {
    const { run, runner } = await runAggregateIntegrate('health-fail');
    const integrate = integrateSnapshot(run);

    expect(runner.executed).toEqual(['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge', 'health:integrate']);
    expect(run.status).toBe('failed');
    expect(run.failure).toMatchObject({ reason: 'post-delivery-check-failed', stage: Stage.Integrate, checkName: 'health:integrate' });
    expect(integrate.freezePoint?.delivery.landedSha).toBe('landed-sha');
    expect(integrate.tasks.map(task => task.id)).not.toContain('fix-integrate-health');
    expect(integrate.checks.find(check => check.name === 'health:integrate')?.status).toBe('failed');
  });

  it('does not schedule automatic code-modifying tasks after freeze', () => {
    const run = advanceToIntegrate();
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } });
    run.completeTask(Stage.Integrate, 'integrate:merge', { status: 'completed', output: { landedSha: 'landed-sha' } });
    run.recordCheckResult(Stage.Integrate, { name: 'health:integrate', status: 'fail', message: 'health failed' });

    expect(run.stageRun(Stage.Integrate).freezePoint).not.toBeNull();
    expect(run.stageRun(Stage.Integrate).tasks.map(task => task.id)).toEqual([
      'integrate:spec-sync',
      'integrate:archive-change',
      'integrate:merge',
    ]);
  });

  it('accepts already-merged task output when recovered delivery evidence includes landed sha', () => {
    const run = advanceToIntegrate();
    run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
    run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } });
    const decision = run.completeTask(Stage.Integrate, 'integrate:merge', {
      status: 'completed',
      output: { targetBranch: 'main', landedSha: 'landed-sha', skipped: true, reason: MergeState.Merged },
    });

    const mergeTask = integrateSnapshot(run).tasks.find(task => task.id === 'integrate:merge')!;
    expect(mergeTask.output).toMatchObject({ targetBranch: 'main', landedSha: 'landed-sha', skipped: true, reason: MergeState.Merged });
    expect(mergeTask.status).toBe('completed');
    expect(run.stageRun(Stage.Integrate).freezePoint?.delivery.landedSha).toBe('landed-sha');
    expect(decision.nextWork).toEqual({ kind: 'check', stage: Stage.Integrate, checkName: 'health:integrate' });
  });
});
