import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../../src/types';
import type {
  StageContext,
  StageRunResult,
  CheckResult,
  ReactionConfig,
  IssueRepo,
  ChangeArtifactsManager,
  WorktreeManager,
  ProjectRepo,
  CheckpointManager,
} from '../../src/workflow/stage-context';
import type { Check, CheckContext } from '../../src/workflow/checks';
import { EventBus } from '../../src/services/event-bus';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';
import {
  WorkflowEngine,
  type PipelineResult,
} from '../../src/workflow/workflow-engine';

class PassCheck implements Check {
  name: string;
  reaction: ReactionConfig = { type: 'escalate' };
  constructor(name: string) {
    this.name = name;
  }
  async run(): Promise<CheckResult> {
    return { name: this.name, status: 'pass' };
  }
}

class FailCheck implements Check {
  name: string;
  reaction: ReactionConfig;
  private runFn: () => Promise<CheckResult>;
  private fixFn?: () => Promise<void>;

  constructor(
    name: string,
    reaction: ReactionConfig,
    runFn?: () => Promise<CheckResult>,
    fixFn?: () => Promise<void>,
  ) {
    this.name = name;
    this.reaction = reaction;
    this.runFn =
      runFn ??
      (async () => ({
        name: this.name,
        status: 'fail',
        message: `${this.name} failed`,
      }));
    this.fixFn = fixFn;
  }

  async run(): Promise<CheckResult> {
    return this.runFn();
  }
  async fix(): Promise<void> {
    if (this.fixFn) await this.fixFn();
  }
}

class PendingCheck implements Check {
  name = 'user-approval';
  reaction: ReactionConfig = {
    type: 'ask-user',
    fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan },
  };

  async run(): Promise<CheckResult> {
    return {
      name: this.name,
      status: 'pending',
      message: 'Waiting for user approval',
    };
  }
}

class SimpleRunner extends BaseStageRunner {
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

  canHandle(s: Stage): boolean {
    return s === this.handledStage;
  }

  protected async executeTasks(): Promise<unknown> {
    this.executeTasksCalls++;
    return this.executeTasksFn();
  }

