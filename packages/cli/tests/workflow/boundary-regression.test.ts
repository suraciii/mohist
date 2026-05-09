import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage } from '../../src/types';
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
} from '../../src/workflow/stage-context';
import type { Check, CheckContext } from '../../src/workflow/checks';
import { EventBus } from '../../src/services/event-bus';
import { BaseStageRunner } from '../../src/workflow/base-stage-runner';

class PassCheck implements Check {
  name: string;
  constructor(name: string) { this.name = name; }
  async run(): Promise<CheckResult> { return { name: this.name, status: 'pass' }; }
}

class FailCheck implements Check {
  name: string;
  runFn: () => Promise<CheckResult>;
  constructor(name: string, runFn?: () => Promise<CheckResult>) {
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
  fixTaskCalls: { taskId: string; failedCheck: CheckResult; attempt: number }[] = [];

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

describe('Task/Check/Artifact boundary regression', () => {
  let ctx: StageContext;

  beforeEach(() => {
    ctx = makeContext();
  });

  describe('AC-1: Check interface has no fix and executeTasks is not re-run', () => {
    it('Check interface does not expose fix() method', () => {
      const check: Check = {
        name: 'test-check',
        run: async () => ({ name: 'test-check', status: 'pass' }),
      };
      expect((check as any).fix).toBeUndefined();
      expect((check as any).reaction).toBeUndefined();
    });

    it('does not re-run executeTasks when a check fails without a policy', async () => {
      const runner = new TestStageRunner({
        checks: [new FailCheck('failing-check')],
        nextStage: Stage.Build,
      });

      await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(1);
    });

    it('does not re-run executeTasks when a check fails with a fix policy', async () => {
      const runner = new TestStageRunner({
        checks: [new FailCheck('health:build')],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-build-health',
          title: 'Fix build health',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 100,
        }),
      });

      await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(1);
    });

    it('never calls check.fix() because the interface does not define it', async () => {
      const checkWithFix = {
        name: 'rogue-check',
        run: vi.fn().mockResolvedValue({ name: 'rogue-check', status: 'fail' }),
        fix: vi.fn().mockResolvedValue(undefined),
      };

      const runner = new TestStageRunner({
        checks: [checkWithFix as any],
        nextStage: Stage.Build,
        failurePolicies: [{ checkName: 'rogue-check', fixTaskId: 'fix-rogue', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-rogue',
          title: 'Fix rogue',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 100,
        }),
      });

      await runner.run(ctx);

      expect(checkWithFix.run).toHaveBeenCalled();
      expect(checkWithFix.fix).not.toHaveBeenCalled();
    });
  });

  describe('AC-2: Failed health check -> explicit fix task -> re-check', () => {
    it('runs fix-build-health and re-runs health:build after fix succeeds', async () => {
      let checkRunCount = 0;
      const healthCheck = new FailCheck(
        'health:build',
        async () => {
          checkRunCount++;
          if (checkRunCount <= 1) {
            return { name: 'health:build', status: 'fail', message: 'npm run build failed', output: { kind: 'health-gate', logExcerpt: 'TS error' } };
          }
          return { name: 'health:build', status: 'pass' };
        },
      );

      let fixTaskCalled = false;
      const runner = new TestStageRunner({
        checks: [healthCheck],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 2 }],
        fixTaskFn: async (taskId, failedCheck) => {
          fixTaskCalled = true;
          expect(taskId).toBe('fix-build-health');
          expect(failedCheck.name).toBe('health:build');
          return { taskId: 'fix-build-health', title: 'Fix build health', status: 'completed', artifacts: [], attempts: 1, duration: 500 };
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(fixTaskCalled).toBe(true);
      expect(checkRunCount).toBe(2);
      expect(runner.fixTaskCalls).toHaveLength(1);
      expect(runner.fixTaskCalls[0].taskId).toBe('fix-build-health');
    });

    it('fix task result has empty artifacts array', async () => {
      const healthCheck = new FailCheck(
        'health:build',
        async () => {
          return { name: 'health:build', status: 'fail', message: 'build failed' };
        },
      );

      const fixResult: StageTaskResult = {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 500,
        output: { kind: 'health-fix-task', stage: 'build', success: true },
      };

      const runner = new TestStageRunner({
        checks: [healthCheck],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 1 }],
        fixTaskFn: async () => fixResult,
      });

      await runner.run(ctx);

      expect(runner.fixTaskCalls).toHaveLength(1);
      const returnedFix = await (runner as any).fixTaskFn('fix-build-health', { name: 'health:build', status: 'fail' }, 1);
      expect(returnedFix.artifacts).toEqual([]);
    });

    it('check result evidence is transient output, not artifacts', async () => {
      const healthCheck = new FailCheck(
        'health:build',
        async () => ({
          name: 'health:build',
          status: 'fail',
          message: 'npm run build failed',
          output: {
            kind: 'health-gate',
            stage: 'build',
            command: 'npm run build',
            exitCode: 1,
            logExcerpt: 'TypeScript error in foo.ts',
          },
        }),
      );

      const runner = new TestStageRunner({
        checks: [healthCheck],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-build-health',
          title: 'Fix build health',
          status: 'failed' as const,
          artifacts: [],
          attempts: 1,
          duration: 100,
        }),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const checkResult = result.checkResults[0];
      expect(checkResult.output).toBeDefined();
      expect((checkResult.output as any).kind).toBe('health-gate');
      expect((checkResult.output as any).logExcerpt).toBe('TypeScript error in foo.ts');
    });
  });

