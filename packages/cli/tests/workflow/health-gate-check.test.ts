import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { execFile } from 'child_process';
import { HealthGateCheck, type HealthGatePolicy } from '../../src/workflow/checks/health-gate-check';

vi.mock('child_process', async (importOriginal) => {
  const actual = await importOriginal<typeof import('child_process')>();
  return {
    ...actual,
    execFile: vi.fn(),
  };
});

function createMockPolicy(overrides?: Partial<HealthGatePolicy>): HealthGatePolicy {
  return {
    enabled: true,
    command: 'npm run build',
    timeout: 300000,
    autoFix: false,
    maxFixAttempts: 0,
    ...overrides,
  };
}

function makeCheckContext() {
  return {
    issue: {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      stage: 'build' as any,
      status: 'active' as any,
      projectId: 'proj-1',
      labels: [],
      priority: 'p2' as any,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    changeDir: '/tmp/change',
    eventBus: { emit: vi.fn() } as any,
    projectId: 'proj-1',
    acpOptions: {} as any,
  };
}

describe('HealthGateCheck', () => {
  const execFileMock = vi.mocked(execFile);

  beforeEach(() => {
    execFileMock.mockReset();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  describe('pass scenario', () => {
    it('returns pass result with health gate metadata', async () => {
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(null, { stdout: 'build success\n', stderr: '' });
          } else if (typeof cb === 'function') {
            cb(null, { stdout: 'build success\n', stderr: '' });
          }
        });
        return { stdout: 'build success\n', stderr: '' };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ command: 'npm run build', enabled: true }),
        stage: 'build',
      });

      const result = await check.run(makeCheckContext());

      expect(result.status).toBe('pass');
      expect(result.name).toBe('health:build');
      expect(result.output).toMatchObject({
        kind: 'health-gate',
        stage: 'build',
        command: 'npm run build',
        enabled: true,
      });
      expect(result.output).toHaveProperty('duration');
      expect(result.output).toHaveProperty('logExcerpt');
    });

    it('returns bounded log excerpt on pass', async () => {
      const longOutput = 'x'.repeat(10000);
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(null, { stdout: longOutput, stderr: '' });
          } else if (typeof cb === 'function') {
            cb(null, { stdout: longOutput, stderr: '' });
          }
        });
        return { stdout: longOutput, stderr: '' };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ command: 'npm run build' }),
        stage: 'build',
      });

      const result = await check.run(makeCheckContext());

      expect(result.output).toHaveProperty('logExcerpt');
      const excerpt = result.output!.logExcerpt as string;
      expect(excerpt.length).toBeLessThan(longOutput.length);
      expect(excerpt).toContain('...[truncated]...');
    });
  });

  describe('fail scenario', () => {
    it('returns fail result with exit code and summary', async () => {
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        const err = new Error('Command failed');
        err.code = 1;
        (err as any).stdout = '';
        (err as any).stderr = 'ERROR: build failed\nsome error details';
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(err, { stdout: '', stderr: 'ERROR: build failed\nsome error details' });
          } else if (typeof cb === 'function') {
            cb(err, { stdout: '', stderr: 'ERROR: build failed\nsome error details' });
          }
        });
        return { stdout: '', stderr: 'ERROR: build failed\nsome error details' };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ command: 'npm run build' }),
        stage: 'build',
      });

      const result = await check.run(makeCheckContext());

      expect(result.status).toBe('fail');
      expect(result.name).toBe('health:build');
      expect(result.output).toMatchObject({
        kind: 'health-gate',
        stage: 'build',
        command: 'npm run build',
        enabled: true,
        exitCode: 1,
        timedOut: false,
      });
      expect(result.output).toHaveProperty('summary');
      expect(result.output).toHaveProperty('logExcerpt');
    });

    it('returns bounded log excerpt on fail', async () => {
      const longStderr = 'y'.repeat(10000);
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        const err = new Error('Command failed');
        err.code = 1;
        (err as any).stdout = '';
        (err as any).stderr = longStderr;
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(err, { stdout: '', stderr: longStderr });
          } else if (typeof cb === 'function') {
            cb(err, { stdout: '', stderr: longStderr });
          }
        });
        return { stdout: '', stderr: longStderr };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ command: 'npm run build' }),
        stage: 'build',
      });

      const result = await check.run(makeCheckContext());

      expect(result.status).toBe('fail');
      const excerpt = result.output!.logExcerpt as string;
      expect(excerpt.length).toBeLessThan(longStderr.length);
      expect(excerpt).toContain('...[truncated]...');
    });
  });

  describe('timeout scenario', () => {
    it('returns fail result with timedOut=true', async () => {
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        const err = new Error('Command timed out');
        err.killed = true;
        err.code = null;
        (err as any).stdout = '';
        (err as any).stderr = '';
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(err, { stdout: '', stderr: '' });
          } else if (typeof cb === 'function') {
            cb(err, { stdout: '', stderr: '' });
          }
        });
        return { stdout: '', stderr: '' };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ command: 'npm run build', timeout: 5000 }),
        stage: 'build',
      });

      const result = await check.run(makeCheckContext());

      expect(result.status).toBe('fail');
      expect(result.output).toMatchObject({
        timedOut: true,
      });
      expect(result.message).toContain('超时');
    });
  });

  describe('disabled gate scenario', () => {
    it('returns pass with enabled=false and does not execute command', async () => {
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(null, { stdout: 'abc123\n', stderr: '' });
          } else if (typeof cb === 'function') {
            cb(null, { stdout: 'abc123\n', stderr: '' });
          }
        });
        return { stdout: 'abc123\n', stderr: '' };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ enabled: false }),
        stage: 'build',
      });

      const result = await check.run(makeCheckContext());

      expect(result.status).toBe('pass');
      expect(result.name).toBe('health:build');
      expect(result.output).toMatchObject({
        kind: 'health-gate',
        stage: 'build',
        enabled: false,
      });
      const calls = execFileMock.mock.calls;
      const healthGateCommandCalls = calls.filter((c: any[]) => c[0] !== 'git');
      expect(healthGateCommandCalls).toHaveLength(0);
    });
  });

  describe('output structure', () => {
    it('pass output contains kind, stage, command, timeout, duration, enabled status, and log excerpt', async () => {
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(null, { stdout: 'done', stderr: '' });
          } else if (typeof cb === 'function') {
            cb(null, { stdout: 'done', stderr: '' });
          }
        });
        return { stdout: 'done', stderr: '' };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ command: 'npm run build', timeout: 300000 }),
        stage: 'check',
      });

      const result = await check.run(makeCheckContext());

      expect(result.output).toHaveProperty('kind', 'health-gate');
      expect(result.output).toHaveProperty('stage', 'check');
      expect(result.output).toHaveProperty('command', 'npm run build');
      expect(result.output).toHaveProperty('timeout', 300000);
      expect(result.output).toHaveProperty('duration');
      expect(result.output).toHaveProperty('enabled', true);
      expect(result.output).toHaveProperty('logExcerpt');
    });

    it('fail output contains exit code or timeout status, summary, duration, command, and bounded log excerpt', async () => {
      execFileMock.mockImplementation((_cmd: any, _args: any, _opts: any, cb: any) => {
        const err = new Error('fail');
        err.code = 2;
        (err as any).stdout = '';
        (err as any).stderr = 'error output';
        process.nextTick(() => {
          if (typeof _opts === 'function') {
            _opts(err, { stdout: '', stderr: 'error output' });
          } else if (typeof cb === 'function') {
            cb(err, { stdout: '', stderr: 'error output' });
          }
        });
        return { stdout: '', stderr: 'error output' };
      });

      const check = new HealthGateCheck({
        worktreePath: '/tmp/worktree',
        policy: createMockPolicy({ command: 'npm test', timeout: 60000 }),
        stage: 'check',
      });

      const result = await check.run(makeCheckContext());

      expect(result.output).toHaveProperty('kind', 'health-gate');
      expect(result.output).toHaveProperty('stage', 'check');
      expect(result.output).toHaveProperty('command', 'npm test');
      expect(result.output).toHaveProperty('timeout', 60000);
      expect(result.output).toHaveProperty('duration');
      expect(result.output).toHaveProperty('exitCode', 2);
      expect(result.output).toHaveProperty('timedOut', false);
      expect(result.output).toHaveProperty('summary');
      expect(result.output).toHaveProperty('logExcerpt');
    });
  });

  describe('check name format', () => {
    it('uses health:stage as check name', () => {
      const stages = ['plan', 'build', 'check', 'postMerge'];
      for (const stage of stages) {
        const check = new HealthGateCheck({
          worktreePath: '/tmp/worktree',
          policy: createMockPolicy(),
          stage,
        });
        expect(check.name).toBe(`health:${stage}`);
      }
    });
  });
});
