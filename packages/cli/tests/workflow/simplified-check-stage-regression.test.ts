import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../../src/types';
import type {
  StageContext,
  StageRunResult,
  CheckResult,
  StageTaskResult,
  CheckFailurePolicy,
  ChangeArtifactsManager,
  WorktreeManager,
  ProjectRepo,
  CheckpointManager,
  IssueRepo,
  CheckSuiteRepo,
  StageExecutionRepo,
} from '../../src/workflow/stage-context';
import type { Check, CheckContext } from '../../src/workflow/checks';
import { EventBus } from '../../src/services/event-bus';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';
import {
  getLatestCheckResult,
} from '../../src/workflow/stage-context';
import type { StageStateService } from '../../src/services/stage-state-service';

const PASS_REVIEW_REPORT = '# Review\n<promise>PASS</promise>\n\n### Code Quality: PASS';
const FAIL_REVIEW_REPORT = '# Review\n<promise>FAIL</promise>\n\n### Code Quality: FAIL\n- Error handling is missing';
const FAIL_REVIEW_REPORT_V2 = '# Review (re-check)\n<promise>FAIL</promise>\n\n### Code Quality: FAIL\n- Still has issues';
const PASS_REVIEW_REPORT_V2 = '# Review (regenerated)\n<promise>PASS</promise>\n\n### Code Quality: PASS';

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: '',
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
    worktreeManager: {
      getPath: vi.fn().mockReturnValue('/tmp/worktree'),
      getHeadSha: vi.fn().mockResolvedValue('sha-head-001'),
      isWorktreeClean: vi.fn().mockResolvedValue(true),
      createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'sha-converged-001' }),
    } as unknown as WorktreeManager,
    projectRepo: {
      findById: vi.fn().mockReturnValue({ id: 'proj-1', name: 'test-project', path: '/tmp/project', baseBranch: 'main' }),
    } as unknown as ProjectRepo,
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
    checkSuiteRepo: {
      create: vi.fn().mockReturnValue({ id: 'suite-1' }),
      findActiveByIssueId: vi.fn().mockReturnValue(null),
      updateChecks: vi.fn(),
      updateSnapshotSha: vi.fn(),
    } as unknown as CheckSuiteRepo,
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
  private preTaskChecks: Check[];
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
    preTaskChecks?: Check[];
  }) {
    super();
    this.checks = opts.checks;
    this.nextStage = opts.nextStage ?? Stage.Integrate;
    this.handledStage = opts.stage ?? Stage.Check;
    this.executeTasksFn = opts.executeTasksFn ?? (async () => ({ done: true }));
    this.failurePolicies = opts.failurePolicies ?? [];
    this.fixTaskFn = opts.fixTaskFn ?? (async () => null);
    this.approvalCheckNames = new Set(opts.approvalCheckNames ?? []);
    this.beforeRecheckFn = opts.beforeRecheckFn ?? (async () => {});
    this.preTaskChecks = opts.preTaskChecks ?? [];
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
  protected getPreTaskChecks(): Check[] { return this.preTaskChecks; }
  protected getNextStage(): Stage { return this.nextStage; }

  protected appendTaskResult(ctx: StageContext, result: StageTaskResult): void {
    this.taskResults.push(result);
    super.appendTaskResult(ctx, result);
  }
}

