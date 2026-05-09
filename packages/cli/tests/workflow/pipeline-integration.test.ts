import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus } from '../../src/types';
import type { StageContext, StageRunResult, CheckResult, ReactionConfig, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo, CheckpointManager } from '../../src/workflow/stage-context';
import type { Check, CheckContext } from '../../src/workflow/checks';
import { EventBus } from '../../src/services/event-bus';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';
import { WorkflowEngine } from '../../src/workflow/workflow-engine';
import type { StageRunner } from '../../src/workflow/check-stage-runner';

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
  fixFn?: () => Promise<void>;

  constructor(name: string, reaction: ReactionConfig, runFn?: () => Promise<CheckResult>, fixFn?: () => Promise<void>) {
    this.name = name;
    this.reaction = reaction;
    this.runFn = runFn ?? (async () => ({ name: this.name, status: 'fail', message: `${this.name} failed` }));
    this.fixFn = fixFn;
  }

  async run(): Promise<CheckResult> { return this.runFn(); }
  async fix(): Promise<void> { if (this.fixFn) await this.fixFn(); }
}

class PendingCheck implements Check {
  name = 'user-approval';
  reaction: ReactionConfig;
  private status: 'pending' | 'pass';

  constructor(startPending: boolean, fallbackTarget: Stage = Stage.Plan) {
    this.status = startPending ? 'pending' : 'pass';
    this.reaction = { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: fallbackTarget } };
  }

  approve(): void { this.status = 'pass'; }

  async run(): Promise<CheckResult> {
    if (this.status === 'pass') {
      return { name: this.name, status: 'pass', message: 'User approved' };
    }
    return { name: this.name, status: 'pending', message: 'Waiting for user approval' };
  }
}

class SimpleStageRunner extends BaseStageRunner {
  private checks: Check[];
  private nextStage: Stage;
  private handledStage: Stage;
  private executeTasksFn: () => Promise<unknown>;
  executeTasksCalls = 0;

  constructor(opts: {
    checks: Check[];
    nextStage: Stage;
    stage?: Stage;
    executeTasksFn?: () => Promise<unknown>;
  }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage;
    this.handledStage = opts.stage ?? Stage.Plan;
    this.executeTasksFn = opts.executeTasksFn ?? (async () => ({ done: true }));
  }

  canHandle(s: Stage): boolean { return s === this.handledStage; }

  protected async executeTasks(): Promise<unknown> {
    this.executeTasksCalls++;
    return this.executeTasksFn();
  }

  protected getChecks(): Check[] { return this.checks; }
  protected getNextStage(): Stage { return this.nextStage; }
}

function makeIssue(overrides?: Partial<StageContext['issue']>): StageContext['issue'] {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    stage: Stage.Plan,
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
    issue: makeIssue(),
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
    } as unknown as CheckpointManager,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo,
    ...overrides,
  } as StageContext;
}

function makeEngineContext(overrides?: Partial<{
  runners: StageRunner[];
  issueRepo: IssueRepo;
  eventBus: EventBus;
  checkpointManager: CheckpointManager;
  artifactManager: ChangeArtifactsManager;
  signal?: AbortSignal;
  stageExecutionRepo?: any;
}>) {
  const issueRepo: IssueRepo = {
    updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
    setApprovalState: vi.fn(),
    clearApprovalState: vi.fn(),
    updateStatus: vi.fn(),
  } as unknown as IssueRepo;

  return {
    runners: overrides?.runners ?? [],
    issueRepo: overrides?.issueRepo ?? issueRepo,
    eventBus: overrides?.eventBus ?? new EventBus(),
    checkpointManager: overrides?.checkpointManager ?? {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
    } as unknown as CheckpointManager,
    artifactManager: overrides?.artifactManager ?? {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn(),
      writeArtifact: vi.fn(),
      exists: vi.fn(),
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn(),
    } as unknown as ChangeArtifactsManager,
    signal: overrides?.signal,
    stageExecutionRepo: overrides?.stageExecutionRepo,
  };
}

