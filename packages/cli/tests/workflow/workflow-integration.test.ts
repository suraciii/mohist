import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../../src/types';
import type {
  StageContext,
  StageRunResult,
  CheckResult,
  IssueRepo,
  ChangeArtifactsManager,
  WorktreeManager,
  ProjectRepo,
  CheckpointManager,
  CheckFailurePolicy,
  StageTaskResult,
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
  constructor(name: string) {
    this.name = name;
  }
  async run(): Promise<CheckResult> {
    return { name: this.name, status: 'pass' };
  }
}

class FailCheck implements Check {
  name: string;
  private runFn: () => Promise<CheckResult>;

  constructor(
    name: string,
    runFn?: () => Promise<CheckResult>,
  ) {
    this.name = name;
    this.runFn =
      runFn ??
      (async () => ({
        name: this.name,
        status: 'fail',
        message: `${this.name} failed`,
      }));
  }

  async run(): Promise<CheckResult> {
    return this.runFn();
  }
}

class PendingCheck implements Check {
  name = 'user-approval';

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
  private failurePolicies: CheckFailurePolicy[];
  private fixTaskResults: Map<string, StageTaskResult>;
  private _isApprovalCheck: (checkName: string) => boolean;

  constructor(opts: {
    checks: Check[];
    nextStage: Stage;
    stage?: Stage;
    executeTasksFn?: () => Promise<unknown>;
    failurePolicies?: CheckFailurePolicy[];
    fixTaskResults?: Map<string, StageTaskResult>;
    isApprovalCheck?: (checkName: string) => boolean;
  }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage;
    this.handledStage = opts.stage ?? Stage.Plan;
    this.executeTasksFn = opts.executeTasksFn ?? (async () => ({ done: true }));
    this.failurePolicies = opts.failurePolicies ?? [];
    this.fixTaskResults = opts.fixTaskResults ?? new Map();
    this._isApprovalCheck = opts.isApprovalCheck ?? (() => false);
  }

  canHandle(s: Stage): boolean {
    return s === this.handledStage;
  }

