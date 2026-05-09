import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Stage, IssueStatus } from '../src/types';
import type { StageContext, ReactionConfig, CheckResult } from '../src/workflow/stage-context';
import type { Check } from '../src/workflow/checks';
import { CheckStageRunner } from '../src/workflow/check-stage-runner';
import { EventBus } from '../src/services/event-bus';
import { BaseStageRunner } from '../src/workflow/base-stage-runner';

function makeCheck(name: string, reaction: ReactionConfig, runFn: () => Promise<CheckResult>, fixFn?: () => Promise<void>): Check {
  return { name, reaction, run: runFn, fix: fixFn } as Check;
}

function makePassCheck(name: string): Check {
  return makeCheck(
    name,
    { type: 'escalate', escalateTarget: Stage.Build },
    async () => ({ name, status: 'pass', message: `${name} passed` }),
  );
}

function makeFailCheck(name: string, message = `${name} failed`): Check {
  return makeCheck(
    name,
    { type: 'escalate', escalateTarget: Stage.Build },
    async () => ({ name, status: 'fail', message }),
  );
}

function makeAutoFixFailCheck(name: string, runFn: () => Promise<CheckResult>, fixFn?: () => Promise<void>, maxAttempts = 2): Check {
  return makeCheck(
    name,
    { type: 'auto-fix', maxAttempts, fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build } },
    runFn,
    fixFn,
  );
}

