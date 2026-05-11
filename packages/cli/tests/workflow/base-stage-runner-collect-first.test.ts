import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../../src/types';
import type {
  StageContext,
  CheckResult,
  StageTaskResult,
  CheckFailurePolicy,
  ChangeArtifactsManager,
  WorktreeManager,
  ProjectRepo,
  CheckpointManager,
  IssueRepo,
  StageExecutionRepo,
} from '../../src/workflow/stage-context';
import type { Check } from '../../src/workflow/checks';
import { EventBus } from '../../src/services/event-bus';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: '',
    stage: Stage.Build,
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
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn().mockReturnValue(null),
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
      upsert: vi.fn(),
      markStepComplete: vi.fn(),
      deleteStep: vi.fn(),
    } as unknown as CheckpointManager,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
      findById: vi.fn(),
    } as unknown as IssueRepo,
    stageExecutionRepo: {
      create: vi.fn().mockReturnValue({ id: 'exec-1' }),
      updateCheckResults: vi.fn(),
      appendTaskResult: vi.fn(),
      updateStatus: vi.fn(),
    } as unknown as StageExecutionRepo,
    ...overrides,
  } as StageContext;
}

class TestStageRunner extends BaseStageRunner {
  private checks: Check[];
  private nextStage: Stage;
  private handledStage: Stage;
  private executeTasksFn: () => Promise<unknown>;
  private failurePolicies: CheckFailurePolicy[];
  private fixTaskFn: (taskId: string, failedCheck: CheckResult, attempt: number) => Promise<StageTaskResult | null>;
  private approvalCheckNames: Set<string>;
  private beforeRecheckFn: (ctx: StageContext, checkName: string, fixTaskId: string) => Promise<void>;
  executeTasksCalls = 0;
  fixTaskCalls: { taskId: string; failedCheck: CheckResult; attempt: number }[] = [];
  taskResults: StageTaskResult[] = [];

  constructor(opts: {
    checks: Check[];
    nextStage?: Stage;
    stage?: Stage;
    executeTasksFn?: () => Promise<unknown>;
    failurePolicies?: CheckFailurePolicy[];
    fixTaskFn?: (taskId: string, failedCheck: CheckResult, attempt: number) => Promise<StageTaskResult | null>;
    approvalCheckNames?: string[];
    beforeRecheckFn?: (ctx: StageContext, checkName: string, fixTaskId: string) => Promise<void>;
  }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage ?? Stage.Check;
    this.handledStage = opts.stage ?? Stage.Build;
    this.executeTasksFn = opts.executeTasksFn ?? (async () => ({ done: true }));
    this.failurePolicies = opts.failurePolicies ?? [];
    this.fixTaskFn = opts.fixTaskFn ?? (async () => null);
    this.approvalCheckNames = new Set(opts.approvalCheckNames ?? []);
    this.beforeRecheckFn = opts.beforeRecheckFn ?? (async () => {});
  }

  canHandle(s: Stage): boolean { return s === this.handledStage; }
  protected isApprovalCheck(checkName: string): boolean { return this.approvalCheckNames.has(checkName); }
  protected getCheckFailurePolicies(): CheckFailurePolicy[] { return this.failurePolicies; }

  protected async runFixTask(
    _ctx: StageContext,
    taskId: string,
    failedCheck: CheckResult,
    attempt: number,
  ): Promise<StageTaskResult | null> {
    this.fixTaskCalls.push({ taskId, failedCheck, attempt });
    return this.fixTaskFn(taskId, failedCheck, attempt);
  }

  protected async beforeRecheckAfterFix(ctx: StageContext, checkName: string, fixTaskId: string): Promise<void> {
    return this.beforeRecheckFn(ctx, checkName, fixTaskId);
  }

  protected async executeTasks(): Promise<unknown> {
    this.executeTasksCalls++;
    return this.executeTasksFn();
  }

  protected getChecks(): Check[] { return this.checks; }
  protected getNextStage(): Stage { return this.nextStage; }