  protected isApprovalCheck(checkName: string): boolean {
    return this._isApprovalCheck(checkName);
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

  protected getCheckFailurePolicies(): CheckFailurePolicy[] {
    return this.failurePolicies;
  }

  protected async runFixTask(
    _ctx: StageContext,
    taskId: string,
    _failedCheck: CheckResult,
    _attempt: number,
  ): Promise<StageTaskResult | null> {
    const result = this.fixTaskResults.get(taskId);
    return result ?? null;
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
        nextStage: Stage.Integrate,
        stage: Stage.Check,
      });

      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) =>
          makeIssue(stage),
        ),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const integrateRunner = new SimpleRunner({
        checks: [],
        nextStage: Stage.Done,
        stage: Stage.Integrate,
      });

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner, integrateRunner],
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
        Stage.Integrate,
      );
    });
  });

  describe('check failure without policy', () => {
    it('CHECK build-test failure without policy fails the stage without escalation', async () => {
      const checkRunner = new SimpleRunner({
        checks: [
          new FailCheck('build-test-passed'),
        ],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const result = await checkRunner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
    });

    it('CHECK ai-review failure without policy fails the stage without escalation', async () => {
      const checkRunner = new SimpleRunner({
        checks: [
          new PassCheck('build-test-passed'),
          new FailCheck('ai-review-passed'),
        ],
        nextStage: Stage.Done,
        stage: Stage.Check,
      });

      const result = await checkRunner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
    });

    it('engine stops at the failing stage when check has no policy', async () => {
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

      const checkRunner = new SimpleRunner({
        checks: [new FailCheck('build-test-passed')],
        nextStage: Stage.Integrate,
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

      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(stageHistory).toContain(Stage.Build);
      expect(stageHistory).toContain(Stage.Check);
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
        isApprovalCheck: (name) => name === 'user-approval',
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
      expect(ctx.issueRepo.setApprovalState).toHaveBeenCalledWith(
        'issue-1',
        expect.objectContaining({ status: 'awaiting' }),
      );
    });

    it('pipeline resumes when user approves', async () => {
      let callCount = 0;

      const approvalCheck: Check = {
        name: 'user-approval',
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
        isApprovalCheck: (name) => name === 'user-approval',
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
        isApprovalCheck: (name) => name === 'user-approval',
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
        isApprovalCheck: (name) => name === 'user-approval',
      });

      await runner.run(ctx);

      expect(emitSpy).toHaveBeenCalledWith('approval_requested', {
        issueId: 'issue-1',
        projectId: 'proj-1',
        stage: Stage.Plan,
      });
    });
  });

  describe('policy-driven fix task behavior', () => {
    it('runs fix task and re-checks when policy matches', async () => {
      let runCount = 0;

      const flakyCheck: Check = {
        name: 'build-test',
        run: async () => {
          runCount++;
          if (runCount <= 1) {
            return { name: 'build-test', status: 'fail', message: 'Build failed' };
          }
          return { name: 'build-test', status: 'pass' };
        },
      };

      const fixResult: StageTaskResult = {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 1000,
      };

      const runner = new SimpleRunner({
        checks: [flakyCheck],
        nextStage: Stage.Check,
        failurePolicies: [
          { checkName: 'build-test', fixTaskId: 'fix-build-health', maxAttempts: 2 },
        ],
        fixTaskResults: new Map([['fix-build-health', fixResult]]),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Check);
      expect(runCount).toBe(2);
    });

    it('fails after max fix attempts exhausted', async () => {
      const alwaysFail: Check = {
        name: 'build-test',
        run: async () => ({
          name: 'build-test',
          status: 'fail',
          message: 'Still broken',
        }),
      };

      const fixResult: StageTaskResult = {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 1000,
      };

      const runner = new SimpleRunner({
        checks: [alwaysFail],
        nextStage: Stage.Check,
        failurePolicies: [
          { checkName: 'build-test', fixTaskId: 'fix-build-health', maxAttempts: 2 },
        ],
        fixTaskResults: new Map([['fix-build-health', fixResult]]),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
    });

    it('succeeds if check passes within fix attempt limit', async () => {
      let failCount = 0;

      const flakyCheck: Check = {
        name: 'flaky',
        run: async () => {
          failCount++;
          if (failCount <= 2) {
            return { name: 'flaky', status: 'fail', message: 'Temporary failure' };
          }
          return { name: 'flaky', status: 'pass' };
        },
      };

      const fixResult: StageTaskResult = {
        taskId: 'fix-flaky',
        title: 'Fix flaky',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 1000,
      };

      const runner = new SimpleRunner({
        checks: [flakyCheck],
        nextStage: Stage.Build,
        failurePolicies: [
          { checkName: 'flaky', fixTaskId: 'fix-flaky', maxAttempts: 3 },
        ],
        fixTaskResults: new Map([['fix-flaky', fixResult]]),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Build);
    });

    it('continues when fix task returns null and check passes on re-run', async () => {
      let runCount = 0;

      const check: Check = {
        name: 'build-test',
        run: async () => {
          runCount++;
          if (runCount <= 1) {
            return { name: 'build-test', status: 'fail', message: 'Build failed' };
          }
          return { name: 'build-test', status: 'pass' };
        },
      };

      const runner = new SimpleRunner({
        checks: [check],
        nextStage: Stage.Check,
        failurePolicies: [
          { checkName: 'build-test', fixTaskId: 'fix-build-health', maxAttempts: 1 },
        ],
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.nextStage).toBe(Stage.Check);
    });
  });

  describe('non-OpenSpec issue completes up to Check (merge-gated)', () => {
    it('pipeline completes build stage and stops at Check awaiting approval', async () => {
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
          {
            name: 'review-passed',
            run: async () => ({
              name: 'review-passed',
              status: 'pass' as const,
              output: { verdict: 'PASS', reviewReport: 'Mock review report' },
            }),
          },
          new PassCheck('merge-ready'),
          new PendingCheck(),
        ],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        isApprovalCheck: (name) => name === 'user-approval',
      });

      const stageTransitions: Stage[] = [];
      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
          stageTransitions.push(stage);
          return makeIssue(stage);
        }),
        setApprovalState: vi.fn(),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const integrateRunner = new SimpleRunner({
        checks: [],
        nextStage: Stage.Done,
        stage: Stage.Integrate,
      });

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner, integrateRunner],
        issueRepo: mockIssueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'abc123' }),
        } as unknown as WorktreeManager,
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'proj-1', name: 'test-project', path: '/tmp/project' }),
        } as unknown as ProjectRepo,
      });

      const result = await engine.run(makeIssue(Stage.Plan), {} as any);

      expect(result.completed).toBe(false);
      expect(result.message).toContain('Waiting for user approval');
    });
  });

  describe('serial check execution', () => {
    it('stops executing checks after first failure', async () => {
      const secondRunSpy = vi.fn();

      const runner = new SimpleRunner({
        checks: [
          new PassCheck('check-a'),
          new FailCheck('check-b'),
          {
            name: 'check-c',
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

    it('returns incomplete when check fails without next stage', async () => {
      const runner = new SimpleRunner({
        checks: [
          new FailCheck('fail'),
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

    it('failed result without escalation has no requiresApproval field', async () => {
      const runner = new SimpleRunner({
        checks: [
          new FailCheck('fail'),
        ],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect((result as any).requiresApproval).toBeUndefined();
      expect((result as any).gateRequired).toBeUndefined();
    });

    it('PipelineResult has no gateRequired field (stops at Check, not Done)', async () => {
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
        nextStage: Stage.Integrate,
        stage: Stage.Check,
      });

      const mockIssueRepo = {
        updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) =>
          makeIssue(stage),
        ),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn().mockReturnValue(makeIssue(Stage.Done)),
      } as unknown as IssueRepo;

      const integrateRunner = new SimpleRunner({
        checks: [],
        nextStage: Stage.Done,
        stage: Stage.Integrate,
      });

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner, integrateRunner],
        issueRepo: mockIssueRepo,
        eventBus: ctx.eventBus,
        checkpointManager: ctx.checkpointManager,
        artifactManager: ctx.artifactManager,
      });

      const result = await engine.run(makeIssue(Stage.Plan), {} as any);

      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);
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
