import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, type Issue, IssueStatus } from '../../src/types';
import type {
  StageContext,
  StageRunResult,
  CheckResult,
  StageTaskResult,
  CheckFailurePolicy,
  IssueRepo,
  ChangeArtifactsManager,
  WorktreeManager,
  ProjectRepo,
  CheckpointManager,
  CheckSuiteRepo,
} from '../../src/workflow/stage-context';
import type { Check, CheckContext } from '../../src/workflow/checks';
import { EventBus } from '../../src/services/event-bus';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';
import {
  getLatestCheckResult,
  replaceCurrentAiReviewTruth,
  buildAuthoritativeAiReviewResult,
} from '../../src/workflow/stage-context';

class PassCheck implements Check {
  name: string;
  constructor(name: string) { this.name = name; }
  async run(): Promise<CheckResult> { return { name: this.name, status: 'pass' }; }
}

class PendingCheck implements Check {
  name = 'user-approval';
  async run(): Promise<CheckResult> { return { name: this.name, status: 'pending', message: 'Waiting for user approval' }; }
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

  constructor(opts: {
    checks: Check[];
    nextStage: Stage;
    stage?: Stage;
    executeTasksFn?: () => Promise<unknown>;
    failurePolicies?: CheckFailurePolicy[];
    fixTaskFn?: (taskId: string, failedCheck: CheckResult, attempt: number) => Promise<StageTaskResult | null>;
    approvalCheckNames?: string[];
    beforeRecheckFn?: (ctx: StageContext, checkName: string, fixTaskId: string) => Promise<void>;
  }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage;
    this.handledStage = opts.stage ?? Stage.Plan;
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
}

function makeContext(overrides?: Partial<StageContext>): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      stage: Stage.Check,
      status: IssueStatus.Active,
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
    worktreeManager: {
      getPath: vi.fn().mockReturnValue('/tmp/worktree'),
      getHeadSha: vi.fn().mockResolvedValue('sha-head-001'),
      isWorktreeClean: vi.fn().mockResolvedValue(true),
      createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'sha-converged-001' }),
    } as unknown as WorktreeManager,
    projectRepo: {
      findById: vi.fn().mockReturnValue({ id: 'proj-1', name: 'test-project', path: '/tmp/project' }),
    } as unknown as ProjectRepo,
    eventBus: new EventBus() as any,
    checkpointManager: {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
      getResumeSteps: vi.fn().mockReturnValue([]),
      upsert: vi.fn(),
      markStepComplete: vi.fn(),
    } as unknown as CheckpointManager,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
      findById: vi.fn(),
    } as unknown as IssueRepo,
    ...overrides,
  } as StageContext;
}

const FAIL_REVIEW_REPORT = '# Review\n<promise>FAIL</promise>\n\n### Code Quality: FAIL\n- Error handling is missing';
const PASS_REVIEW_REPORT = '# Review\n<promise>PASS</promise>\n\n### Code Quality: PASS';
const FAIL_REVIEW_REPORT_V2 = '# Review (re-check)\n<promise>FAIL</promise>\n\n### Code Quality: FAIL\n- Still has issues';

