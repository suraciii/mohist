import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage } from '../src/types';
import type { StageContext, StageRunResult, CheckResult, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo, CheckpointManager, CheckFailurePolicy, StageTaskResult } from '../src/workflow/stage-context';
import type { Check, CheckContext } from '../src/workflow/checks';
import { EventBus } from '../src/services/event-bus';
import { BaseStageRunner } from '../src/workflow/base-stage-runner';

class PassCheck implements Check {
  name: string;
  constructor(name: string) { this.name = name; }
  async run(): Promise<CheckResult> { return { name: this.name, status: 'pass' }; }
}

class FailCheck implements Check {
  name: string;
  runFn: () => Promise<CheckResult>;

  constructor(name: string, _reaction?: unknown, runFn?: () => Promise<CheckResult>) {
    this.name = name;
    this.runFn = runFn ?? (async () => ({ name: this.name, status: 'fail', message: `${this.name} failed` }));
  }

  async run(): Promise<CheckResult> { return this.runFn(); }
}

class TestStageRunner extends BaseStageRunner {
  private checks: Check[];
  private nextStage: Stage;
  private handledStage: Stage;
  private executeTasksFn: () => Promise<unknown>;
  private failurePolicies: CheckFailurePolicy[];
  private fixTaskFn: (taskId: string, failedCheck: CheckResult, attempt: number) => Promise<StageTaskResult | null>;
  private approvalCheckNames: Set<string>;
  executeTasksCalls = 0;

  constructor(opts: {
    checks: Check[];
    nextStage: Stage;
    stage?: Stage;
    executeTasksFn?: () => Promise<unknown>;
    failurePolicies?: CheckFailurePolicy[];
    fixTaskFn?: (taskId: string, failedCheck: CheckResult, attempt: number) => Promise<StageTaskResult | null>;
    approvalCheckNames?: string[];
  }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage;
    this.handledStage = opts.stage ?? Stage.Plan;
    this.executeTasksFn = opts.executeTasksFn ?? (async () => ({ done: true }));
    this.failurePolicies = opts.failurePolicies ?? [];
    this.fixTaskFn = opts.fixTaskFn ?? (async () => null);
    this.approvalCheckNames = new Set(opts.approvalCheckNames ?? []);
  }

  canHandle(s: Stage): boolean { return s === this.handledStage; }

  protected isApprovalCheck(checkName: string): boolean {
    return this.approvalCheckNames.has(checkName);
  }

  protected getCheckFailurePolicies(): CheckFailurePolicy[] {
    return this.failurePolicies;
  }

  protected async runFixTask(
    ctx: StageContext,
    taskId: string,
    failedCheck: CheckResult,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    return this.fixTaskFn(taskId, failedCheck, attempt);
  }

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
    stageStateService: {
      ensureStage: vi.fn(),
      upsertTask: vi.fn(),
      upsertCheck: vi.fn(),
      setApproval: vi.fn(),
      setStageStatus: vi.fn(),
    } as any,
    ...overrides,
  } as StageContext;
}