  protected appendTaskResult(ctx: StageContext, result: StageTaskResult): void {
    this.taskResults.push(result);
    super.appendTaskResult(ctx, result);
  }
}

describe('collect-first check phase regression', () => {
  describe('AC-1: multiple ordinary failing checks returns complete initial check visibility', () => {
    it('phase with two failing non-approval checks records both in baseline before any repair', async () => {
      const check1Run = vi.fn().mockResolvedValue({ name: 'check-a', status: 'fail' as const, message: 'check-a failed' });
      const check2Run = vi.fn().mockResolvedValue({ name: 'check-b', status: 'fail' as const, message: 'check-b failed' });

      const runner = new TestStageRunner({
        checks: [
          { name: 'check-a', run: check1Run } as Check,
          { name: 'check-b', run: check2Run } as Check,
        ],
        nextStage: Stage.Check,
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const baselineChecks = result.checkResults.map(r => r.name);
      expect(baselineChecks).toContain('check-a');
      expect(baselineChecks).toContain('check-b');
      expect(check1Run).toHaveBeenCalledTimes(1);
      expect(check2Run).toHaveBeenCalledTimes(1);
    });

    it('later checks run even when earlier checks fail during baseline collection', async () => {
      const check1Run = vi.fn().mockResolvedValue({ name: 'check-first', status: 'fail' as const, message: 'first failed' });
      const check2Run = vi.fn().mockResolvedValue({ name: 'check-second', status: 'pass' as const });
      const check3Run = vi.fn().mockResolvedValue({ name: 'check-third', status: 'pass' as const });

      const runner = new TestStageRunner({
        checks: [
          { name: 'check-first', run: check1Run } as Check,
          { name: 'check-second', run: check2Run } as Check,
          { name: 'check-third', run: check3Run } as Check,
        ],
        nextStage: Stage.Check,
      });

      const ctx = makeContext();
      await runner.run(ctx);

      expect(check1Run).toHaveBeenCalledTimes(1);
      expect(check2Run).toHaveBeenCalledTimes(1);
      expect(check3Run).toHaveBeenCalledTimes(1);
    });
  });

  describe('AC-2: pending user-approval without ordinary failures yields awaiting approval without repair', () => {
    it('pending user-approval alone triggers no fix task when all ordinary checks pass', async () => {
      let fixTaskCalled = false;

      const runner = new TestStageRunner({
        checks: [
          { name: 'health:build', run: async () => ({ name: 'health:build', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending', message: 'Waiting for approval' }) } as Check,
        ],
        nextStage: Stage.Check,
        approvalCheckNames: ['user-approval'],
        fixTaskFn: async () => {
          fixTaskCalled = true;
          return null;
        },
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Waiting for approval');
      expect(fixTaskCalled).toBe(false);
      expect(runner.fixTaskCalls).toHaveLength(0);
    });

    it('pending user-approval is not treated as repairable even with a policy', async () => {
      let fixTaskCalled = false;

      const runner = new TestStageRunner({
        checks: [
          { name: 'health:build', run: async () => ({ name: 'health:build', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        nextStage: Stage.Check,
        approvalCheckNames: ['user-approval'],
        failurePolicies: [{ checkName: 'user-approval', fixTaskId: 'fix-approval', maxAttempts: 2 }],
        fixTaskFn: async () => {
          fixTaskCalled = true;
          return null;
        },
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(fixTaskCalled).toBe(false);
      expect(result.success).toBe(false);
    });

    it('user-approval pending does not mask ordinary failures', async () => {
      const runner = new TestStageRunner({
        checks: [
          { name: 'health:build', run: async () => ({ name: 'health:build', status: 'fail', message: 'build broken' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        nextStage: Stage.Check,
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('build broken');
      const checkNames = result.checkResults.map(r => r.name);
      expect(checkNames).toContain('health:build');
      expect(checkNames).toContain('user-approval');
    });
  });

  describe('AC-3: failed check with policy runs fix task, rechecks, preserves downstream execution', () => {
    it('repairable failure triggers fix task and re-runs the repaired check', async () => {
      let checkRunCount = 0;
      const check = {
        name: 'health:build',
        run: async () => {
          checkRunCount++;
          if (checkRunCount === 1) {
            return { name: 'health:build', status: 'fail', message: 'build failed' };
          }
          return { name: 'health:build', status: 'pass' };
        },
      } as Check;

      let fixTaskCalled = false;
      const runner = new TestStageRunner({
        checks: [check],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 2 }],
        fixTaskFn: async (taskId) => {
          fixTaskCalled = true;
          return { taskId, title: 'Fix build health', status: 'completed', artifacts: [], attempts: 1, duration: 100 };
        },
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(fixTaskCalled).toBe(true);
      expect(checkRunCount).toBe(2);
      expect(runner.fixTaskCalls).toHaveLength(1);
      expect(runner.fixTaskCalls[0].taskId).toBe('fix-build-health');
    });

    it('after successful repair, later checks run from the repaired point', async () => {
      let checkRunCount = 0;
      const check1 = {
        name: 'check-a',
        run: async () => {
          checkRunCount++;
          if (checkRunCount === 1) {
            return { name: 'check-a', status: 'fail' };
          }
          return { name: 'check-a', status: 'pass' };
        },
      } as Check;
      const check2Run = vi.fn().mockResolvedValue({ name: 'check-b', status: 'pass' });
      const check3Run = vi.fn().mockResolvedValue({ name: 'check-c', status: 'pass' });

      const runner = new TestStageRunner({
        checks: [check1, { name: 'check-b', run: check2Run } as Check, { name: 'check-c', run: check3Run } as Check],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'check-a', fixTaskId: 'fix-a', maxAttempts: 1 }],
        fixTaskFn: async () => ({ taskId: 'fix-a', title: 'Fix A', status: 'completed', artifacts: [], attempts: 1, duration: 100 }),
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(checkRunCount).toBe(2);
      expect(check2Run).toHaveBeenCalledTimes(2);
      expect(check3Run).toHaveBeenCalledTimes(2);
    });

    it('fix task result is appended to task results', async () => {
      const check = {
        name: 'health:build',
        run: async () => ({ name: 'health:build', status: 'fail', message: 'failed' }),
      } as Check;

      const runner = new TestStageRunner({
        checks: [check],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 1 }],
        fixTaskFn: async () => ({ taskId: 'fix-build-health', title: 'Fix build health', status: 'completed', artifacts: [], attempts: 1, duration: 200 }),
      });

      const ctx = makeContext();
      await runner.run(ctx);

      expect(runner.taskResults.some(t => t.taskId === 'fix-build-health')).toBe(true);
    });
  });

  describe('AC-4: collected failures without policy fail the stage with complete evidence preserved', () => {
    it('failure without policy stops stage with full baseline visible', async () => {
      const runner = new TestStageRunner({
        checks: [
          { name: 'check-a', run: async () => ({ name: 'check-a', status: 'fail', message: 'a broken' }) } as Check,
          { name: 'check-b', run: async () => ({ name: 'check-b', status: 'fail', message: 'b broken' }) } as Check,
        ],
        nextStage: Stage.Check,
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.checkResults.map(r => r.name)).toEqual(['check-a', 'check-b']);
      expect(result.message).toContain('a broken');
      expect(runner.fixTaskCalls).toHaveLength(0);
    });

    it('unrepaired failure without policy preserves all check results', async () => {
      const runner = new TestStageRunner({
        checks: [
          { name: 'check-first', run: async () => ({ name: 'check-first', status: 'fail', message: 'first broken' }) } as Check,
          { name: 'check-second', run: async () => ({ name: 'check-second', status: 'pass' }) } as Check,
        ],
        nextStage: Stage.Check,
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.checkResults).toHaveLength(2);
      const names = result.checkResults.map(r => r.name);
      expect(names).toContain('check-first');
      expect(names).toContain('check-second');
    });
  });
});
