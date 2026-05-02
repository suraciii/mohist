import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage } from '../src/types';
import type { StageContext, StageRunResult, CheckResult, ReactionConfig, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo, CheckpointManager } from '../src/workflow/stage-context';
import type { Check, CheckContext } from '../src/workflow/checks';
import { EventBus } from '../src/services/event-bus';
import { BaseStageRunner } from '../src/workflow/base-stage-runner';

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

class TestStageRunner extends BaseStageRunner {
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

function makeContext(overrides?: Partial<StageContext>): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      stage: Stage.Plan,
      status: 'active' as any,
      projectId: 'proj-1',
      labels: [],
      priority: 'p2',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
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

describe('BaseStageRunner', () => {
  let ctx: StageContext;

  beforeEach(() => {
    ctx = makeContext();
  });

  describe('all-pass scenario', () => {
    it('returns success with nextStage when all checks pass', async () => {
      const runner = new TestStageRunner({
        checks: [
          new PassCheck('check-a'),
          new PassCheck('check-b'),
        ],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Build);
      expect(result.checkResults).toHaveLength(2);
      expect(result.checkResults[0].status).toBe('pass');
      expect(result.checkResults[1].status).toBe('pass');
    });

    it('executes tasks before checks', async () => {
      const order: string[] = [];
      const runner = new TestStageRunner({
        checks: [new PassCheck('check-a')],
        nextStage: Stage.Build,
        executeTasksFn: async () => {
          order.push('task');
          return { tasks: 'done' };
        },
      });

      // Override check to track order
      const origChecks = runner['checks'];
      runner['checks'] = [
        {
          name: 'check-a',
          reaction: { type: 'escalate' as const },
          run: async () => { order.push('check'); return { name: 'check-a', status: 'pass' as const }; },
        },
      ];

      await runner.run(ctx);

      expect(order).toEqual(['task', 'check']);
    });
  });

  describe('retry-task scenario', () => {
    it('re-executes tasks and re-runs checks on retry-task reaction', async () => {
      let failCount = 0;
      const flakyCheck = new FailCheck(
        'flaky',
        { type: 'retry-task', maxAttempts: 2 },
        async () => {
          failCount++;
          if (failCount <= 1) {
            return { name: 'flaky', status: 'fail', message: 'flaky failed' };
          }
          return { name: 'flaky', status: 'pass' };
        },
      );

      const runner = new TestStageRunner({
        checks: [flakyCheck],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Build);
      expect(runner.executeTasksCalls).toBe(2);
    });

    it('stops retrying after max attempts and returns failure', async () => {
      const alwaysFail = new FailCheck(
        'always-fail',
        { type: 'retry-task', maxAttempts: 2 },
      );

      const runner = new TestStageRunner({
        checks: [alwaysFail],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(runner.executeTasksCalls).toBe(3);
    });

    it('uses fallback reaction after max retries exhausted', async () => {
      const alwaysFail = new FailCheck(
        'always-fail',
        {
          type: 'retry-task',
          maxAttempts: 1,
          fallbackReaction: { type: 'escalate', escalateTarget: Stage.Draft },
        },
      );

      const runner = new TestStageRunner({
        checks: [alwaysFail],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Draft);
    });
  });

  describe('escalate scenario', () => {
    it('returns escalateToStage on escalate reaction', async () => {
      const escalateCheck = new FailCheck(
        'review',
        { type: 'escalate', escalateTarget: Stage.Plan },
      );

      const runner = new TestStageRunner({
        checks: [escalateCheck],
        nextStage: Stage.Done,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Plan);
      expect(result.checkResults).toHaveLength(1);
      expect(result.checkResults[0].status).toBe('fail');
    });

    it('skips remaining checks after first failure', async () => {
      const escalateCheck = new FailCheck(
        'first',
        { type: 'escalate', escalateTarget: Stage.Plan },
      );
      const secondCheck = new PassCheck('second');
      const secondRunSpy = vi.spyOn(secondCheck, 'run');

      const runner = new TestStageRunner({
        checks: [escalateCheck, secondCheck],
        nextStage: Stage.Done,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(secondRunSpy).not.toHaveBeenCalled();
    });
  });

  describe('ask-user scenario', () => {
    it('calls setApprovalState with awaiting status before emitting event', async () => {
      const approvalCheck = new FailCheck(
        'user-approval',
        { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan } },
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(ctx.issueRepo.setApprovalState).toHaveBeenCalledWith(
        'issue-1',
        expect.objectContaining({
          stage: Stage.Plan,
          status: 'awaiting',
        }),
      );
    });

    it('emits approval_requested event', async () => {
      const approvalCheck = new FailCheck(
        'user-approval',
        { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan } },
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
      });

      const emitSpy = vi.spyOn(ctx.eventBus, 'emit');
      await runner.run(ctx);

      expect(emitSpy).toHaveBeenCalledWith('approval_requested', {
        issueId: 'issue-1',
        projectId: 'proj-1',
        stage: Stage.Plan,
      });
    });

    it('returns success false without nextStage (pipeline pauses)', async () => {
      const approvalCheck = new FailCheck(
        'user-approval',
        { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan } },
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
      expect(result.escalateToStage).toBeUndefined();
    });

    it('calls setApprovalState before emitting approval_requested', async () => {
      const approvalCheck = new FailCheck(
        'user-approval',
        { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan } },
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
      });

      const callOrder: string[] = [];
      vi.spyOn(ctx.issueRepo, 'setApprovalState').mockImplementation(() => { callOrder.push('setApprovalState'); });
      vi.spyOn(ctx.eventBus, 'emit').mockImplementation(() => { callOrder.push('emit'); });

      await runner.run(ctx);

      expect(callOrder).toEqual(['setApprovalState', 'emit']);
    });
  });

  describe('task execution failure', () => {
    it('returns failure when executeTasks throws', async () => {
      const runner = new TestStageRunner({
        checks: [new PassCheck('check-a')],
        nextStage: Stage.Build,
        executeTasksFn: async () => { throw new Error('task boom'); },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('task boom');
      expect(result.checkResults).toEqual([]);
    });
  });

  describe('auto-fix scenario', () => {
    it('calls fix and re-runs the check on auto-fix reaction', async () => {
      let runCount = 0;
      let fixCalled = false;
      const autoFixCheck = new FailCheck(
        'build-test',
        { type: 'auto-fix', maxAttempts: 2 },
        async () => {
          runCount++;
          if (runCount <= 1) {
            return { name: 'build-test', status: 'fail', message: 'build failed' };
          }
          return { name: 'build-test', status: 'pass' };
        },
        async () => { fixCalled = true; },
      );

      const runner = new TestStageRunner({
        checks: [autoFixCheck],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Build);
      expect(fixCalled).toBe(true);
      expect(runCount).toBe(2);
    });

    it('escalates after max auto-fix attempts exhausted', async () => {
      const alwaysFailCheck = new FailCheck(
        'build-test',
        {
          type: 'auto-fix',
          maxAttempts: 2,
          fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan },
        },
        async () => ({ name: 'build-test', status: 'fail', message: 'still broken' }),
        async () => {},
      );

      const runner = new TestStageRunner({
        checks: [alwaysFailCheck],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Plan);
    });
  });

  describe('canHandle', () => {
    it('delegates to subclass implementation', () => {
      const runner = new TestStageRunner({
        checks: [],
        nextStage: Stage.Build,
        stage: Stage.Check,
      });

      expect(runner.canHandle(Stage.Check)).toBe(true);
      expect(runner.canHandle(Stage.Plan)).toBe(false);
    });
  });
});
