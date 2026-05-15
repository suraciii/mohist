import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Stage, IssueStatus } from '../src/types';
import type { StageContext, CheckResult } from '../src/workflow/stage-context';
import type { Check } from '../src/workflow/checks';
import { EventBus } from '../src/services/event-bus';
import { BaseStageRunner } from '../src/workflow/base-stage-runner';
import { UserApprovalCheck } from '../src/workflow/checks/user-approval-check';
import { loadHealthGatePolicies } from '../src/workflow/workflow-loader';

function makePassCheck(name: string, output?: Record<string, unknown>): Check {
  return {
    name,
    run: async () => ({ name, status: 'pass' as const, message: `${name} passed`, output }),
  };
}

function makeFailCheck(name: string, message = `${name} failed`, output?: Record<string, unknown>): Check {
  return {
    name,
    run: async () => ({ name, status: 'fail' as const, message, output }),
  };
}

function createMockContext(tmpDir: string, issueNumber = 42, overrides?: Partial<StageContext>): StageContext {
  const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
  fs.mkdirSync(changeDir, { recursive: true });

  const emitSpy = vi.fn();
  const eventBus = new EventBus();
  vi.spyOn(eventBus, 'emit').mockImplementation(emitSpy);

  const ctx = {
    issue: {
      id: `issue-${issueNumber}`,
      number: issueNumber,
      title: 'Test Issue',
      body: '',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: {} as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn(),
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn().mockResolvedValue(undefined),
    },
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: eventBus as any,
    checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn() } as any,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as any,
    ...overrides,
  } as StageContext;
  ctx.emit = ctx.emit ?? ((event: string, data: unknown) => {
    try {
      (ctx.eventBus as any)?.emit?.(event, data);
    } catch {
      // fire-and-forget
    }
  });
  return ctx;
}

class TestStageRunner extends BaseStageRunner {
  preTaskChecks: Check[] = [];
  postTaskChecks: Check[] = [];
  nextStage = Stage.Done;
  executeTasksFn = vi.fn().mockResolvedValue({ done: true });
  handledStage = Stage.Check;

  canHandle(s: Stage): boolean { return s === this.handledStage; }

  protected isApprovalCheck(checkName: string): boolean {
    return checkName === 'user-approval';
  }

  protected async executeTasks(): Promise<unknown> {
    return this.executeTasksFn();
  }

  protected getChecks(): Check[] { return this.postTaskChecks; }
  protected getPreTaskChecks(): Check[] { return this.preTaskChecks; }
  protected getNextStage(): Stage { return this.nextStage; }
}