function createMockContext(tmpDir: string, issueNumber = 42, overrides?: Partial<StageContext>): StageContext {
  const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
  fs.mkdirSync(changeDir, { recursive: true });

  const emitSpy = vi.fn();
  const eventBus = new EventBus();
  vi.spyOn(eventBus, 'emit').mockImplementation(emitSpy);

  return {
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
      writeArtifact: vi.fn().mockImplementation((_dir: string, artifact: string, _content: string) => {
        const artifactPath = path.join(changeDir, artifact);
        return fs.existsSync(artifactPath);
      }),
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
}

function artifactExists(tmpDir: string, issueNumber: number, artifact: string): boolean {
  const artifactPath = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`, artifact);
  return fs.existsSync(artifactPath);
}

class TestStageRunner extends BaseStageRunner {
  preTaskChecks: Check[] = [];
  postTaskChecks: Check[] = [];
  nextStage = Stage.Done;
  executeTasksFn = vi.fn().mockResolvedValue({ done: true });
  executeTasksCalls = 0;
  handledStage = Stage.Check;

  canHandle(s: Stage): boolean { return s === this.handledStage; }

  protected async executeTasks(): Promise<unknown> {
    this.executeTasksCalls++;
    return this.executeTasksFn();
  }

  protected getChecks(): Check[] { return this.postTaskChecks; }
  protected getPreTaskChecks(): Check[] { return this.preTaskChecks; }
  protected getNextStage(): Stage { return this.nextStage; }
}

describe('CheckStageRunner ordering', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'check-stage-ordering-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('health:check is the default check-stage boundary', () => {
    it('pre-task checks run before executeTasks', async () => {
      const runner = new TestStageRunner();
      runner.preTaskChecks = [makePassCheck('build-test')];
      runner.postTaskChecks = [makePassCheck('ai-review')];

      const ctx = createMockContext(tmpDir);
      await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(1);
    });

    it('executeTasks is skipped when pre-task checks fail', async () => {
      const runner = new TestStageRunner();
      runner.preTaskChecks = [makeFailCheck('build-test')];
      runner.postTaskChecks = [makePassCheck('ai-review')];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(runner.executeTasksCalls).toBe(0);
    });

    it('default CheckStageRunner runs health:check and readiness checks as pre-task checks', () => {
      const runner = new CheckStageRunner({ worktreePath: '/tmp/worktree' });
      const preChecks = runner.getPreTaskChecks();

      expect(preChecks.map(check => check.name)).toEqual([
        'health:check',
        'openspec-sync-dry-run',
        'merge-readiness',
        'integration-health-gate-preview',
      ]);
    });

    it('default CheckStageRunner runs AI review and user approval after tasks', () => {
      const worktreePath = path.join(tmpDir, 'worktree');
      fs.mkdirSync(worktreePath, { recursive: true });
      const runner = new CheckStageRunner({ worktreePath });
      const checks = runner.getChecks();

      expect(checks.map(check => check.name)).toEqual(['ai-review', 'user-approval']);
    });

    it('default CheckStageRunner.run blocks review artifact generation when health:check fails', async () => {
      const worktreePath = path.join(tmpDir, 'worktree');
      fs.mkdirSync(worktreePath, { recursive: true });
      fs.writeFileSync(path.join(worktreePath, 'workflow.yaml'), [
        'stages:',
        '  - stage: check',
        '    prompt: check',
        'healthGates:',
        '  check:',
        '    command: "node -e \\"process.exit(1)\\""',
        '    autoFix: false',
        '    fallbackReaction:',
        '      type: escalate',
        '      escalateTarget: build',
      ].join('\n'));

      const runner = new CheckStageRunner({ worktreePath });
      const ctx = createMockContext(tmpDir, 42, {
        acpOptions: { cwd: worktreePath, issueNumber: 42 } as any,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.checkResults?.map(check => check.name)).toEqual(['health:check']);
      expect(artifactExists(tmpDir, 42, 'review.md')).toBe(false);
      expect(artifactExists(tmpDir, 42, 'review-self-check.md')).toBe(false);
    });
  });

  describe('Build/test failure after max autofix attempts', () => {
    it('stops with failure when autofix exhausts max attempts', async () => {
      let runCount = 0;
      const alwaysFail = makeAutoFixFailCheck(
        'build-test',
        async () => {
          runCount++;
          return { name: 'build-test', status: 'fail', message: 'Build failed', output: { buildLog: 'error log' } };
        },
        async () => {},
        2,
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [alwaysFail];
      runner.postTaskChecks = [makePassCheck('ai-review')];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(runCount).toBe(3);
    });

    it('failure includes concise message from check result', async () => {
      const alwaysFail = makeAutoFixFailCheck(
        'build-test',
        async () => ({ name: 'build-test', status: 'fail', message: 'Build & test 失败 (exit code 1)', output: { buildLog: 'error' } }),
        async () => {},
        2,
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [alwaysFail];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toBeDefined();
    });

    it('failure output includes buildLog from check result', async () => {
      const longLog = 'line1\nline2\nerror TS2307\nline4\nerror TS2339\n';
      const alwaysFail = makeAutoFixFailCheck(
        'build-test',
        async () => ({ name: 'build-test', status: 'fail', message: 'Build failed', output: { buildLog: longLog } }),
        async () => {},
        2,
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [alwaysFail];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const buildTestResult = result.checkResults?.find(r => r.name === 'build-test');
      expect(buildTestResult).toBeDefined();
      const output = buildTestResult?.output as { buildLog?: string };
      expect(output.buildLog).toBeDefined();
    });
  });

  describe('review.md and review-self-check.md are not generated on build/test failure', () => {
    it('executeTasks not called means no review artifacts created', async () => {
      const alwaysFail = makeAutoFixFailCheck(
        'build-test',
        async () => ({ name: 'build-test', status: 'fail', message: 'Build failed' }),
        async () => {},
        2,
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [alwaysFail];
      runner.postTaskChecks = [];
      runner.executeTasksFn = vi.fn().mockImplementation(() => {
        fs.writeFileSync(path.join(tmpDir, 'openspec', 'changes', '42-test', 'review.md'), '# Review');
        return { done: true };
      });

      const ctx = createMockContext(tmpDir);
      await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(0);
      expect(artifactExists(tmpDir, 42, 'review.md')).toBe(false);
    });

    it('executeTasks is called when pre-task checks pass', async () => {
      const runner = new TestStageRunner();
      runner.preTaskChecks = [makePassCheck('build-test')];
      runner.postTaskChecks = [];
      runner.executeTasksFn = vi.fn().mockImplementation(() => {
        const reviewPath = path.join(tmpDir, 'openspec', 'changes', '42-test', 'review.md');
        fs.writeFileSync(reviewPath, '# Review');
        return { done: true };
      });

      const ctx = createMockContext(tmpDir);
      await runner.run(ctx);

      expect(runner.executeTasksCalls).toBe(1);
      expect(artifactExists(tmpDir, 42, 'review.md')).toBe(true);
    });
  });

  describe('approval_requested is not emitted unless build/test and AI review have passed', () => {
    it('approval_requested not emitted when pre-task checks fail', async () => {
      const alwaysFail = makeAutoFixFailCheck(
        'build-test',
        async () => ({ name: 'build-test', status: 'fail', message: 'Build failed' }),
        async () => {},
        2,
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [alwaysFail];
      runner.postTaskChecks = [
        makePassCheck('ai-review'),
        makeCheck('user-approval', { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build } }, async () => ({
          name: 'user-approval', status: 'pending', message: 'Waiting for approval',
        })),
      ];

      const ctx = createMockContext(tmpDir);
      const emitSpy = vi.spyOn(ctx.eventBus!, 'emit');
      await runner.run(ctx);

      const approvalCalls = emitSpy.mock.calls.filter(([event]) => event === 'approval_requested');
      expect(approvalCalls).toHaveLength(0);
    });

    it('approval_requested emitted after all checks pass including ask-user', async () => {
      const runner = new TestStageRunner();
      runner.preTaskChecks = [makePassCheck('build-test')];
      runner.postTaskChecks = [
        makePassCheck('ai-review'),
        makeCheck('user-approval', { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build } }, async () => ({
          name: 'user-approval', status: 'pending', message: 'Waiting for approval',
        })),
      ];

      const ctx = createMockContext(tmpDir);
      const emitSpy = vi.spyOn(ctx.eventBus!, 'emit');
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      const approvalCalls = emitSpy.mock.calls.filter(([event]) => event === 'approval_requested');
      expect(approvalCalls).toHaveLength(1);
    });

    it('approval_requested not emitted when AI review fails', async () => {
      const runner = new TestStageRunner();
      runner.preTaskChecks = [makePassCheck('build-test')];
      runner.postTaskChecks = [
        makeFailCheck('ai-review', 'AI review failed'),
        makeCheck('user-approval', { type: 'ask-user', fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build } }, async () => ({
          name: 'user-approval', status: 'pending', message: 'Waiting for approval',
        })),
      ];

      const ctx = createMockContext(tmpDir);
      const emitSpy = vi.spyOn(ctx.eventBus!, 'emit');
      await runner.run(ctx);

      const approvalCalls = emitSpy.mock.calls.filter(([event]) => event === 'approval_requested');
      expect(approvalCalls).toHaveLength(0);
    });

    it('ai-review check not run when build-test fails', async () => {
      const alwaysFail = makeAutoFixFailCheck(
        'build-test',
        async () => ({ name: 'build-test', status: 'fail', message: 'Build failed' }),
        async () => {},
        2,
      );

      const aiReviewCheck = makePassCheck('ai-review');
      const aiReviewRun = vi.spyOn(aiReviewCheck, 'run');

      const runner = new TestStageRunner();
      runner.preTaskChecks = [alwaysFail];
      runner.postTaskChecks = [aiReviewCheck];

      const ctx = createMockContext(tmpDir);
      await runner.run(ctx);

      expect(aiReviewRun).not.toHaveBeenCalled();
    });
  });

  describe('Build/test autofix success continues to AI review', () => {
    it('when autofix succeeds, stage continues to post-task checks', async () => {
      let runCount = 0;
      const flakyBuild = makeAutoFixFailCheck(
        'build-test',
        async () => {
          runCount++;
          if (runCount <= 1) {
            return { name: 'build-test', status: 'fail', message: 'Build failed' };
          }
          return { name: 'build-test', status: 'pass' };
        },
        async () => {},
        2,
      );

      const aiReviewCheck = makePassCheck('ai-review');
      const aiReviewRun = vi.spyOn(aiReviewCheck, 'run');

      const runner = new TestStageRunner();
      runner.preTaskChecks = [flakyBuild];
      runner.postTaskChecks = [aiReviewCheck];

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(runCount).toBe(2);
      expect(aiReviewRun).toHaveBeenCalled();
    });

    it('after autofix success, executeTasks is called', async () => {
      let runCount = 0;
      const flakyBuild = makeAutoFixFailCheck(
        'build-test',
        async () => {
          runCount++;
          if (runCount <= 1) return { name: 'build-test', status: 'fail', message: 'Build failed' };
          return { name: 'build-test', status: 'pass' };
        },
        async () => {},
        2,
      );

      const runner = new TestStageRunner();
      runner.preTaskChecks = [flakyBuild];
      runner.postTaskChecks = [];
      runner.executeTasksFn = vi.fn().mockImplementation(() => {
        const reviewPath = path.join(tmpDir, 'openspec', 'changes', '42-test', 'review.md');
        fs.writeFileSync(reviewPath, '# Review');
        return { done: true };
      });

      const ctx = createMockContext(tmpDir);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(runCount).toBe(2);
      expect(runner.executeTasksCalls).toBe(1);
      expect(artifactExists(tmpDir, 42, 'review.md')).toBe(true);
    });
  });
});