  protected getChecks(): Check[] {
    return this.checks;
  }
  protected getNextStage(): Stage {
    return this.nextStage;
  }
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
      delete: vi.fn(),
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

function makeIssue(
  stage: Stage,
  overrides?: Partial<Issue>,
): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test',
    stage,
    status: 'active' as any,
    projectId: 'proj-1',
    labels: [],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

describe('Workflow Integration Tests', () => {
  let ctx: StageContext;

  beforeEach(() => {
    ctx = makeContext();
  });

  describe('full pipeline Plan→Build→Check→Done', () => {
    it('completes all stages when all checks pass', async () => {
      const planRunner = new SimpleRunner({
        checks: [new PassCheck('proposal-complete'), new PassCheck('user-approval')],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });

      const buildRunner = new SimpleRunner({
        checks: [
          new PassCheck('all-tasks-complete'),
          new PassCheck('code-compiles'),
        ],
        nextStage: Stage.Check,
        stage: Stage.Build,
      });

      const checkRunner = new SimpleRunner({
        checks: [
          new PassCheck('build-test-passed'),
          new PassCheck('ai-review-passed'),
          new PassCheck('user-approval'),
        ],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) =>
          makeIssue(stage),
        ),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner],
        issueRepo: mockIssueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
      });

      const result = await engine.run(makeIssue(Stage.Plan), {} as any);

      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);

      expect(mockIssueRepo.updateStage).toHaveBeenCalledWith(
        'issue-1',
        Stage.Build,
      );
      expect(mockIssueRepo.updateStage).toHaveBeenCalledWith(
        'issue-1',
        Stage.Check,
      );
      expect(mockIssueRepo.updateStage).toHaveBeenCalledWith(
        'issue-1',
        Stage.Done,
      );
      expect(mockIssueRepo.updateStatus).toHaveBeenCalledWith(
        'issue-1',
        IssueStatus.Completed,
      );
    });
  });

  describe('escalation paths', () => {
    it('CHECK build-test failure (after auto-fix exhausted) escalates to BUILD', async () => {
      const checkRunner = new SimpleRunner({
        checks: [
          new FailCheck(
            'build-test-passed',
            {
              type: 'auto-fix',
              maxAttempts: 1,
              fallbackReaction: {
                type: 'escalate',
                escalateTarget: Stage.Build,
              },
            },
            undefined,
            async () => {},
          ),
        ],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const result = await checkRunner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Build);
    });

    it('CHECK ai-review failure escalates to PLAN', async () => {
      const checkRunner = new SimpleRunner({
        checks: [
          new PassCheck('build-test-passed'),
          new FailCheck('ai-review-passed', {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          }),
        ],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const result = await checkRunner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Plan);
    });

    it('engine handles CHECK→Build escalation and continues pipeline', async () => {
      let checkRunCount = 0;

      const planRunner = new SimpleRunner({
        checks: [new PassCheck('proposal-complete')],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });

      const buildRunner = new SimpleRunner({
        checks: [new PassCheck('all-tasks-complete')],
        nextStage: Stage.Check,
        stage: Stage.Build,
      });

      const countingCheck: Check = {
        name: 'build-test-passed',
        reaction: { type: 'escalate', escalateTarget: Stage.Build },
        run: async () => {
          checkRunCount++;
          if (checkRunCount === 1) {
            return { name: 'build-test-passed', status: 'fail', message: 'First call fails' };
          }
          return { name: 'build-test-passed', status: 'pass' };
        },
      };

      const checkRunner = new SimpleRunner({
        checks: [countingCheck],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const stageHistory: Stage[] = [];
      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
          stageHistory.push(stage);
          return makeIssue(stage);
        }),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner],
        issueRepo: mockIssueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
      });

      const result = await engine.run(makeIssue(Stage.Plan), {} as any);

      expect(result.completed).toBe(true);
      expect(stageHistory).toContain(Stage.Build);
      expect(stageHistory).toContain(Stage.Check);
      expect(stageHistory).toContain(Stage.Done);
    });
  });

  describe('user-approval check pauses and resumes', () => {
    it('pipeline pauses when user-approval check returns pending', async () => {
      const runner = new SimpleRunner({
        checks: [
          new PassCheck('proposal-complete'),
          new PendingCheck(),
        ],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
      expect(result.escalateToStage).toBeUndefined();
      expect(ctx.issueRepo.setApprovalState).toHaveBeenCalledWith(
        'issue-1',
        expect.objectContaining({ status: 'awaiting' }),
      );
    });

    it('pipeline resumes when user approves', async () => {
      let callCount = 0;

      const approvalCheck: Check = {
        name: 'user-approval',
        reaction: {
          type: 'ask-user',
          fallbackReaction: {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          },
        },
        run: async () => {
          callCount++;
          if (callCount === 1) {
            return {
              name: 'user-approval',
              status: 'pending' as const,
              message: 'Waiting for approval',
            };
          }
          return {
            name: 'user-approval',
            status: 'pass' as const,
            message: 'User approved',
          };
        },
      };

      const runner = new SimpleRunner({
        checks: [new PassCheck('proposal-complete'), approvalCheck],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });

      const firstResult = await runner.run(ctx);
      expect(firstResult.success).toBe(false);

      const approvedIssue = makeIssue(Stage.Plan, {
        approvalState: {
          stage: Stage.Plan,
          status: 'approved',
          output: null,
          requestedAt: new Date().toISOString(),
        },
      });

      const approvalCtx = makeContext({ issue: approvedIssue });
      const approvalRunnerCheck: Check = {
        name: 'user-approval',
        reaction: {
          type: 'ask-user',
          fallbackReaction: {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          },
        },
        run: async () => ({
          name: 'user-approval',
          status: 'pass' as const,
          message: 'User approved',
        }),
      };

      const resumeRunner = new SimpleRunner({
        checks: [
          new PassCheck('proposal-complete'),
          approvalRunnerCheck,
        ],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });

      const secondResult = await resumeRunner.run(approvalCtx);
      expect(secondResult.success).toBe(true);
      expect(secondResult.nextStage).toBe(Stage.Build);
    });

    it('emits approval_requested event when pipeline pauses', async () => {
      const emitSpy = vi.spyOn(ctx.eventBus, 'emit');

      const runner = new SimpleRunner({
        checks: [new PendingCheck()],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });

      await runner.run(ctx);

      expect(emitSpy).toHaveBeenCalledWith('approval_requested', {
        issueId: 'issue-1',
        projectId: 'proj-1',
        stage: Stage.Plan,
      });
    });
  });

  describe('retry-task reaction with max retries', () => {
    it('retries tasks up to maxAttempts then stops', async () => {
      let failCount = 0;
      const alwaysFailAfter3 = new FailCheck(
        'flaky-check',
        { type: 'retry-task', maxAttempts: 3 },
        async () => {
          failCount++;
          return {
            name: 'flaky-check',
            status: 'fail',
            message: `Attempt ${failCount} failed`,
          };
        },
      );

      const runner = new SimpleRunner({
        checks: [alwaysFailAfter3],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(failCount).toBe(4);
      expect(runner.executeTasksCalls).toBe(4);
      expect(result.message).toContain('Attempt 4 failed');
    });

    it('uses fallback reaction after max retries exhausted', async () => {
      const alwaysFail = new FailCheck(
        'always-fail',
        {
          type: 'retry-task',
          maxAttempts: 2,
          fallbackReaction: {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          },
        },
      );

      const runner = new SimpleRunner({
        checks: [alwaysFail],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Plan);
    });

    it('succeeds if check passes within retry limit', async () => {
      let failCount = 0;
      const flakyCheck = new FailCheck(
        'flaky',
        { type: 'retry-task', maxAttempts: 3 },
        async () => {
          failCount++;
          if (failCount <= 2) {
            return {
              name: 'flaky',
              status: 'fail',
              message: 'Temporary failure',
            };
          }
          return { name: 'flaky', status: 'pass' };
        },
      );

      const runner = new SimpleRunner({
        checks: [flakyCheck],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Build);
      expect(runner.executeTasksCalls).toBe(3);
    });
  });

  describe('auto-fix reaction with fallback escalate', () => {
    it('re-runs check after fix and passes if fix works', async () => {
      let runCount = 0;
      let fixCalled = false;

      const autoFixCheck = new FailCheck(
        'build-test',
        { type: 'auto-fix', maxAttempts: 2 },
        async () => {
          runCount++;
          if (runCount <= 1) {
            return {
              name: 'build-test',
              status: 'fail',
              message: 'Build failed',
            };
          }
          return { name: 'build-test', status: 'pass' };
        },
        async () => {
          fixCalled = true;
        },
      );

      const runner = new SimpleRunner({
        checks: [autoFixCheck],
        nextStage: Stage.Check,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Check);
      expect(fixCalled).toBe(true);
      expect(runCount).toBe(2);
    });

    it('falls back to escalate after max auto-fix attempts', async () => {
      const alwaysFailCheck = new FailCheck(
        'build-test',
        {
          type: 'auto-fix',
          maxAttempts: 2,
          fallbackReaction: {
            type: 'escalate',
            escalateTarget: Stage.Build,
          },
        },
        async () => ({
          name: 'build-test',
          status: 'fail',
          message: 'Still broken',
        }),
        async () => {},
      );

      const runner = new SimpleRunner({
        checks: [alwaysFailCheck],
        nextStage: Stage.Check,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Build);
    });

    it('auto-fix with escalate to Plan fallback', async () => {
      const alwaysFailCheck = new FailCheck(
        'code-compiles',
        {
          type: 'auto-fix',
          maxAttempts: 2,
          fallbackReaction: {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          },
        },
        async () => ({
          name: 'code-compiles',
          status: 'fail',
          message: 'Compilation errors',
        }),
        async () => {},
      );

      const runner = new SimpleRunner({
        checks: [alwaysFailCheck],
        nextStage: Stage.Check,
        stage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBe(Stage.Plan);
    });
  });

  describe('non-OpenSpec issue completes full pipeline', () => {
    it('pipeline completes with simple runners (no openspec/changes/)', async () => {
      const planRunner = new SimpleRunner({
        checks: [new PassCheck('tasks-valid'), new PassCheck('user-approval')],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });

      const buildRunner = new SimpleRunner({
        checks: [
          new PassCheck('all-tasks-complete'),
          new PassCheck('code-compiles'),
        ],
        nextStage: Stage.Check,
        stage: Stage.Build,
      });

      const checkRunner = new SimpleRunner({
        checks: [
          new PassCheck('build-test-passed'),
          new PassCheck('ai-review-passed'),
          new PassCheck('user-approval'),
        ],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const stageTransitions: Stage[] = [];
      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
          stageTransitions.push(stage);
          return makeIssue(stage);
        }),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner],
        issueRepo: mockIssueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
      });

      const result = await engine.run(makeIssue(Stage.Plan), {} as any);

      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);
      expect(stageTransitions).toEqual([
        Stage.Build,
        Stage.Check,
        Stage.Done,
      ]);
    });
  });

  describe('serial check execution', () => {
    it('stops executing checks after first failure', async () => {
      const secondRunSpy = vi.fn();

      const runner = new SimpleRunner({
        checks: [
          new PassCheck('check-a'),
          new FailCheck('check-b', {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          }),
          {
            name: 'check-c',
            reaction: { type: 'escalate', escalateTarget: Stage.Plan },
            run: async () => {
              secondRunSpy();
              return { name: 'check-c', status: 'pass' as const };
            },
          },
        ],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(secondRunSpy).not.toHaveBeenCalled();
      expect(result.checkResults).toHaveLength(2);
      expect(result.checkResults[0].status).toBe('pass');
      expect(result.checkResults[1].status).toBe('fail');
    });
  });

  describe('WorkflowEngine handles edge cases', () => {
    it('returns incomplete when stage has no runner', async () => {
      const engine = new WorkflowEngine({
        runners: [],
        issueRepo: ctx.issueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
      });

      const result = await engine.run(makeIssue(Stage.Plan), {} as any);

      expect(result.completed).toBe(false);
      expect(result.message).toContain('cannot handle stage');
    });

    it('handles escalation in engine loop', async () => {
      const escalateRunner = new SimpleRunner({
        checks: [
          new FailCheck('check-fail', {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          }),
        ],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const passRunner = new SimpleRunner({
        checks: [new PassCheck('ok')],
        nextStage: Stage.Done,
        stage: Stage.Plan,
      });

      const stageHistory: Stage[] = [];
      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
          stageHistory.push(stage);
          return makeIssue(stage);
        }),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const engine = new WorkflowEngine({
        runners: [passRunner, escalateRunner],
        issueRepo: mockIssueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
      });

      const result = await engine.run(makeIssue(Stage.Check), {} as any);

      expect(result.completed).toBe(true);
      expect(stageHistory[0]).toBe(Stage.Plan);
    });

    it('returns incomplete when stage result has no nextStage and no escalate', async () => {
      const runner = new SimpleRunner({
        checks: [
          new FailCheck('fail', { type: 'retry-task', maxAttempts: 0 }),
        ],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
    });
  });

  describe('StageRunResult has no gate fields', () => {
    it('successful result has no requiresApproval field', async () => {
      const runner = new SimpleRunner({
        checks: [new PassCheck('ok')],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect((result as any).requiresApproval).toBeUndefined();
      expect((result as any).gateRequired).toBeUndefined();
    });

    it('failed result with escalation has no requiresApproval field', async () => {
      const runner = new SimpleRunner({
        checks: [
          new FailCheck('fail', {
            type: 'escalate',
            escalateTarget: Stage.Plan,
          }),
        ],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect((result as any).requiresApproval).toBeUndefined();
      expect((result as any).gateRequired).toBeUndefined();
    });

    it('PipelineResult has no gateRequired field', async () => {
      const planRunner = new SimpleRunner({
        checks: [new PassCheck('ok')],
        nextStage: Stage.Build,
        stage: Stage.Plan,
      });
      const buildRunner = new SimpleRunner({
        checks: [new PassCheck('ok')],
        nextStage: Stage.Check,
        stage: Stage.Build,
      });
      const checkRunner = new SimpleRunner({
        checks: [new PassCheck('ok')],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) =>
          makeIssue(stage),
        ),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner],
        issueRepo: mockIssueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
      });

      const result = await engine.run(makeIssue(Stage.Plan), {} as any);

      expect(result.completed).toBe(true);
      expect((result as any).gateRequired).toBeUndefined();
    });
  });

  describe('check results persistence', () => {
    it('persists check results via stageExecutionRepo', async () => {
      const mockStageExecRepo = {
        create: vi.fn().mockReturnValue({ id: 'exec-1' }),
        updateCheckResults: vi.fn(),
      };

      const ctxWithRepo = makeContext({
        stageExecutionRepo: mockStageExecRepo as any,
      });

      const runner = new SimpleRunner({
        checks: [new PassCheck('check-a'), new PassCheck('check-b')],
        nextStage: Stage.Build,
      });

      await runner.run(ctxWithRepo);

      expect(mockStageExecRepo.create).toHaveBeenCalledWith(
        'issue-1',
        Stage.Plan,
      );
      expect(mockStageExecRepo.updateCheckResults).toHaveBeenCalledWith(
        'exec-1',
        expect.arrayContaining([
          expect.objectContaining({ name: 'check-a', status: 'pass' }),
          expect.objectContaining({ name: 'check-b', status: 'pass' }),
        ]),
      );
    });
  });
});