describe('Check verification regression', () => {
  let tmpDir: string;

  beforeEach(() => {
    vi.clearAllMocks();
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'check-verification-regression-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('Failing Check verification blocks AI review, merge-ready, and approval', () => {
    it('health:check failure prevents approval request even when later checks would pass', async () => {
      const healthCheckFails = makeFailCheck(
        'health:check',
        'npm run build && npm test failed (exit code 1)',
        { kind: 'health-gate', stage: 'check', command: 'npm run build && npm test', exitCode: 1, summary: 'Test failed' },
      );
      const aiReviewCheck = makePassCheck('ai-review', { verdict: 'PASS' });
      const mergeReadyCheck = makePassCheck('merge-ready');
      const userApprovalCheck = new UserApprovalCheck(Stage.Check);

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckFails];
      runner.postTaskChecks = [aiReviewCheck, mergeReadyCheck, userApprovalCheck];

      const ctx = createMockContext(tmpDir, 42, {
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', path: '/tmp/project' }),
        } as any,
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'abc123' }),
        } as any,
      });
      const emitSpy = vi.spyOn(ctx.eventBus!, 'emit');
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const healthCheckResult = result.checkResults.find(r => r.name === 'health:check');
      expect(healthCheckResult?.status).toBe('fail');
      const approvalCalls = emitSpy.mock.calls.filter(([event]) => event === 'approval_requested');
      expect(approvalCalls).toHaveLength(0);
      expect(ctx.issueRepo.setApprovalState).not.toHaveBeenCalled();
    });

    it('health:check failure blocks before ai-review runs', async () => {
      const healthCheckFails = makeFailCheck('health:check', 'build failed');
      const aiReviewCheck = makePassCheck('ai-review', { verdict: 'PASS' });
      const aiReviewRun = vi.spyOn(aiReviewCheck, 'run');

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckFails];
      runner.postTaskChecks = [aiReviewCheck];

      const ctx = createMockContext(tmpDir);
      await runner.run(ctx);

      expect(aiReviewRun).not.toHaveBeenCalled();
    });

    it('health:check failure blocks before merge-ready runs', async () => {
      const healthCheckFails = makeFailCheck('health:check', 'build failed');
      const mergeReadyCheck = makePassCheck('merge-ready');
      const mergeReadyRun = vi.spyOn(mergeReadyCheck, 'run');

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckFails];
      runner.postTaskChecks = [mergeReadyCheck];

      const ctx = createMockContext(tmpDir);
      await runner.run(ctx);

      expect(mergeReadyRun).not.toHaveBeenCalled();
    });
  });

  describe('Passing health:check evidence is persisted before review-passed, merge-ready, and approval', () => {
    it('health:check pass result appears first in check results ordering', async () => {
      const healthCheckPasses = makePassCheck('health:check', {
        kind: 'health-gate',
        stage: 'check',
        command: 'npm run build && npm test',
        duration: 45000,
        enabled: true,
      });
      const aiReviewCheck = makePassCheck('ai-review', { verdict: 'PASS' });
      const userApprovalCheck = new UserApprovalCheck(Stage.Check);

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckPasses];
      runner.postTaskChecks = [aiReviewCheck, userApprovalCheck];

      const ctx = createMockContext(tmpDir, 42, {
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', path: '/tmp/project' }),
        } as any,
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'abc123' }),
        } as any,
      });
      const result = await runner.run(ctx);

      expect(result.checkResults.map(r => r.name)).toEqual(['health:check', 'ai-review', 'user-approval']);
      expect(result.checkResults[0].name).toBe('health:check');
      expect(result.checkResults[0].status).toBe('pass');
    });

    it('health:check evidence includes command, status, duration, and summary', async () => {
      const healthCheckPasses = makePassCheck('health:check', {
        kind: 'health-gate',
        stage: 'check',
        command: 'npm run build && npm test',
        duration: 62000,
        enabled: true,
        logExcerpt: 'PASS - all tests passed',
      });

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckPasses];
      runner.postTaskChecks = [];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      const healthCheckResult = result.checkResults.find(r => r.name === 'health:check');
      expect(healthCheckResult).toBeDefined();
      expect(healthCheckResult?.status).toBe('pass');
      const output = healthCheckResult?.output as Record<string, unknown>;
      expect(output.command).toBe('npm run build && npm test');
      expect(output.duration).toBe(62000);
      expect(output.enabled).toBe(true);
      expect(output.logExcerpt).toBeDefined();
    });

    it('passing health:check enables user-approval to become pending', async () => {
      const healthCheckPasses = makePassCheck('health:check');
      const reviewPassedCheck = makePassCheck('review-passed', { verdict: 'PASS', snapshotSha: 'abc123' });
      const mergeReadyCheck = makePassCheck('merge-ready');
      const userApprovalCheck = new UserApprovalCheck(Stage.Check);

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckPasses];
      runner.postTaskChecks = [
        reviewPassedCheck,
        mergeReadyCheck,
        userApprovalCheck,
      ];

      const ctx = createMockContext(tmpDir, 42, {
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', path: '/tmp/project' }),
        } as any,
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'abc123' }),
        } as any,
      });
      const emitSpy = vi.spyOn(ctx.eventBus!, 'emit');
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const userApprovalResult = result.checkResults.find(r => r.name === 'user-approval');
      expect(userApprovalResult?.status).toBe('pending');
      const approvalCalls = emitSpy.mock.calls.filter(([event]) => event === 'approval_requested');
      expect(approvalCalls).toHaveLength(1);
    });
  });

  describe('Approval is rejected when verification evidence is missing or stale', () => {
    it('approval output lacks verification evidence when health:check is absent from results', async () => {
      const runner = new TestStageRunner();
      runner.postTaskChecks = [
        makePassCheck('review-passed', { verdict: 'PASS', snapshotSha: 'abc123' }),
        makePassCheck('merge-ready'),
        new UserApprovalCheck(Stage.Check),
      ];

      const ctx = createMockContext(tmpDir, 42, {
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', path: '/tmp/project' }),
        } as any,
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'abc123' }),
        } as any,
      });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const userApprovalResult = result.checkResults.find(r => r.name === 'user-approval');
      expect(userApprovalResult?.status).toBe('pending');
      const output = (userApprovalResult?.output as Record<string, unknown>) ?? {};
      expect(output.verificationEvidence).toBeUndefined();
    });

    it('stale health:check result does not satisfy approval requirement', async () => {
      const newCandidateHealthCheck = makeFailCheck('health:check', 'npm run build && npm test failed', {
        kind: 'health-gate',
        stage: 'check',
        command: 'npm run build && npm test',
        exitCode: 1,
        summary: 'Tests failed after rebase',
        candidateHeadSha: 'new-sha-999',
      });

      const runner = new TestStageRunner();
      runner.preTaskChecks = [newCandidateHealthCheck];
      runner.postTaskChecks = [
        makePassCheck('review-passed', { verdict: 'PASS' }),
        makePassCheck('merge-ready'),
        new UserApprovalCheck(Stage.Check),
      ];

      const ctx = createMockContext(tmpDir, 42, {
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', path: '/tmp/project' }),
        } as any,
        worktreeManager: {
          getPath: vi.fn().mockReturnValue('/tmp/worktree'),
          createCheckConvergenceCommit: vi.fn().mockResolvedValue({ success: true, headSha: 'new-sha-999' }),
        } as any,
      });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const lastHealthCheck = result.checkResults.filter(r => r.name === 'health:check').at(-1);
      expect(lastHealthCheck?.status).toBe('fail');
      expect(ctx.issueRepo.setApprovalState).not.toHaveBeenCalled();
    });
  });

  describe('checks.buildTest config controls Check full verification when healthGates.check is absent', () => {
    it('checks.buildTest maps to health:check policy when healthGates.check is absent', () => {
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
            command: 'npm run legacy-test',
            timeout: 300000,
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

    it('checks.buildTest uses default command when healthGates.check is absent and no buildTest command defined', () => {
      const workflow = {
        stages: [{ stage: 'check' }],
        checks: {
          buildTest: {
            timeout: 300000,
            autoFix: false,
            maxFixAttempts: 1,
          },
        },
      } as any;

      const policies = loadHealthGatePolicies(workflow);

      expect(policies.check.command).toBe('npm run build && npm test');
    });

    it('health:check policy uses defaults when neither healthGates.check nor checks.buildTest are defined', () => {
      const workflow = {
        stages: [{ stage: 'check' }],
      } as any;

      const policies = loadHealthGatePolicies(workflow);

      expect(policies.check.command).toBe('npm run build && npm test');
      expect(policies.check.enabled).toBe(true);
      expect(policies.check.timeout).toBe(300000);
    });
  });

  describe('Failed Check verification evidence includes command, status, duration, summary, and log excerpt', () => {
    it('failing health:check output includes command and exit code', async () => {
      const healthCheckFails = makeFailCheck(
        'health:check',
        'npm run build && npm test failed (exit code 2)',
        {
          kind: 'health-gate',
          stage: 'check',
          command: 'npm run build && npm test',
          exitCode: 2,
          duration: 120000,
          enabled: true,
          timedOut: false,
          summary: 'npm run build && npm test failed (exit code 2)',
          logExcerpt: 'FAIL: test suite integration-regression.test.ts\nError: Expected 2 to be 0',
        },
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckFails];
      runner.postTaskChecks = [];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const healthCheckResult = result.checkResults.find(r => r.name === 'health:check');
      expect(healthCheckResult?.status).toBe('fail');
      const output = healthCheckResult?.output as Record<string, unknown>;
      expect(output.command).toBe('npm run build && npm test');
      expect(output.exitCode).toBe(2);
      expect(output.duration).toBe(120000);
      expect(output.enabled).toBe(true);
    });

    it('failing health:check output includes summary and log excerpt', async () => {
      const healthCheckFails = makeFailCheck(
        'health:check',
        'npm run build && npm test failed (exit code 1)',
        {
          kind: 'health-gate',
          stage: 'check',
          command: 'npm run build && npm test',
          exitCode: 1,
          summary: 'Test failed: integration-regression.test.ts',
          logExcerpt: 'FAIL: tests/integrate-regression.test.ts\n  ✓ should pass health check\n  ✗ should fail verification on broken implementation\n\n  Error: expect(received).toBe(expected)\n\n  Received: 1\n  Expected: 0',
        },
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckFails];
      runner.postTaskChecks = [];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      const healthCheckResult = result.checkResults.find(r => r.name === 'health:check');
      const output = healthCheckResult?.output as Record<string, unknown>;
      expect(output.summary).toContain('integration-regression.test.ts');
      expect(output.logExcerpt).toContain('expect(received).toBe(expected)');
      expect(output.logExcerpt).toContain('Received: 1');
      expect(output.logExcerpt).toContain('Expected: 0');
    });

    it('failing health:check with timeout includes timedOut flag', async () => {
      const healthCheckTimesOut = makeFailCheck(
        'health:check',
        'npm run build && npm test — 超时',
        {
          kind: 'health-gate',
          stage: 'check',
          command: 'npm run build && npm test',
          timeout: 300000,
          duration: 300001,
          enabled: true,
          timedOut: true,
          summary: 'npm run build && npm test — 超时',
          logExcerpt: '',
        },
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckTimesOut];
      runner.postTaskChecks = [];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      const healthCheckResult = result.checkResults.find(r => r.name === 'health:check');
      const output = healthCheckResult?.output as Record<string, unknown>;
      expect(output.timedOut).toBe(true);
      expect(output.duration).toBe(300001);
    });

    it('health:check failure prevents executeTasks from running', async () => {
      const healthCheckFails = makeFailCheck('health:check', 'build failed');
      const executeTasksFn = vi.fn().mockImplementation(() => {
        fs.writeFileSync(path.join(tmpDir, 'openspec', 'changes', '42-test', 'review.md'), '# Review');
        return { done: true };
      });

      const runner = new TestStageRunner();
      runner.preTaskChecks = [healthCheckFails];
      runner.postTaskChecks = [];
      runner.executeTasksFn = executeTasksFn;

      const ctx = createMockContext(tmpDir, 42);
      await runner.run(ctx);

      expect(executeTasksFn).not.toHaveBeenCalled();
      const reviewPath = path.join(tmpDir, 'openspec', 'changes', '42-test', 'review.md');
      expect(fs.existsSync(reviewPath)).toBe(false);
    });
  });
});