describe('Full Pipeline: Plan -> Build -> Check -> Done', () => {
  it('completes the full pipeline when all checks pass', async () => {
    const planRunner = new SimpleStageRunner({
      checks: [
        new PassCheck('proposal-complete'),
        new PassCheck('specs-complete'),
        new PassCheck('design-complete'),
        new PassCheck('tasks-valid'),
        new PassCheck('self-review-passed'),
        new PassCheck('user-approval'),
      ],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const buildRunner = new SimpleStageRunner({
      checks: [
        new PassCheck('all-tasks-complete'),
        new PassCheck('code-compiles'),
      ],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const checkRunner = new SimpleStageRunner({
      checks: [
        new PassCheck('build-test'),
        new PassCheck('ai-review'),
        new PassCheck('user-approval'),
      ],
      nextStage: Stage.Integrate,
      stage: Stage.Check,
    });

    const integrateRunner = new SimpleStageRunner({
      checks: [],
      nextStage: Stage.Done,
      stage: Stage.Integrate,
    });

    const ctx = makeEngineContext({
      runners: [planRunner, buildRunner, checkRunner, integrateRunner],
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Plan });

    const result = await engine.run(issue, {} as any);

    expect(result.completed).toBe(true);
    expect(result.stage).toBe(Stage.Done);
    expect(ctx.issueRepo.updateStage).toHaveBeenCalledWith(expect.any(String), Stage.Build);
    expect(ctx.issueRepo.updateStage).toHaveBeenCalledWith(expect.any(String), Stage.Check);
    expect(ctx.issueRepo.updateStage).toHaveBeenCalledWith(expect.any(String), Stage.Integrate);
  });

  it('Plan stage has no requiresApproval in result', async () => {
    const planRunner = new SimpleStageRunner({
      checks: [new PassCheck('proposal-complete'), new PassCheck('user-approval')],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext();
    const result = await planRunner.run(ctx);

    expect(result).not.toHaveProperty('requiresApproval');
    expect(result.success).toBe(true);
    expect(result.nextStage).toBe(Stage.Build);
  });
});

describe('Escalation Paths', () => {
  it('CHECK build-test failure with auto-fix exhausted escalates to BUILD', async () => {
    const alwaysFailCheck = new FailCheck(
      'build-test',
      {
        type: 'auto-fix',
        maxAttempts: 2,
        fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build },
      },
      async () => ({ name: 'build-test', status: 'fail', message: 'build failed' }),
      async () => {},
    );

    const checkRunner = new SimpleStageRunner({
      checks: [alwaysFailCheck, new PassCheck('ai-review'), new PassCheck('user-approval')],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const buildRunner = new SimpleStageRunner({
      checks: [new PassCheck('all-tasks-complete'), new PassCheck('code-compiles')],
      nextStage: Stage.Done,
      stage: Stage.Build,
    });

    const issueRepo: IssueRepo = {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo;

    const ctx = makeEngineContext({
      runners: [checkRunner, buildRunner],
      issueRepo,
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Check });

    const result = await engine.run(issue, {} as any);

    expect(result.completed).toBe(true);
    expect(ctx.issueRepo.updateStage).toHaveBeenCalledWith(expect.any(String), Stage.Build);
  });

  it('CHECK ai-review failure auto-fixes before falling back to BUILD', async () => {
    let fixCalled = false;
    const buildRunner = new SimpleStageRunner({
      checks: [new PassCheck('all-tasks-complete'), new PassCheck('code-compiles')],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const aiReviewFailCheck = new FailCheck(
      'ai-review',
      { type: 'auto-fix', maxAttempts: 1, fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build } },
      undefined,
      async () => { fixCalled = true; },
    );

    const checkRunner = new SimpleStageRunner({
      checks: [new PassCheck('build-test'), aiReviewFailCheck, new PassCheck('user-approval')],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const issueRepo: IssueRepo = {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo;

    const ctx = makeEngineContext({
      runners: [buildRunner, checkRunner],
      issueRepo,
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Check });

    const result = await engine.run(issue, {} as any);

    expect(result.completed).toBe(false);
    expect(fixCalled).toBe(true);
    expect(ctx.issueRepo.updateStage).toHaveBeenCalledWith(expect.any(String), Stage.Build);
  });

  it('BUILD tasks-complete failure escalates to PLAN', async () => {
    const planRunner = new SimpleStageRunner({
      checks: [new PassCheck('proposal-complete'), new PassCheck('user-approval')],
      nextStage: Stage.Done,
      stage: Stage.Plan,
    });

    const tasksFailCheck = new FailCheck(
      'all-tasks-complete',
      {
        type: 'retry-task',
        maxAttempts: 1,
        fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan },
      },
    );

    const buildRunner = new SimpleStageRunner({
      checks: [tasksFailCheck, new PassCheck('code-compiles')],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const issueRepo: IssueRepo = {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo;

    const ctx = makeEngineContext({
      runners: [planRunner, buildRunner],
      issueRepo,
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Build });

    const result = await engine.run(issue, {} as any);

    expect(result.completed).toBe(true);
    expect(ctx.issueRepo.updateStage).toHaveBeenCalledWith(expect.any(String), Stage.Plan);
  });
});

describe('User-Approval Check', () => {
  it('pauses pipeline and resumes on approval', async () => {
    const approvalCheck = new PendingCheck(true, Stage.Plan);

    const planRunner = new SimpleStageRunner({
      checks: [new PassCheck('proposal-complete'), approvalCheck],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const buildRunner = new SimpleStageRunner({
      checks: [new PassCheck('all-tasks-complete'), new PassCheck('code-compiles')],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const checkRunner = new SimpleStageRunner({
      checks: [new PassCheck('build-test'), new PassCheck('ai-review'), new PassCheck('user-approval')],
      nextStage: Stage.Integrate,
      stage: Stage.Check,
    });

    const integrateRunner = new SimpleStageRunner({
      checks: [],
      nextStage: Stage.Done,
      stage: Stage.Integrate,
    });

    const issueRepo: IssueRepo = {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo;

    const eventBus = new EventBus();

    const ctx = makeEngineContext({
      runners: [planRunner, buildRunner, checkRunner, integrateRunner],
      issueRepo,
      eventBus,
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Plan });

    const result = await engine.run(issue, {} as any);

    expect(result.completed).toBe(false);
    expect(ctx.issueRepo.setApprovalState).toHaveBeenCalledWith(
      expect.any(String),
      expect.objectContaining({ status: 'awaiting' }),
    );

    approvalCheck.approve();

    const issueAfterApproval = makeIssue({
      stage: Stage.Plan,
      approvalState: { stage: Stage.Plan, status: 'approved', output: null, requestedAt: new Date().toISOString() },
    });

    const result2 = await engine.run(issueAfterApproval, {} as any);

    expect(result2.completed).toBe(true);
    expect(result2.stage).toBe(Stage.Done);
  });

  it('emits approval_requested event when user-approval check is pending', async () => {
    const approvalCheck = new PendingCheck(true, Stage.Plan);

    const planRunner = new SimpleStageRunner({
      checks: [new PassCheck('proposal-complete'), approvalCheck],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const eventBus = new EventBus();
    const emitSpy = vi.spyOn(eventBus, 'emit');
    const ctx = makeContext({ eventBus });

    await planRunner.run(ctx);

    expect(emitSpy).toHaveBeenCalledWith('approval_requested', {
      issueId: 'issue-1',
      projectId: 'proj-1',
      stage: Stage.Plan,
    });
  });
});

describe('Retry-Task Reaction', () => {
  it('stops after max retries and falls back to escalate', async () => {
    const alwaysFailCheck = new FailCheck(
      'self-review-passed',
      {
        type: 'retry-task',
        maxAttempts: 3,
        fallbackReaction: { type: 'escalate', escalateTarget: Stage.Draft },
      },
    );

    const runner = new SimpleStageRunner({
      checks: [
        new PassCheck('proposal-complete'),
        new PassCheck('specs-complete'),
        alwaysFailCheck,
      ],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.escalateToStage).toBe(Stage.Draft);
    expect(runner.executeTasksCalls).toBe(4);
  });

  it('retries and succeeds before max retries', async () => {
    let failCount = 0;
    const flakyCheck = new FailCheck(
      'self-review-passed',
      { type: 'retry-task', maxAttempts: 3 },
      async () => {
        failCount++;
        if (failCount <= 2) {
          return { name: 'self-review-passed', status: 'fail', message: 'failed' };
        }
        return { name: 'self-review-passed', status: 'pass' };
      },
    );

    const runner = new SimpleStageRunner({
      checks: [
        new PassCheck('proposal-complete'),
        flakyCheck,
      ],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result.success).toBe(true);
    expect(result.nextStage).toBe(Stage.Build);
    expect(runner.executeTasksCalls).toBe(3);
  });
});

describe('Auto-Fix Reaction', () => {
  it('falls back to escalate after max auto-fix attempts', async () => {
    const alwaysFailCheck = new FailCheck(
      'build-test',
      {
        type: 'auto-fix',
        maxAttempts: 2,
        fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build },
      },
      async () => ({ name: 'build-test', status: 'fail', message: 'still broken' }),
      async () => {},
    );

    const runner = new SimpleStageRunner({
      checks: [alwaysFailCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(result.escalateToStage).toBe(Stage.Build);
  });

  it('succeeds after auto-fix resolves the issue', async () => {
    let runCount = 0;
    let fixCalled = false;

    const autoFixCheck = new FailCheck(
      'code-compiles',
      { type: 'auto-fix', maxAttempts: 2 },
      async () => {
        runCount++;
        if (runCount <= 1) {
          return { name: 'code-compiles', status: 'fail', message: 'compile error' };
        }
        return { name: 'code-compiles', status: 'pass' };
      },
      async () => { fixCalled = true; },
    );

    const runner = new SimpleStageRunner({
      checks: [autoFixCheck],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result.success).toBe(true);
    expect(result.nextStage).toBe(Stage.Check);
    expect(fixCalled).toBe(true);
  });
});

describe('Non-OpenSpec Issue (no openspec/changes/)', () => {
  it('still completes full pipeline with simplified checks', async () => {
    const planRunner = new SimpleStageRunner({
      checks: [new PassCheck('user-approval')],
      nextStage: Stage.Build,
      stage: Stage.Plan,
      executeTasksFn: async () => ({ simplified: true }),
    });

    const buildRunner = new SimpleStageRunner({
      checks: [new PassCheck('all-tasks-complete')],
      nextStage: Stage.Check,
      stage: Stage.Build,
      executeTasksFn: async () => ({ simplified: true }),
    });

    const checkRunner = new SimpleStageRunner({
      checks: [new PassCheck('build-test'), new PassCheck('user-approval')],
      nextStage: Stage.Integrate,
      stage: Stage.Check,
      executeTasksFn: async () => ({ simplified: true }),
    });

    const integrateRunner = new SimpleStageRunner({
      checks: [],
      nextStage: Stage.Done,
      stage: Stage.Integrate,
      executeTasksFn: async () => ({ simplified: true }),
    });

    const issueRepo: IssueRepo = {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo;

    const ctx = makeEngineContext({
      runners: [planRunner, buildRunner, checkRunner, integrateRunner],
      issueRepo,
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Plan });

    const result = await engine.run(issue, {} as any);

    expect(result.completed).toBe(true);
    expect(result.stage).toBe(Stage.Done);
    expect(planRunner.executeTasksCalls).toBe(1);
    expect(buildRunner.executeTasksCalls).toBe(1);
    expect(checkRunner.executeTasksCalls).toBe(1);
    expect(integrateRunner.executeTasksCalls).toBe(1);
  });
});

describe('StageRunResult has no gate fields', () => {
  it('successful result contains no requiresApproval or gateRequired', async () => {
    const runner = new SimpleStageRunner({
      checks: [new PassCheck('check-a')],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result).not.toHaveProperty('requiresApproval');
    expect(result).not.toHaveProperty('gateRequired');
    expect(result.success).toBe(true);
    expect(result.nextStage).toBe(Stage.Build);
    expect(result.checkResults).toBeDefined();
    expect(result.checkResults).toHaveLength(1);
  });

  it('failed result with escalation contains no gate fields', async () => {
    const failCheck = new FailCheck(
      'failing',
      { type: 'escalate', escalateTarget: Stage.Plan },
    );

    const runner = new SimpleStageRunner({
      checks: [failCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result).not.toHaveProperty('requiresApproval');
    expect(result).not.toHaveProperty('gateRequired');
    expect(result.success).toBe(false);
    expect(result.escalateToStage).toBe(Stage.Plan);
    expect(result.checkResults).toHaveLength(1);
  });
});

describe('WorkflowEngine no gate logic', () => {
  it('PipelineResult has no gateRequired field', async () => {
    const planRunner = new SimpleStageRunner({
      checks: [new PassCheck('check-a')],
      nextStage: Stage.Done,
      stage: Stage.Plan,
    });

    const issueRepo: IssueRepo = {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo;

    const ctx = makeEngineContext({
      runners: [planRunner],
      issueRepo,
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Plan });

    const result = await engine.run(issue, {} as any);

    expect(result).not.toHaveProperty('gateRequired');
    expect(result.completed).toBe(true);
  });

  it('handles escalation in engine loop', async () => {
    const failCheck = new FailCheck(
      'review',
      { type: 'escalate', escalateTarget: Stage.Plan },
    );

    const checkRunner = new SimpleStageRunner({
      checks: [failCheck],
      nextStage: Stage.Done,
      stage: Stage.Check,
    });

    const planRunner = new SimpleStageRunner({
      checks: [new PassCheck('check-a')],
      nextStage: Stage.Done,
      stage: Stage.Plan,
    });

    const issueRepo: IssueRepo = {
      updateStage: vi.fn().mockImplementation((id: string, stage: Stage) => makeIssue({ stage })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as IssueRepo;

    const ctx = makeEngineContext({
      runners: [checkRunner, planRunner],
      issueRepo,
    });

    const engine = new WorkflowEngine(ctx);
    const issue = makeIssue({ stage: Stage.Check });

    const result = await engine.run(issue, {} as any);

    expect(ctx.issueRepo.updateStage).toHaveBeenCalledWith(expect.any(String), Stage.Plan);
    expect(result.completed).toBe(true);
  });
});

describe('Serial check execution', () => {
  it('stops executing checks after first failure', async () => {
    const failCheck = new FailCheck(
      'specs-complete',
      { type: 'escalate', escalateTarget: Stage.Draft },
    );
    const thirdCheck = new PassCheck('design-complete');
    const thirdRunSpy = vi.spyOn(thirdCheck, 'run');

    const runner = new SimpleStageRunner({
      checks: [
        new PassCheck('proposal-complete'),
        failCheck,
        thirdCheck,
      ],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result.success).toBe(false);
    expect(thirdRunSpy).not.toHaveBeenCalled();
    expect(result.checkResults).toHaveLength(2);
  });

  it('all checks run when all pass', async () => {
    const check1 = new PassCheck('proposal-complete');
    const check2 = new PassCheck('specs-complete');
    const check3 = new PassCheck('design-complete');

    const spy1 = vi.spyOn(check1, 'run');
    const spy2 = vi.spyOn(check2, 'run');
    const spy3 = vi.spyOn(check3, 'run');

    const runner = new SimpleStageRunner({
      checks: [check1, check2, check3],
      nextStage: Stage.Build,
      stage: Stage.Plan,
    });

    const ctx = makeContext();
    const result = await runner.run(ctx);

    expect(result.success).toBe(true);
    expect(spy1).toHaveBeenCalled();
    expect(spy2).toHaveBeenCalled();
    expect(spy3).toHaveBeenCalled();
    expect(result.checkResults).toHaveLength(3);
  });
});

describe('Build stage has no user-approval check', () => {
  it('Build stage auto-advances to Check without user-approval', async () => {
    const buildRunner = new SimpleStageRunner({
      checks: [
        new PassCheck('all-tasks-complete'),
        new PassCheck('code-compiles'),
      ],
      nextStage: Stage.Check,
      stage: Stage.Build,
    });

    const ctx = makeContext({ issue: makeIssue({ stage: Stage.Build }) });
    const result = await buildRunner.run(ctx);

    expect(result.success).toBe(true);
    expect(result.nextStage).toBe(Stage.Check);
    expect(result.checkResults.every(r => r.name !== 'user-approval')).toBe(true);
    expect(ctx.issueRepo.setApprovalState).not.toHaveBeenCalled();
  });
});