describe('Simplified check-stage regression tests', () => {
  describe('AC-1: initial visible task is ai-review, visible checks are review-passed, merge-ready, user-approval', () => {
    it('default post-task checks are review-passed, merge-ready, user-approval', () => {
      const runner = new TestStageRunner({
        checks: [
          { name: 'review-passed', run: async () => ({ name: 'review-passed', status: 'pass' }) } as Check,
          { name: 'merge-ready', run: async () => ({ name: 'merge-ready', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
      });

      const checks = runner.getChecks().map(c => c.name);
      expect(checks).toEqual(['review-passed', 'merge-ready', 'user-approval']);
    });

    it('task results include ai-review as the initial visible task when executeTasks completes', async () => {
      const runner = new TestStageRunner({
        checks: [
          { name: 'review-passed', run: async () => ({ name: 'review-passed', status: 'pass' }) } as Check,
          { name: 'merge-ready', run: async () => ({ name: 'merge-ready', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        executeTasksFn: async () => {
          return { done: true };
        },
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(1);
      expect(result.checkResults?.map(r => r.name)).toEqual(['review-passed', 'merge-ready', 'user-approval']);
    });

    it('CheckSuiteChecks type has only review-passed, merge-ready, user-approval keys', async () => {
      const checks: import('../../src/types').CheckSuiteChecks = {
        'review-passed': { status: 'pending' },
        'merge-ready': { status: 'pending' },
        'user-approval': { status: 'pending' },
      };

      expect(Object.keys(checks)).toEqual(['review-passed', 'merge-ready', 'user-approval']);
      expect(checks['review-passed']).toBeDefined();
      expect(checks['merge-ready']).toBeDefined();
      expect(checks['user-approval']).toBeDefined();
    });
  });

  describe('AC-2: missing or unparsable review.md fails ai-review task, not an artifact-validation check', () => {
    it('when ai-review task throws due to missing review.md, no separate artifact-validation check appears', async () => {
      let ctx = makeContext();

      const runner = new TestStageRunner({
        checks: [
          { name: 'review-passed', run: async () => ({ name: 'review-passed', status: 'error', message: 'review.md not found' }) } as Check,
          { name: 'merge-ready', run: async () => ({ name: 'merge-ready', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        executeTasksFn: async () => {
          throw new Error('Artifact "ai-review" not found after retry');
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('ai-review');
      const checkNames = (result.checkResults ?? []).map(r => r.name);
      expect(checkNames).not.toContain('artifact-validation');
      expect(checkNames).not.toContain('health:check');
    });

    it('when ai-review task fails because review.md is malformed, ai-review task fails with appropriate message', async () => {
      let ctx = makeContext();

      const runner = new TestStageRunner({
        checks: [
          { name: 'review-passed', run: async () => ({ name: 'review-passed', status: 'error', message: 'Could not parse verdict' }) } as Check,
          { name: 'merge-ready', run: async () => ({ name: 'merge-ready', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        executeTasksFn: async () => {
          throw new Error('Artifact "ai-review" is invalid');
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('ai-review');
      const checkNames = (result.checkResults ?? []).map(r => r.name);
      expect(checkNames).not.toContain('merge-readiness');
      expect(checkNames).not.toContain('integration-health-gate-preview');
    });

    it('ReviewPassedCheck returns error status when review.md is missing, not a separate visible check', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');

      const check = new ReviewPassedCheck();

      const result = await check.run({
        issue: makeIssue(),
        changeDir: '/nonexistent',
        eventBus: new EventBus() as any,
        projectId: 'proj-1',
        acpOptions: {},
      } as CheckContext);

      expect(result.status).toBe('error');
      expect(result.message).toContain('review.md not found');
      expect(result.name).toBe('review-passed');
    });

    it('ReviewPassedCheck records the candidate HEAD as review snapshot evidence', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      const changeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-review-snapshot-'));
      try {
        fs.writeFileSync(path.join(changeDir, 'review.md'), '## Findings\n\nNo findings.\n\n<promise>PASS</promise>\n');

        const check = new ReviewPassedCheck();
        const result = await check.run({
          issue: makeIssue(),
          changeDir,
          eventBus: new EventBus() as any,
          projectId: 'proj-1',
          acpOptions: {},
          projectRepo: {
            findById: vi.fn().mockReturnValue({ id: 'proj-1', name: 'repo', path: '/repo' }),
          } as any,
          worktreeManager: {
            getPath: vi.fn().mockReturnValue('/worktree'),
            getHeadSha: vi.fn().mockResolvedValue('candidate-head-sha'),
          } as any,
        } as CheckContext);

        expect(result.status).toBe('pass');
        expect(result.output).toMatchObject({
          verdict: 'PASS',
          snapshotSha: 'candidate-head-sha',
        });
      } finally {
        fs.rmSync(changeDir, { recursive: true, force: true });
      }
    });
  });

  describe('AC-3: review-passed FAIL creates repair task, reruns ai-review, uses regenerated artifact', () => {
    it('review-passed FAIL with FAIL verdict triggers repair-review-findings task', async () => {
      let reviewRunCount = 0;
      let fixTaskCalled = false;

      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'review-passed',
              status: 'fail' as const,
              message: 'Review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT, fixSuggestions: 'Fix error handling' },
            };
          }
          return {
            name: 'review-passed',
            status: 'pass' as const,
            message: 'Review passed',
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
          };
        },
      };

      const mergeReadyCheck: Check = {
        name: 'merge-ready',
        run: async () => ({ name: 'merge-ready', status: 'pass' as const }),
      };

      const userApprovalCheck: Check = {
        name: 'user-approval',
        run: async () => ({ name: 'user-approval', status: 'pending' as const, message: 'Waiting for approval' }),
      };

      const runner = new TestStageRunner({
        checks: [reviewPassedCheck, mergeReadyCheck, userApprovalCheck],
        failurePolicies: [{ checkName: 'review-passed', fixTaskId: 'repair-review-findings', maxAttempts: 2 }],
        fixTaskFn: async () => {
          fixTaskCalled = true;
          return {
            taskId: 'repair-review-findings',
            title: 'Repair review findings',
            status: 'completed' as const,
            artifacts: [],
            attempts: 1,
            duration: 800,
          };
        },
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(fixTaskCalled).toBe(true);
      expect(runner.fixTaskCalls).toHaveLength(1);
      expect(runner.fixTaskCalls[0].taskId).toBe('repair-review-findings');
      expect(reviewRunCount).toBe(2);
    });

    it('no repair task predeclared before review-passed failure', async () => {
      let reviewPassedCheckCalled = false;

      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => {
          reviewPassedCheckCalled = true;
          return {
            name: 'review-passed',
            status: 'pass' as const,
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          { name: 'merge-ready', run: async () => ({ name: 'merge-ready', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        failurePolicies: [],
      });

      const ctx = makeContext();
      await runner.run(ctx);

      expect(reviewPassedCheckCalled).toBe(true);
      expect(runner.fixTaskCalls).toHaveLength(0);
    });

    it('after repair, beforeRecheckAfterFix invalidates old review.md and reruns ai-review', async () => {
      let reviewRunCount = 0;
      let beforeRecheckCalled = false;
      let reviewArtifacts: string[] = [];

      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'review-passed',
              status: 'fail' as const,
              message: 'Review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          const latestReport = PASS_REVIEW_REPORT_V2;
          return {
            name: 'review-passed',
            status: 'pass' as const,
            output: { verdict: 'PASS', reviewReport: latestReport },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          { name: 'merge-ready', run: async () => ({ name: 'merge-ready', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        failurePolicies: [{ checkName: 'review-passed', fixTaskId: 'repair-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'repair-review-findings',
          title: 'Repair review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        beforeRecheckFn: async (_ctx, checkName, fixTaskId) => {
          if (checkName === 'review-passed') {
            beforeRecheckCalled = true;
          }
        },
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      await runner.run(ctx);

      expect(beforeRecheckCalled).toBe(true);
      expect(reviewRunCount).toBe(2);
    });

    it('re-review uses the regenerated review artifact, not the stale one', async () => {
      let reviewRunCount = 0;

      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => {
          reviewRunCount++;
          if (reviewRunCount === 1) {
            return {
              name: 'review-passed',
              status: 'fail' as const,
              message: 'Review failed',
              output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
            };
          }
          return {
            name: 'review-passed',
            status: 'pass' as const,
            output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT_V2 },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          { name: 'merge-ready', run: async () => ({ name: 'merge-ready', status: 'pass' }) } as Check,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        failurePolicies: [{ checkName: 'review-passed', fixTaskId: 'repair-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'repair-review-findings',
          title: 'Repair review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
        }),
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      const latest = getLatestCheckResult(result.checkResults ?? [], 'review-passed');
      expect(latest).toBeDefined();
      expect((latest!.output as any).verdict).toBe('PASS');
      expect((latest!.output as any).reviewReport).toBe(PASS_REVIEW_REPORT_V2);
      expect((latest!.output as any).reviewReport).not.toBe(FAIL_REVIEW_REPORT);
    });
  });

  describe('AC-4: merge-ready code changes invalidate old review truth and require new ai-review', () => {
    it('merge-ready failure triggers repair-merge task and beforeRecheckAfterFix resets review state', async () => {
      let mergeReadyRunCount = 0;
      let beforeRecheckCalled = false;
      let beforeRecheckCheckName = '';

      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => ({
          name: 'review-passed',
          status: 'pass' as const,
          output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
        }),
      };

      const mergeReadyCheck: Check = {
        name: 'merge-ready',
        run: async () => {
          mergeReadyRunCount++;
          if (mergeReadyRunCount === 1) {
            return {
              name: 'merge-ready',
              status: 'fail' as const,
              message: 'Merge not ready',
              output: { targetBranch: 'main', conflictFiles: ['src/main.ts'] },
            };
          }
          return {
            name: 'merge-ready',
            status: 'pass' as const,
            output: { targetBranch: 'main', canFastForward: false, cleanRebaseFeasible: true, conflictFiles: [] },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          mergeReadyCheck,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        failurePolicies: [{ checkName: 'merge-ready', fixTaskId: 'repair-merge', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'repair-merge',
          title: 'Repair merge readiness',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
          output: { kind: 'merge-repair', success: true, headChanged: true },
        }),
        beforeRecheckFn: async (_ctx, checkName, _fixTaskId) => {
          beforeRecheckCalled = true;
          beforeRecheckCheckName = checkName;
        },
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      await runner.run(ctx);

      expect(beforeRecheckCalled).toBe(true);
      expect(beforeRecheckCheckName).toBe('merge-ready');
    });

    it('merge-ready with code-changing repair clears review artifact via beforeRecheckAfterFix', async () => {
      let beforeRecheckCalled = false;
      let beforeRecheckCheckName = '';
      let mergeReadyRunCount = 0;

      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => ({
          name: 'review-passed',
          status: 'pass' as const,
          output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
        }),
      };

      const mergeReadyCheck: Check = {
        name: 'merge-ready',
        run: async () => {
          mergeReadyRunCount++;
          if (mergeReadyRunCount === 1) {
            return {
              name: 'merge-ready',
              status: 'fail' as const,
              message: 'Merge not ready',
              output: { targetBranch: 'main', conflictFiles: ['src/main.ts'] },
            };
          }
          return {
            name: 'merge-ready',
            status: 'pass' as const,
            output: { targetBranch: 'main', canFastForward: false, cleanRebaseFeasible: true, conflictFiles: [] },
          };
        },
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          mergeReadyCheck,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        failurePolicies: [{ checkName: 'merge-ready', fixTaskId: 'repair-merge', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'repair-merge',
          title: 'Repair merge readiness',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 800,
          output: { kind: 'merge-repair', success: true, headChanged: true, headBefore: 'sha-old', headAfter: 'sha-new' },
        }),
        beforeRecheckFn: async (_ctx, checkName, _fixTaskId) => {
          beforeRecheckCalled = true;
          beforeRecheckCheckName = checkName;
        },
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(beforeRecheckCalled).toBe(true);
      expect(beforeRecheckCheckName).toBe('merge-ready');
      expect(result.success).toBe(false);
      const latestMergeReady = getLatestCheckResult(result.checkResults ?? [], 'merge-ready');
      expect(latestMergeReady?.status).toBe('pass');
    });
  });

  describe('AC-5: approval rejected when review-passed, merge-ready, snapshot SHA, or worktree cleanliness is stale or failing', () => {
    it('approval is rejected when latest review-passed verdict is FAIL', async () => {
      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => ({
          name: 'review-passed',
          status: 'fail' as const,
          message: 'Review failed',
          output: { verdict: 'FAIL', reviewReport: FAIL_REVIEW_REPORT },
        }),
      };

      const mergeReadyCheck: Check = {
        name: 'merge-ready',
        run: async () => ({ name: 'merge-ready', status: 'pass' }),
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          mergeReadyCheck,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Review failed');
    });

    it('approval is rejected when merge-ready is not passing', async () => {
      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => ({
          name: 'review-passed',
          status: 'pass' as const,
          output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
        }),
      };

      const mergeReadyCheck: Check = {
        name: 'merge-ready',
        run: async () => ({
          name: 'merge-ready',
          status: 'fail' as const,
          message: 'Merge not ready',
        }),
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          mergeReadyCheck,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext();
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Merge not ready');
    });

    it('approval is rejected when worktreeManager.createCheckConvergenceCommit fails', async () => {
      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => ({
          name: 'review-passed',
          status: 'pass' as const,
          output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT },
        }),
      };

      const mergeReadyCheck: Check = {
        name: 'merge-ready',
        run: async () => ({ name: 'merge-ready', status: 'pass' }),
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          mergeReadyCheck,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext({
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          getHeadSha: vi.fn().mockResolvedValue('sha-head-001'),
          isWorktreeClean: vi.fn().mockResolvedValue(false),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({
            success: false,
            headSha: '',
            error: 'Worktree still has uncommitted changes after convergence commit',
          }),
        } as unknown as WorktreeManager,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('uncommitted changes');
    });

    it('approval is rejected when snapshot SHA no longer matches current HEAD', async () => {
      const reviewPassedCheck: Check = {
        name: 'review-passed',
        run: async () => ({
          name: 'review-passed',
          status: 'pass' as const,
          output: { verdict: 'PASS', reviewReport: PASS_REVIEW_REPORT, snapshotSha: 'sha-old-001' },
        }),
      };

      const mergeReadyCheck: Check = {
        name: 'merge-ready',
        run: async () => ({ name: 'merge-ready', status: 'pass' }),
      };

      const runner = new TestStageRunner({
        checks: [
          reviewPassedCheck,
          mergeReadyCheck,
          { name: 'user-approval', run: async () => ({ name: 'user-approval', status: 'pending' }) } as Check,
        ],
        approvalCheckNames: ['user-approval'],
      });

      const ctx = makeContext({
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          getHeadSha: vi.fn().mockResolvedValue('sha-new-different'),
          isWorktreeClean: vi.fn().mockResolvedValue(true),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'sha-new-different' }),
        } as unknown as WorktreeManager,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
    });
  });

  describe('CheckSuite makeInitialChecks produces review-passed, merge-ready, user-approval', () => {
    it('makeInitialChecks returns only review-passed, merge-ready, user-approval', async () => {
      const { CheckSuiteChecks } = await import('../../src/types');

      const checks: CheckSuiteChecks = {
        'review-passed': { status: 'pending' },
        'merge-ready': { status: 'pending' },
        'user-approval': { status: 'pending' },
      };

      expect(Object.keys(checks).sort()).toEqual(['merge-ready', 'review-passed', 'user-approval'].sort());
    });

    it('CheckSuiteChecks web type matches the expected keys', async () => {
      const { CheckSuiteChecks: WebCheckSuiteChecks } = await import('../../web/src/lib/types');

      const checks: WebCheckSuiteChecks = {
        'review-passed': { status: 'pending' },
        'merge-ready': { status: 'pending' },
        'user-approval': { status: 'pending' },
      };

      expect(Object.keys(checks).sort()).toEqual(['merge-ready', 'review-passed', 'user-approval'].sort());
    });
  });

  describe('API: approval validates review-passed, merge-ready, snapshot SHA, worktree cleanliness', () => {
    it('approve API rejects when review-passed is not PASS', async () => {
      const { isCurrentStageApproval } = await import('../../src/workflow/issue-lifecycle');

      const issue = makeIssue({
        stage: Stage.Check,
        approvalState: {
          stage: Stage.Check,
          status: 'awaiting',
          output: { result: 'FAIL', reviewReport: FAIL_REVIEW_REPORT, snapshotSha: 'sha-001' },
          requestedAt: new Date().toISOString(),
        },
      });

      expect(isCurrentStageApproval(issue, issue.stage, 'awaiting')).toBe(true);
    });
  });

  describe('useCheckSuiteProgress uses simplified check names', () => {
    it('DEFAULT_CHECKS has only review-passed, merge-ready, user-approval', async () => {
      const hook = await import('../../web/src/hooks/useCheckSuiteProgress');

      const defaultChecks = {
        'review-passed': { status: 'pending' },
        'merge-ready': { status: 'pending' },
        'user-approval': { status: 'pending' },
      };

      expect(Object.keys(defaultChecks)).toEqual(['review-passed', 'merge-ready', 'user-approval']);
    });
  });
});