  describe('AC-3: Failed AI review -> fix-review-findings -> re-check', () => {
    it('runs fix-review-findings and re-runs ai-review after fix succeeds', async () => {
      let reviewRunCount = 0;
      const reviewCheck = new FailCheck(
        'ai-review',
        async () => {
          reviewRunCount++;
          if (reviewRunCount <= 1) {
            return {
              name: 'ai-review',
              status: 'fail',
              message: 'AI review failed',
              output: {
                verdict: 'FAIL',
                reviewReport: '# Review\n<promise>FAIL</promise>',
                fixSuggestions: 'Fix the error handling',
              },
            };
          }
          return { name: 'ai-review', status: 'pass' };
        },
      );

      let fixCalled = false;
      const runner = new TestStageRunner({
        checks: [reviewCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async (taskId, failedCheck) => {
          fixCalled = true;
          expect(taskId).toBe('fix-review-findings');
          expect(failedCheck.name).toBe('ai-review');
          expect((failedCheck.output as any).verdict).toBe('FAIL');
          return {
            taskId: 'fix-review-findings',
            title: 'Fix review findings',
            status: 'completed',
            artifacts: [],
            attempts: 1,
            duration: 800,
            output: { kind: 'review-fix-task', success: true },
          };
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(fixCalled).toBe(true);
      expect(reviewRunCount).toBe(2);
      expect(runner.fixTaskCalls).toHaveLength(1);
      expect(runner.fixTaskCalls[0].taskId).toBe('fix-review-findings');
    });

    it('fix-review-findings has empty artifacts because it changes code not workflow files', async () => {
      const reviewCheck = new FailCheck(
        'ai-review',
        async () => ({
          name: 'ai-review',
          status: 'fail',
          message: 'AI review failed',
          output: { verdict: 'FAIL', reviewReport: '# Review\n<promise>FAIL</promise>' },
        }),
      );

      const fixResult: StageTaskResult = {
        taskId: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 800,
        output: { kind: 'review-fix-task', success: true, acpSessionId: 'ses-1' },
      };

      const runner = new TestStageRunner({
        checks: [reviewCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => fixResult,
      });

      await runner.run(ctx);

      expect(fixResult.artifacts).toEqual([]);
    });

    it('re-check preserves failed result history and appends the follow-up result', async () => {
      let reviewRunCount = 0;
      const persistedCheckResults: CheckResult[][] = [];
      ctx = makeContext({
        stageExecutionRepo: {
          create: vi.fn().mockReturnValue({ id: 'exec-1' }),
          updateCheckResults: vi.fn().mockImplementation((_id: string, checkResults: CheckResult[]) => {
            persistedCheckResults.push(checkResults.map(checkResult => ({ ...checkResult })));
            return null;
          }),
          appendTaskResult: vi.fn(),
          updateStatus: vi.fn(),
        } as any,
      });

      const reviewCheck = new FailCheck(
        'ai-review',
        async () => {
          reviewRunCount++;
          if (reviewRunCount <= 1) {
            return { name: 'ai-review', status: 'fail', message: 'FAIL verdict' };
          }
          return { name: 'ai-review', status: 'pass' };
        },
      );

      const runner = new TestStageRunner({
        checks: [reviewCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => ({
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed',
          artifacts: [],
          attempts: 1,
          duration: 100,
        }),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(reviewRunCount).toBe(2);
      expect(runner.fixTaskCalls).toHaveLength(1);
      expect(runner.fixTaskCalls[0].taskId).toBe('fix-review-findings');
      expect(result.checkResults.filter(r => r.name === 'ai-review')).toEqual([
        expect.objectContaining({ name: 'ai-review', status: 'fail' }),
        expect.objectContaining({ name: 'ai-review', status: 'pass' }),
      ]);
      expect(persistedCheckResults).toHaveLength(2);
      expect(persistedCheckResults[0]).toHaveLength(1);
      expect(persistedCheckResults[0][0]).toMatchObject({ name: 'ai-review', status: 'fail' });
      expect(persistedCheckResults[1]).toHaveLength(2);
      expect(persistedCheckResults[1]).toEqual([
        expect.objectContaining({ name: 'ai-review', status: 'fail' }),
        expect.objectContaining({ name: 'ai-review', status: 'pass' }),
      ]);
    });
  });

  describe('AC-4: Build task results with empty durable artifact lists', () => {
    it('completed task result with artifacts:[] is valid', () => {
      const result: StageTaskResult = {
        taskId: 'T-002',
        title: 'Implement feature X',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 45000,
        output: { changedFiles: ['src/foo.ts', 'src/bar.ts'] },
      };

      expect(result.status).toBe('completed');
      expect(result.artifacts).toEqual([]);
      expect(result.output).toBeDefined();
    });

    it('build fix task result with artifacts:[] is valid', () => {
      const result: StageTaskResult = {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 30000,
        output: { kind: 'health-fix-task', stage: 'build', success: true },
      };

      expect(result.status).toBe('completed');
      expect(result.artifacts).toEqual([]);
    });

    it('task output field is optional and does not break results without it', () => {
      const result: StageTaskResult = {
        taskId: 'T-001',
        title: 'Legacy task',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 100,
      };

      expect(result.output).toBeUndefined();
      expect(result.status).toBe('completed');
    });

    it('multiple empty-artifact build task results are all valid', () => {
      const results: StageTaskResult[] = [
        { taskId: 'T-001', title: 'Task A', status: 'completed', artifacts: [], attempts: 1, duration: 100 },
        { taskId: 'T-002', title: 'Task B', status: 'completed', artifacts: [], attempts: 1, duration: 200 },
        { taskId: 'T-003', title: 'Task C', status: 'completed', artifacts: [], attempts: 1, duration: 300, output: { log: 'ok' } },
      ];

      for (const r of results) {
        expect(r.status).toBe('completed');
        expect(r.artifacts).toEqual([]);
      }
    });
  });

  describe('AC-5: Durable artifact preservation for plan and review reports', () => {
    it('plan task results preserve durable artifact paths', () => {
      const planTaskResults: StageTaskResult[] = [
        { taskId: 'proposal', title: 'proposal.md', status: 'completed', artifacts: ['proposal.md'], attempts: 1, duration: 1000 },
        { taskId: 'specs', title: 'specs/', status: 'completed', artifacts: ['specs/'], attempts: 1, duration: 2000 },
        { taskId: 'design', title: 'design.md', status: 'completed', artifacts: ['design.md'], attempts: 1, duration: 1500 },
        { taskId: 'tasks', title: 'tasks.json', status: 'completed', artifacts: ['tasks.json'], attempts: 1, duration: 800 },
        { taskId: 'self-review', title: 'self-review.md', status: 'completed', artifacts: ['self-review.md'], attempts: 1, duration: 500 },
      ];

      for (const r of planTaskResults) {
        expect(r.artifacts.length).toBeGreaterThan(0);
        expect(r.artifacts[0]).not.toBe('');
      }
    });

    it('review task results preserve durable artifact paths', () => {
      const reviewTaskResults: StageTaskResult[] = [
        { taskId: 'review', title: 'review.md', status: 'completed', artifacts: ['review.md'], attempts: 1, duration: 1000 },
        { taskId: 'review-self-check', title: 'review-self-check.md', status: 'completed', artifacts: ['review-self-check.md'], attempts: 1, duration: 500 },
      ];

      for (const r of reviewTaskResults) {
        expect(r.artifacts.length).toBeGreaterThan(0);
      }
    });

    it('repair-plan-artifacts only lists durable workflow files in artifacts', () => {
      const repairResult: StageTaskResult = {
        taskId: 'repair-plan-artifacts',
        title: 'Repair plan artifacts',
        status: 'completed',
        artifacts: ['proposal.md', 'design.md'],
        attempts: 1,
        duration: 2000,
        output: { kind: 'plan-repair-task', repairedArtifacts: ['proposal.md', 'design.md'] },
      };

      const durableArtifacts = ['proposal.md', 'specs/', 'specs', 'design.md', 'tasks.json', 'self-review.md', 'review.md', 'review-self-check.md'];
      for (const artifact of repairResult.artifacts) {
        expect(durableArtifacts).toContain(artifact);
      }
    });

    it('transient outputs like build logs are not in artifacts', () => {
      const taskResult: StageTaskResult = {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 5000,
        output: {
          kind: 'health-fix-task',
          stage: 'build',
          checkName: 'health:build',
          healthCommand: 'npm run build',
          success: true,
          summary: 'Fix completed',
          logExcerpt: 'TypeScript error in foo.ts line 42',
        },
      };

      expect(taskResult.artifacts).toEqual([]);
      expect((taskResult.output as any).logExcerpt).toBeDefined();
      expect(taskResult.artifacts).not.toContain('build.log');
      expect(taskResult.artifacts).not.toContain('stdout.txt');
    });
  });

  describe('AC-6: Max fix attempts stop current stage without fallback escalation', () => {
    it('fails after maxAttempts without fallback chain', async () => {
      let fixCount = 0;
      const alwaysFail = new FailCheck(
        'health:build',
        async () => ({ name: 'health:build', status: 'fail', message: 'still broken' }),
      );

      const runner = new TestStageRunner({
        checks: [alwaysFail],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 2 }],
        fixTaskFn: async () => {
          fixCount++;
          return { taskId: 'fix-build-health', title: 'Fix build health', status: 'completed', artifacts: [], attempts: fixCount, duration: 100 };
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBeUndefined();
      expect(result.nextStage).toBeUndefined();
      expect(fixCount).toBe(2);
    });

    it('preserves failed check results and fix task results when max attempts exhausted', async () => {
      let checkRuns = 0;
      const alwaysFail = new FailCheck(
        'health:build',
        async () => {
          checkRuns++;
          return { name: 'health:build', status: 'fail', message: `fail #${checkRuns}` };
        },
      );

      const runner = new TestStageRunner({
        checks: [alwaysFail],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 1 }],
        fixTaskFn: async (_taskId, _fc, attempt) => ({
          taskId: 'fix-build-health',
          title: 'Fix build health',
          status: 'completed',
          artifacts: [],
          attempts: attempt,
          duration: 100,
        }),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBeUndefined();
      expect(runner.fixTaskCalls).toHaveLength(1);
      expect(checkRuns).toBe(2);
    });

    it('max attempts for AI review also stops without fallback', async () => {
      const alwaysFailReview = new FailCheck(
        'ai-review',
        async () => ({ name: 'ai-review', status: 'fail', message: 'FAIL verdict' }),
      );

      let fixCalled = false;
      const runner = new TestStageRunner({
        checks: [alwaysFailReview],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
        failurePolicies: [{ checkName: 'ai-review', fixTaskId: 'fix-review-findings', maxAttempts: 1 }],
        fixTaskFn: async () => {
          fixCalled = true;
          return {
            taskId: 'fix-review-findings',
            title: 'Fix review findings',
            status: 'completed',
            artifacts: [],
            attempts: 1,
            duration: 100,
          };
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBeUndefined();
      expect(fixCalled).toBe(true);
    });

    it('no policy means immediate failure without fallback', async () => {
      const failCheck = new FailCheck('unknown-check');

      const runner = new TestStageRunner({
        checks: [failCheck],
        nextStage: Stage.Build,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBeUndefined();
      expect(result.nextStage).toBeUndefined();
      expect(result.message).toContain('unknown-check');
    });

    it('fix task failure stops stage without fallback', async () => {
      const failCheck = new FailCheck(
        'health:build',
        async () => ({ name: 'health:build', status: 'fail', message: 'build failed' }),
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
          output: { error: 'agent crashed' },
        }),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.escalateToStage).toBeUndefined();
      expect(result.message).toContain('Fix build health failed');
    });
  });

  describe('Check result evidence is transient', () => {
    it('health check stores output but never artifacts in CheckResult', async () => {
      const healthCheck = new FailCheck(
        'health:build',
        async () => ({
          name: 'health:build',
          status: 'fail',
          message: 'build failed',
          output: {
            kind: 'health-gate',
            stage: 'build',
            command: 'npm run build',
            exitCode: 1,
            logExcerpt: 'error TS2304',
          },
        }),
      );

      const runner = new TestStageRunner({
        checks: [healthCheck],
        nextStage: Stage.Check,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const check = result.checkResults[0];
      expect(check.output).toBeDefined();
      expect(typeof (check.output as any)).toBe('object');
    });

    it('AI review check stores verdict in output, not artifacts', async () => {
      const reviewCheck = new FailCheck(
        'ai-review',
        async () => ({
          name: 'ai-review',
          status: 'fail',
          message: 'AI review failed',
          output: {
            verdict: 'FAIL',
            reviewReport: '# Review\n<promise>FAIL</promise>',
            fixSuggestions: 'Fix X',
          },
        }),
      );

      const runner = new TestStageRunner({
        checks: [reviewCheck],
        nextStage: Stage.Integrate,
        stage: Stage.Check,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const check = result.checkResults[0];
      expect((check.output as any).verdict).toBe('FAIL');
      expect((check.output as any).reviewReport).toBeDefined();
    });
  });

  describe('Fix task scheduling produces visible task results', () => {
    it('fix task call captures correct attempt numbering', async () => {
      let checkRuns = 0;
      const flaky = new FailCheck(
        'health:build',
        async () => {
          checkRuns++;
          if (checkRuns <= 2) return { name: 'health:build', status: 'fail', message: 'fail' };
          return { name: 'health:build', status: 'pass' };
        },
      );

      const runner = new TestStageRunner({
        checks: [flaky],
        nextStage: Stage.Check,
        failurePolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', maxAttempts: 3 }],
        fixTaskFn: async (_id, _fc, attempt) => ({
          taskId: 'fix-build-health',
          title: 'Fix build health',
          status: 'completed',
          artifacts: [],
          attempts: attempt,
          duration: 100,
        }),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(runner.fixTaskCalls).toHaveLength(2);
      expect(runner.fixTaskCalls[0].attempt).toBe(1);
      expect(runner.fixTaskCalls[1].attempt).toBe(2);
    });
  });
});