describe('BaseStageRunner', () => {
  let ctx: StageContext;

  beforeEach(() => {
    ctx = makeContext();
  });

  describe('all-pass scenario', () => {
    it('returns success without deciding the next stage when all checks pass', async () => {
      const runner = new TestStageRunner({
        checks: [
          new PassCheck('check-a'),
          new PassCheck('check-b'),
        ],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result).not.toHaveProperty('nextStage');
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

      runner['checks'] = [
        {
          name: 'check-a',
          run: async () => { order.push('check'); return { name: 'check-a', status: 'pass' as const }; },
        },
      ];

      await runner.run(ctx);

      expect(order).toEqual(['task', 'check']);
    });
  });

  describe('failed check without policy', () => {
    it('fails stage without escalation when check fails and no policy exists', async () => {
      const failCheck = new FailCheck('always-fail');

      const runner = new TestStageRunner({
        checks: [failCheck],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBeUndefined();
      expect(result.checkResults).toHaveLength(1);
      expect(result.checkResults[0].status).toBe('fail');
    });

    it('collects remaining checks after first failure', async () => {
      const failCheck = new FailCheck('first');
      const secondCheck = new PassCheck('second');
      const secondRunSpy = vi.spyOn(secondCheck, 'run');

      const runner = new TestStageRunner({
        checks: [failCheck, secondCheck],
        nextStage: Stage.Done,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(secondRunSpy).toHaveBeenCalledTimes(1);
      expect(result.checkResults.map(r => r.name)).toEqual(['first', 'second']);
    });

    it('does not re-execute tasks when check fails', async () => {
      const failCheck = new FailCheck('flaky');

      const runner = new TestStageRunner({
        checks: [failCheck],
        nextStage: Stage.Build,
      });

      await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(1);
    });
  });

  describe('policy-driven fix task', () => {
    it('runs fix task and re-runs check when policy exists', async () => {
      let runCount = 0;
      let fixCalled = false;
      const flakyCheck = new FailCheck(
        'build-test',
        undefined,
        async () => {
          runCount++;
          if (runCount <= 1) {
            return { name: 'build-test', status: 'fail', message: 'build failed' };
          }
          return { name: 'build-test', status: 'pass' };
        },
      );

      const runner = new TestStageRunner({
        checks: [flakyCheck],
        nextStage: Stage.Build,
        failurePolicies: [{ checkName: 'build-test', fixTaskId: 'fix-build', maxAttempts: 2 }],
        fixTaskFn: async () => {
          fixCalled = true;
          return { taskId: 'fix-build', title: 'Fix build', status: 'completed', artifacts: [], attempts: 1, duration: 100 };
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result).not.toHaveProperty('nextStage');
      expect(fixCalled).toBe(true);
      expect(runCount).toBe(2);
    });

    it('fails after max fix attempts exhausted', async () => {
      let fixCount = 0;
      const alwaysFail = new FailCheck(
        'build-test',
        undefined,
        async () => ({ name: 'build-test', status: 'fail', message: 'still broken' }),
      );

      const runner = new TestStageRunner({
        checks: [alwaysFail],
        nextStage: Stage.Build,
        failurePolicies: [{ checkName: 'build-test', fixTaskId: 'fix-build', maxAttempts: 2 }],
        fixTaskFn: async () => {
          fixCount++;
          return { taskId: 'fix-build', title: 'Fix build', status: 'completed', artifacts: [], attempts: fixCount, duration: 100 };
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBeUndefined();
      expect(fixCount).toBe(2);
    });

    it('does not call executeTasks when running fix task', async () => {
      const failCheck = new FailCheck('build-test');

      const runner = new TestStageRunner({
        checks: [failCheck],
        nextStage: Stage.Build,
        failurePolicies: [{ checkName: 'build-test', fixTaskId: 'fix-build', maxAttempts: 1 }],
        fixTaskFn: async () => {
          return { taskId: 'fix-build', title: 'Fix build', status: 'completed', artifacts: [], attempts: 1, duration: 100 };
        },
      });

      await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(1);
    });

    it('stops immediately when fix task fails', async () => {
      let checkRuns = 0;
      const failCheck = new FailCheck(
        'health:build',
        undefined,
        async () => {
          checkRuns++;
          return { name: 'health:build', status: 'fail', message: 'build failed' };
        },
      );

      const runner = new TestStageRunner({
        checks: [failCheck],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 2 }],
        fixTaskFn: async () => ({
          taskId: 'fix-build-health',
          title: 'Fix build health',
          status: 'failed',
          artifacts: [],
          attempts: 1,
          duration: 100,
          output: { error: 'agent failed' },
        }),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Fix build health failed');
      expect(checkRuns).toBe(1);
    });
  });

  describe('ask-user / approval scenario', () => {
    it('calls setApprovalState with awaiting status when approval check returns pending', async () => {
      const approvalCheck = new FailCheck(
        'user-approval',
        undefined,
        async () => ({ name: 'user-approval', status: 'pending', message: 'Waiting for approval' }),
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
        approvalCheckNames: ['user-approval'],
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
      expect(ctx.stageStateService!.setApproval).toHaveBeenCalledWith(
        'issue-1',
        Stage.Plan,
        expect.objectContaining({
          status: 'awaiting',
        }),
      );
    });

    it('emits approval_requested event', async () => {
      const approvalCheck = new FailCheck(
        'user-approval',
        undefined,
        async () => ({ name: 'user-approval', status: 'pending', message: 'Waiting for approval' }),
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
        approvalCheckNames: ['user-approval'],
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
        undefined,
        async () => ({ name: 'user-approval', status: 'pending', message: 'Waiting for approval' }),
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
      expect(result.escalateToStage).toBeUndefined();
    });

    it('calls setApprovalState before emitting approval_requested', async () => {
      const approvalCheck = new FailCheck(
        'user-approval',
        undefined,
        async () => ({ name: 'user-approval', status: 'pending', message: 'Waiting for approval' }),
      );

      const runner = new TestStageRunner({
        checks: [approvalCheck],
        nextStage: Stage.Done,
        approvalCheckNames: ['user-approval'],
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
