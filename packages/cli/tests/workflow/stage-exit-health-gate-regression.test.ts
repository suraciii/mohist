import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../../src/types';
import type { CheckResult, CheckContext, ReactionConfig } from '../../src/workflow/checks';
import type { StageContext, ChangeArtifactsManager, CheckpointManager, IssueRepo, ProjectRepo, WorktreeManager } from '../../src/workflow/stage-context';
import { EventBus } from '../../src/services/event-bus';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';
import type { Check } from '../../src/workflow/checks';
import { loadHealthGatePolicies } from '../../src/workflow/workflow-loader';
import { UserApprovalCheck } from '../../src/workflow/checks/user-approval-check';

function makeIssue(overrides: Partial<import('../../src/types').Issue> = {}): import('../../src/types').Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    stage: Stage.Check,
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function makeContext(overrides?: Partial<StageContext>): StageContext {
  return {
    issue: makeIssue(overrides?.issue ? { stage: overrides.issue.stage } : {}),
    acpOptions: {} as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn(),
      writeArtifact: vi.fn(),
      exists: vi.fn(),
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn(),
    } as unknown as ChangeArtifactsManager,
    worktreeManager: {} as WorktreeManager,
    projectRepo: {} as ProjectRepo,
    eventBus: new EventBus() as any,
    checkpointManager: {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
      getResumeSteps: vi.fn().mockReturnValue([]),
      markStepComplete: vi.fn(),
      delete: vi.fn(),
    } as unknown as CheckpointManager,
    issueRepo: {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
      findById: vi.fn(),
    } as unknown as IssueRepo,
    ...overrides,
  } as StageContext;
}

class PassCheck implements Check {
  name: string;
  reaction: ReactionConfig = { type: 'escalate' };
  constructor(name: string) { this.name = name; }
  async run(): Promise<CheckResult> { return { name: this.name, status: 'pass' }; }
}

class FailCheck implements Check {
  name: string;
  reaction: ReactionConfig;
  runFn: () => Promise<CheckResult>;
  constructor(name: string, reaction: ReactionConfig, runFn?: () => Promise<CheckResult>) {
    this.name = name;
    this.reaction = reaction;
    this.runFn = runFn ?? (async () => ({ name: this.name, status: 'fail', message: `${this.name} failed` }));
  }
  async run(): Promise<CheckResult> { return this.runFn(); }
}

class HealthGateCheckRunner extends BaseStageRunner {
  private checks: Check[];
  private nextStage: Stage;
  private handledStage: Stage;
  executeTasksCalls = 0;

  constructor(opts: { checks: Check[]; nextStage: Stage; stage?: Stage }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage;
    this.handledStage = opts.stage ?? Stage.Check;
  }

  canHandle(s: Stage): boolean { return s === this.handledStage; }

  protected async executeTasks(): Promise<unknown> {
    this.executeTasksCalls++;
    return { done: true };
  }

  protected getChecks(): Check[] { return this.checks; }
  protected getNextStage(): Stage { return this.nextStage; }
}