describe('Check-stage re-review convergence regressions', () => {
  let ctx: StageContext;

  beforeEach(() => {
    ctx = makeContext();
  });

  describe('AC-1: FAIL -> auto-fix -> regenerated re-review PASS -> persisted PASS -> approval requested', () => {
    it('full convergence flow: ai-review FAIL, fix, re-review PASS, persisted PASS, approval requested', async () => {
      let reviewRunCount = 0;
      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: {
                verdict: 'FAIL',
                reviewReport: FAIL_REVIEW_REPORT,
                fixSuggestions: 'Fix error handling',
              },
            };
          }
          return {
            name: 'ai-review',
            status: 'pass' as const,
            message: 'AI review passed',
            output: {
              verdict: 'PASS',
              reviewReport: PASS_REVIEW_REPORT,
            },
          };
        },
      };

      const userApprovalCheck = new PendingCheck();
      const persistedCheckResults: CheckResult[][] = [];
      const approvalCalls: unknown[] = [];

      ctx = makeContext({
        stageExecutionRepo: {
          create: vi.fn().mockReturnValue({ id: 'exec-1' }),
          updateCheckResults: vi.fn().mockImplementation((_id: string, results: CheckResult[]) => {
            persistedCheckResults.push(results.map(r => ({ ...r })));
          }),
          appendTaskResult: vi.fn(),
          updateStatus: vi.fn(),
        } as any,
        issueRepo: {
          updateStage: vi.fn(),
          setApprovalState: vi.fn().mockImplementation((_id: string, state: unknown) => {
            approvalCalls.push(state);
          }),
          clearApprovalState: vi.fn(),
          updateStatus: vi.fn(),
          findById: vi.fn(),
        } as unknown as IssueRepo,
      });

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, userApprovalCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(reviewRunCount).toBe(2);
      expect(runner.fixTaskCalls).toHaveLength(1);
      expect(runner.fixTaskCalls[0].taskId).toBe('fix-review-findings');

      const latestAiReview = getLatestCheckResult(result.checkResults, 'ai-review');
      expect(latestAiReview).toBeDefined();
      expect(latestAiReview!.status).toBe('pass');
      expect((latestAiReview!.output as any).verdict).toBe('PASS');
      expect((latestAiReview!.output as any).reviewReport).toBe(PASS_REVIEW_REPORT);
      expect(result.checkResults.filter(r => r.name === 'ai-review')).toHaveLength(1);
      expect((latestAiReview!.output as any).snapshotSha).toBeDefined();
      expect((latestAiReview!.output as any).reviewArtifactPath).toBe('/tmp/change/review.md');
      expect((latestAiReview!.output as any).selfCheckArtifactPath).toBe('/tmp/change/review-self-check.md');

      expect(approvalCalls).toHaveLength(1);
      const approvalState = approvalCalls[0] as { stage: Stage; status: string; output: any };
      expect(approvalState.status).toBe('awaiting');
      expect(approvalState.output).toBeDefined();
      expect(approvalState.output.result).toBe('PASS');
      expect(approvalState.output.snapshotSha).toBe('sha-converged-001');

      expect(result.message).toContain('approval');
    });
  });

  describe('AC-2: fix-review-findings changes code and old review.md is not reused', () => {
    it('beforeRecheckAfterFix is called for ai-review after fix-review-findings', async () => {
      let reviewRunCount = 0;
      let beforeRecheckCalled = false;
      let beforeRecheckArgs: { checkName: string; fixTaskId: string } | null = null;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'pass' as const,
            message: 'AI review passed',
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, new PendingCheck()],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
        beforeRecheckFn: async (_ctx, checkName, fixTaskId) => {
          beforeRecheckCalled = true;
          beforeRecheckArgs = { checkName, fixTaskId };
        },
      });

      await runner.run(ctx);

      expect(beforeRecheckCalled).toBe(true);
      expect(beforeRecheckArgs).toEqual({
        checkName: 'ai-review',
        fixTaskId: 'fix-review-findings',
      });
    });

    it('re-review produces a fresh report different from the stale one', async () => {
      let reviewRunCount = 0;
      const reviewReports: string[] = [];

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'pass' as const,
            message: 'AI review passed',
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, new PendingCheck()],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      const aiReviewResults = result.checkResults.filter(r => r.name === 'ai-review');
      for (const r of aiReviewResults) {
        reviewReports.push((r.output as any).reviewReport);
      }

      expect(reviewReports).toEqual([PASS_REVIEW_REPORT]);
    });
  });

  describe('AC-3: approval output uses latest re-review report and matching snapshotSha', () => {
    it('approval output contains latest PASS report and converged snapshotSha', async () => {
      const latestReport = '# Review (regenerated)\n<promise>PASS</promise>\n\n### Code Quality: PASS';
      let reviewRunCount = 0;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'pass' as const,
            message: 'AI review passed',
            output: { verdict: 'PASS', reviewReport: latestReport },
          };
        },
      };

      const approvalCalls: unknown[] = [];
      ctx = makeContext({
        issueRepo: {
          updateStage: vi.fn(),
          setApprovalState: vi.fn().mockImplementation((_id: string, state: unknown) => {
            approvalCalls.push(state);
          }),
          clearApprovalState: vi.fn(),
          updateStatus: vi.fn(),
          findById: vi.fn(),
        } as unknown as IssueRepo,
      });

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, new PendingCheck()],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      await runner.run(ctx);

      expect(approvalCalls).toHaveLength(1);
      const approvalOutput = (approvalCalls[0] as any).output;
      expect(approvalOutput.result).toBe('PASS');
      expect(approvalOutput.reviewReport).toBe(latestReport);
      expect(approvalOutput.snapshotSha).toBe('sha-converged-001');
    });

    it('convergence snapshotSha is set on the latest ai-review result in checkResults', async () => {
      let reviewRunCount = 0;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'pass' as const,
            message: 'AI review passed',
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, new PendingCheck()],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      const latest = getLatestCheckResult(result.checkResults, 'ai-review');
      expect(latest).toBeDefined();
      expect((latest!.output as any).snapshotSha).toBe('sha-converged-001');
    });
  });

  describe('AC-4: auto-fix changes that cannot be committed block ordinary approval', () => {
    it('convergence commit failure prevents approval with a clear message', async () => {
      let reviewRunCount = 0;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'pass' as const,
            message: 'AI review passed',
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
          };
        },
      };

      const approvalCalls: unknown[] = [];
      ctx = makeContext({
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({
            success: false,
            headSha: '',
            error: 'Worktree still has uncommitted changes after convergence commit (possible pre-commit hook modifications)',
          }),
        } as unknown as WorktreeManager,
        issueRepo: {
          updateStage: vi.fn(),
          setApprovalState: vi.fn().mockImplementation((_id: string, state: unknown) => {
            approvalCalls.push(state);
          }),
          clearApprovalState: vi.fn(),
          updateStatus: vi.fn(),
          findById: vi.fn(),
        } as unknown as IssueRepo,
      });

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, new PendingCheck()],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('uncommitted changes');
      expect(approvalCalls).toHaveLength(0);
    });

    it('convergence commit error from git failure blocks approval', async () => {
      let reviewRunCount = 0;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'pass' as const,
            message: 'AI review passed',
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
          };
        },
      };

      ctx = makeContext({
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({
            success: false,
            headSha: '',
            error: 'Convergence commit failed: git commit exited with code 1',
          }),
        } as unknown as WorktreeManager,
      });

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, new PendingCheck()],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Convergence commit failed');
    });
  });

  describe('AC-5: re-review FAIL does not enter PASS or approval and preserves latest FAIL truth', () => {
    it('re-review still returns FAIL after fix: no approval, latest FAIL preserved', async () => {
      let reviewRunCount = 0;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'AI review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'fail' as const,
            message: 'AI review still failed',
            output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT_V2 },
          };
        },
      };

      const approvalCalls: unknown[] = [];
      ctx = makeContext({
        issueRepo: {
          updateStage: vi.fn(),
          setApprovalState: vi.fn().mockImplementation((_id: string, state: unknown) => {
            approvalCalls.push(state);
          }),
          clearApprovalState: vi.fn(),
          updateStatus: vi.fn(),
          findById: vi.fn(),
        } as unknown as IssueRepo,
      });

      const runner = new TestStageRunner({
        checks: [aiReviewCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(reviewRunCount).toBe(2);
      expect(approvalCalls).toHaveLength(0);

      const latest = getLatestCheckResult(result.checkResults, 'ai-review');
      expect(latest).toBeDefined();
      expect(latest!.status).toBe('fail');
      expect((latest!.output as any).verdict).toBe('FAIL');
      expect((latest!.output as any).reviewReport).toBe(FAIL_REVIEW_REPORT_V2);
      expect(result.checkResults.filter(r => r.name === 'ai-review')).toHaveLength(1);
    });

    it('latest FAIL report is the re-reviewed one, not the initial one', async () => {
      let reviewRunCount = 0;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'ai-review',
              status: 'fail' as const,
              message: 'Initial FAIL',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'ai-review',
            status: 'fail' as const,
            message: 'Re-review FAIL',
            output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT_V2 },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [aiReviewCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
      });

      const result = await runner.run(ctx);

      const latest = getLatestCheckResult(result.checkResults, 'ai-review');
      expect((latest!.output as any).reviewReport).toBe(FAIL_REVIEW_REPORT_V2);
      expect((latest!.output as any).reviewReport).not.toBe(FAIL_REVIEW_REPORT);
    });

    it('does not reach user-approval check when re-review fails', async () => {
      let reviewRunCount = 0;
      let approvalCheckReached = false;

      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => {
          reviewRunCount++;
          return {
            name: 'ai-review',
            status: 'fail' as const,
            message: reviewRunCount === 1 ? 'Initial FAIL' : 'Re-review FAIL',
            output: { verdict: 'FAIL', reviewReport: reviewRunCount === 1 ? FAIL_REVIEW_REPORT : FAIL_REVIEW_REPORT_V2 },
          };
        },
      };

      const userApprovalCheck: Check = {
        name: 'user-approval',
        run: async () => {
          approvalCheckReached = true;
          return { name: 'user-approval', status: 'pending' as const, message: 'Waiting for user approval' };
        },
      };

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, userApprovalCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(approvalCheckReached).toBe(false);
    });
  });

  describe('Helper functions: getLatestCheckResult and replaceCurrentAiReviewTruth', () => {
    it('getLatestCheckResult picks the last ai-review entry, not the first', () => {
      const results: CheckResult[] = [
        { name: 'ai-review', status: 'fail', output: { verdict: 'FAIL' } },
        { name: 'some-other', status: 'pass' },
        { name: 'ai-review', status: 'pass', output: { verdict: 'PASS' } },
      ];

      const latest = getLatestCheckResult(results, 'ai-review');
      expect(latest).toBeDefined();
      expect(latest!.status).toBe('pass');
      expect((latest!.output as any).verdict).toBe('PASS');
    });

    it('replaceCurrentAiReviewTruth deduplicates to the latest ai-review only', () => {
      const results: CheckResult[] = [
        { name: 'ai-review', status: 'fail', output: { verdict: 'FAIL' } },
        { name: 'some-other', status: 'pass' },
        { name: 'ai-review', status: 'pass', output: { verdict: 'PASS' } },
      ];

      const deduped = replaceCurrentAiReviewTruth([...results]);

      const aiReviewEntries = deduped.filter(r => r.name === 'ai-review');
      expect(aiReviewEntries).toHaveLength(1);
      expect(aiReviewEntries[0].status).toBe('pass');
    });

    it('buildAuthoritativeAiReviewResult builds a result with snapshot metadata', () => {
      const checkResult: CheckResult = {
        name: 'ai-review',
        status: 'pass',
        output: {
          verdict: 'PASS',
          reviewReport: PASS_REVIEW_REPORT,
        },
      };

      const result = buildAuthoritativeAiReviewResult(checkResult, {
        snapshotSha: 'sha-abc',
        reviewArtifactPath: '/tmp/review.md',
        selfCheckArtifactPath: '/tmp/review-self-check.md',
      });

      expect(result).toBeDefined();
      expect(result!.verdict).toBe('PASS');
      expect(result!.reviewReport).toBe(PASS_REVIEW_REPORT);
      expect(result!.snapshotSha).toBe('sha-abc');
      expect(result!.reviewArtifactPath).toBe('/tmp/review.md');
      expect(result!.selfCheckArtifactPath).toBe('/tmp/review-self-check.md');
      expect(result!.convergedAt).toBeDefined();
    });
  });

  describe('Approval guard: non-PASS verdict blocks approval even at user-approval check', () => {
    it('approval is not requested when latest ai-review is FAIL', async () => {
      const aiReviewCheck: Check = {
        name: 'ai-review',
        run: async () => ({
          name: 'ai-review',
          status: 'pass' as const,
          message: 'Parsed stale artifact',
          output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
        }),
      };

      const userApprovalCheck = new PendingCheck();
      const approvalCalls: unknown[] = [];

      ctx = makeContext({
        issueRepo: {
          updateStage: vi.fn(),
          setApprovalState: vi.fn().mockImplementation((_id: string, state: unknown) => {
            approvalCalls.push(state);
          }),
          clearApprovalState: vi.fn(),
          updateStatus: vi.fn(),
          findById: vi.fn(),
        } as unknown as IssueRepo,
      });

      const runner = new TestStageRunner({
        checks: [aiReviewCheck, userApprovalCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        approvalCheckNames: ['user-approval'],
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('latest AI review verdict is FAIL');
      expect(approvalCalls).toHaveLength(0);
    });
  });
});