describe('Check-stage approval requires health:check pass', () => {
  it('UserApprovalCheck is NOT reached when health:check fails', async () => {
    const healthGateFailsCheck = new FailCheck(
      'health:check',
      { type: 'escalate', escalateTarget: Stage.Build },
      async () => ({ name: 'health:check', status: 'fail', message: 'health:check failed' }),
    );
    const aiReviewCheck = new PassCheck('ai-review');
    const userApprovalCheck = new PassCheck('user-approval');

    const runner = new HealthGateCheckRunner({
      checks: [healthGateFailsCheck, aiReviewCheck, userApprovalCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Check }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.checkResults).toHaveLength(1);
    expect(result.checkResults[0].name).toBe('health:check');
    expect(result.checkResults[0].status).toBe('fail');
    expect(ctx.issueRepo.setApprovalState).not.toHaveBeenCalled();
  });

  it('UserApprovalCheck IS reached only after health:check passes', async () => {
    const healthGatePassCheck = new PassCheck('health:check');
    const aiReviewCheck = new PassCheck('ai-review');
    const userApprovalCheck = new UserApprovalCheck(Stage.Check);

    const runner = new HealthGateCheckRunner({
      checks: [healthGatePassCheck, aiReviewCheck, userApprovalCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Check }) });
    const result = await runner.run(ctx);

    expect(result.checkResults).toHaveLength(3);
    expect(result.checkResults.map(r => r.name)).toEqual(['health:check', 'ai-review', 'user-approval']);
    expect(result.checkResults[2].name).toBe('user-approval');
    expect(result.checkResults[2].status).toBe('pending');
  });

  it('health:check pass followed by all checks pass results in pending approval', async () => {
    const healthGatePassCheck = new PassCheck('health:check');
    const aiReviewCheck = new PassCheck('ai-review');
    const userApprovalCheck = new UserApprovalCheck(Stage.Check);

    const runner = new HealthGateCheckRunner({
      checks: [healthGatePassCheck, aiReviewCheck, userApprovalCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Check }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.checkResults).toHaveLength(3);
    expect(result.checkResults[2].status).toBe('pending');
    expect(ctx.issueRepo.setApprovalState).toHaveBeenCalled();
  });

  it('health:check failure blocks check-stage even when AI review would pass', async () => {
    const healthGateFailsCheck = new FailCheck(
      'health:check',
      { type: 'escalate', escalateTarget: Stage.Build },
    );
    const aiReviewCheck = new PassCheck('ai-review');

    const runner = new HealthGateCheckRunner({
      checks: [healthGateFailsCheck, aiReviewCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Check }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.checkResults).toHaveLength(1);
    expect(result.checkResults[0].name).toBe('health:check');
    expect(ctx.issueRepo.setApprovalState).not.toHaveBeenCalled();
  });
});

describe('Build-stage completion requires health:build pass', () => {
  it('Build stage does not advance when health:build fails', async () => {
    const allTasksCompleteCheck = new PassCheck('all-tasks-complete');
    const healthGateFailsCheck = new FailCheck(
      'health:build',
      { type: 'escalate', escalateTarget: Stage.Plan },
    );

    const runner = new HealthGateCheckRunner({
      checks: [allTasksCompleteCheck, healthGateFailsCheck],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Build }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.checkResults).toHaveLength(2);
    expect(result.checkResults[1].name).toBe('health:build');
    expect(result.checkResults[1].status).toBe('fail');
    expect(result.escalateToStage).toBe(Stage.Plan);
  });

  it('Build stage produces success with nextStage=Check when health:build passes', async () => {
    const allTasksCompleteCheck = new PassCheck('all-tasks-complete');
    const healthGatePassCheck = new PassCheck('health:build');

    const runner = new HealthGateCheckRunner({
      checks: [allTasksCompleteCheck, healthGatePassCheck],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Build }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(true);
    expect(result.checkResults).toHaveLength(2);
    expect(result.nextStage).toBe(Stage.Check);
  });

  it('health:build failure escalates to plan as configured', async () => {
    const allTasksCompleteCheck = new PassCheck('all-tasks-complete');
    const healthGateFailsCheck = new FailCheck(
      'health:build',
      { type: 'escalate', escalateTarget: Stage.Plan },
    );

    const runner = new HealthGateCheckRunner({
      checks: [allTasksCompleteCheck, healthGateFailsCheck],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Build }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.escalateToStage).toBe(Stage.Plan);
  });
});

describe('Direct merge API cannot mark issue done when health:postMerge fails', () => {
  it('postMergeFinalizer returns failure result when enabled gate fails', async () => {
    const execFileMock = vi.fn();
    execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
      const err = new Error('Command failed');
      (err as any).code = 1;
      (err as any).stdout = '';
      (err as any).stderr = 'build failed\nerror details';
      process.nextTick(() => {
        if (typeof opts === 'function') {
          opts(err, { stdout: '', stderr: 'build failed\nerror details' });
        } else if (typeof cb === 'function') {
          cb(err, { stdout: '', stderr: 'build failed\nerror details' });
        }
      });
      return { stdout: '', stderr: 'build failed' } as any;
    });

    vi.doMock('child_process', async () => ({
      ...await vi.importActual<typeof import('child_process')>('child_process'),
      execFile: execFileMock,
    }));

    const { PostMergeFinalizer } = await import('../../src/services/post-merge-finalizer');

    const issueRepo = {
      updateStage: vi.fn(),
      updateStatus: vi.fn(),
      clearApprovalState: vi.fn(),
      setMergeState: vi.fn(),
      updateBlockedReason: vi.fn(),
      findById: vi.fn().mockReturnValue(null),
    } as unknown as import('../../src/db/issue-repo').IssueRepo;

    const projectRepo = {
      findById: vi.fn().mockReturnValue({ id: 'proj-1', path: '/tmp/test-project', name: 'test', baseBranch: 'main' }),
    } as unknown as import('../../src/db/project-repo').ProjectRepo;

    const stageExecutionRepo = {
      findActiveByIssueId: vi.fn().mockReturnValue(null),
      updateCheckResults: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as import('../../src/db/stage-execution-repo').StageExecutionRepo;

    const eventBus = new EventBus();

    const finalizer = new PostMergeFinalizer(issueRepo, projectRepo, stageExecutionRepo, eventBus as any);

    const issue = makeIssue({ stage: Stage.Check, mergeState: MergeState.Merged });

    const result = await finalizer.finalize(issue);

    expect(result.success).toBe(false);
    expect(result.healthGateResult).toBeDefined();
    expect(result.healthGateResult!.enabled).toBe(true);
    expect(result.healthGateResult!.passed).toBe(false);
  });

  it('postMergeFinalizer does not mark issue done when health gate fails', async () => {
    const execFileMock = vi.fn();
    execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
      const err = new Error('Command failed');
      (err as any).code = 1;
      (err as any).stdout = '';
      (err as any).stderr = 'error';
      process.nextTick(() => {
        if (typeof opts === 'function') {
          opts(err, { stdout: '', stderr: 'error' });
        } else if (typeof cb === 'function') {
          cb(err, { stdout: '', stderr: 'error' });
        }
      });
      return { stdout: '', stderr: 'error' } as any;
    });

    vi.doMock('child_process', async () => ({
      ...await vi.importActual<typeof import('child_process')>('child_process'),
      execFile: execFileMock,
    }));

    const { PostMergeFinalizer } = await import('../../src/services/post-merge-finalizer');

    const issueRepo = {
      updateStage: vi.fn(),
      updateStatus: vi.fn(),
      clearApprovalState: vi.fn(),
      setMergeState: vi.fn(),
      updateBlockedReason: vi.fn(),
      findById: vi.fn().mockReturnValue(null),
    } as unknown as import('../../src/db/issue-repo').IssueRepo;

    const projectRepo = {
      findById: vi.fn().mockReturnValue({ id: 'proj-1', path: '/tmp/test-project', name: 'test', baseBranch: 'main' }),
    } as unknown as import('../../src/db/project-repo').ProjectRepo;

    const stageExecutionRepo = {
      findActiveByIssueId: vi.fn().mockReturnValue(null),
      updateCheckResults: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as import('../../src/db/stage-execution-repo').StageExecutionRepo;

    const eventBus = new EventBus();

    const finalizer = new PostMergeFinalizer(issueRepo, projectRepo, stageExecutionRepo, eventBus as any);

    const issue = makeIssue({ stage: Stage.Check, mergeState: MergeState.Merged });

    await finalizer.finalize(issue);

    expect(issueRepo.updateStage).not.toHaveBeenCalled();
    expect(issueRepo.updateStatus).not.toHaveBeenCalled();
  });
});

describe('checks.buildTest-only workflow config controls check-stage health command', () => {
  it('checks.buildTest maps to check health gate when no healthGates.check is present', () => {
    const workflow = {
      stages: [{ stage: 'check' }],
      checks: {
        buildTest: {
          command: 'npm run ci-test',
          timeout: 600000,
          autoFix: true,
          maxFixAttempts: 3,
        },
      },
    } as any;

    const policies = loadHealthGatePolicies(workflow);

    expect(policies.check.command).toBe('npm run ci-test');
    expect(policies.check.timeout).toBe(600000);
    expect(policies.check.autoFix).toBe(true);
    expect(policies.check.maxFixAttempts).toBe(3);
  });

  it('explicit healthGates.check takes precedence over checks.buildTest', () => {
    const workflow = {
      stages: [{ stage: 'check' }],
      checks: {
        buildTest: {
          command: 'npm run ci-test',
        },
      },
      healthGates: {
        check: {
          command: 'npm run health-check',
          autoFix: false,
        },
      },
    } as any;

    const policies = loadHealthGatePolicies(workflow);

    expect(policies.check.command).toBe('npm run health-check');
    expect(policies.check.autoFix).toBe(false);
    expect(policies.check.timeout).toBe(300000);
  });

  it('checks.buildTest does not affect plan gate', () => {
    const workflow = {
      stages: [{ stage: 'plan' }],
      checks: {
        buildTest: {
          command: 'npm run ci-test',
        },
      },
    } as any;

    const policies = loadHealthGatePolicies(workflow);

    expect(policies.plan.command).toBe('npm run typecheck');
    expect(policies.plan.command).not.toBe('npm run ci-test');
  });

  it('checks.buildTest does not affect build gate', () => {
    const workflow = {
      stages: [{ stage: 'build' }],
      checks: {
        buildTest: {
          command: 'npm run ci-test',
        },
      },
    } as any;

    const policies = loadHealthGatePolicies(workflow);

    expect(policies.build.command).toBe('npm run build');
    expect(policies.build.command).not.toBe('npm run ci-test');
  });
});

describe('Disabled gates are recorded as disabled policy results and do not block progression', () => {
  it('Disabled health gate returns pass result with enabled=false', async () => {
    const disabledHealthGateCheck = new PassCheck('health:plan');
    disabledHealthGateCheck.run = async () => ({
      name: 'health:plan',
      status: 'pass',
      output: {
        kind: 'health-gate',
        stage: 'plan',
        command: 'npm run typecheck',
        enabled: false,
      },
    });

    const runner = new HealthGateCheckRunner({
      checks: [disabledHealthGateCheck],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Plan }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(true);
    expect(result.checkResults).toHaveLength(1);
    expect(result.checkResults[0].name).toBe('health:plan');
    expect((result.checkResults[0].output as any)?.enabled).toBe(false);
  });

  it('Disabled health gate does not block stage progression', async () => {
    const runner = new HealthGateCheckRunner({
      checks: [new PassCheck('health:build')],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Build }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(true);
    expect(result.nextStage).toBe(Stage.Check);
  });
});

describe('Stage exit health guarantee integration', () => {
  it('Check stage check order is health:check before user-approval', async () => {
    const healthCheck = new PassCheck('health:check');
    const aiReviewCheck = new PassCheck('ai-review');
    const userApprovalCheck = new PassCheck('user-approval');

    const runner = new HealthGateCheckRunner({
      checks: [healthCheck, aiReviewCheck, userApprovalCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Check }) });
    const result = await runner.run(ctx);

    expect(result.checkResults.map(r => r.name)).toEqual(['health:check', 'ai-review', 'user-approval']);
  });

  it('Plan stage check order is health:plan before user-approval', async () => {
    const proposalCheck = new PassCheck('proposal-complete');
    const selfReviewCheck = new PassCheck('self-review-passed');
    const healthPlanCheck = new PassCheck('health:plan');
    const userApprovalCheck = new PassCheck('user-approval');

    const runner = new HealthGateCheckRunner({
      checks: [proposalCheck, selfReviewCheck, healthPlanCheck, userApprovalCheck],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Plan }) });
    const result = await runner.run(ctx);

    expect(result.checkResults.map(r => r.name)).toEqual([
      'proposal-complete',
      'self-review-passed',
      'health:plan',
      'user-approval',
    ]);
  });

  it('Build stage health gate runs after all tasks complete check', async () => {
    const allTasksCompleteCheck = new PassCheck('all-tasks-complete');
    const healthBuildCheck = new PassCheck('health:build');

    const runner = new HealthGateCheckRunner({
      checks: [allTasksCompleteCheck, healthBuildCheck],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Build }) });
    const result = await runner.run(ctx);

    expect(result.checkResults.map(r => r.name)).toEqual(['all-tasks-complete', 'health:build']);
  });

  it('Failing health gate prevents later checks from running', async () => {
    const healthCheck = new FailCheck('health:check', { type: 'escalate', escalateTarget: Stage.Build });
    const aiReviewCheck = new PassCheck('ai-review');
    const userApprovalCheck = new PassCheck('user-approval');

    const aiReviewRun = vi.spyOn(aiReviewCheck, 'run');
    const userApprovalRun = vi.spyOn(userApprovalCheck, 'run');

    const runner = new HealthGateCheckRunner({
      checks: [healthCheck, aiReviewCheck, userApprovalCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Check }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.checkResults).toHaveLength(1);
    expect(aiReviewRun).not.toHaveBeenCalled();
    expect(userApprovalRun).not.toHaveBeenCalled();
  });

  it('All checks run when all pass including health gates', async () => {
    const allTasksComplete = new PassCheck('all-tasks-complete');
    const healthBuild = new PassCheck('health:build');
    const healthCheck = new PassCheck('health:check');
    const aiReview = new PassCheck('ai-review');
    const userApproval = new PassCheck('user-approval');

    const runner = new HealthGateCheckRunner({
      checks: [allTasksComplete, healthBuild, healthCheck, aiReview, userApproval],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Check }) });
    const result = await runner.run(ctx);

    expect(result.success).toBe(true);
    expect(result.checkResults).toHaveLength(5);
    expect(result.checkResults.map(r => r.name)).toEqual([
      'all-tasks-complete',
      'health:build',
      'health:check',
      'ai-review',
      'user-approval',
    ]);
  });
});