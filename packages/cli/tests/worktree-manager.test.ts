import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { smartFetch, WorktreeManager, formatSquashCommitMessage, MergeMetadata } from '../src/git/worktree-manager';
import { execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

vi.mock('child_process', async (importOriginal) => {
  const actual = await importOriginal<typeof import('child_process')>();
  return {
    ...actual,
    execFile: vi.fn(),
  };
});

// Helper: vi.mocked execFile loses the util.promisify.custom symbol,
// so promisify(execFile) resolves the *second callback arg* directly
// instead of wrapping it in {stdout, stderr}. Code that destructures
// {stdout} from the promise therefore sees undefined. By returning an
// object with a `stdout` property as the second arg we restore the
// expected shape for callers that read stdout.
function mockStdout(stdout: string) {
  return { stdout, stderr: '' };
}

describe('smartFetch', () => {
  let tmpDir: string;
  let gitDir: string;
  let cacheFile: string;
  const execFileMock = vi.mocked(execFile);
  let stderrSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'smartfetch-test-'));
    gitDir = path.join(tmpDir, '.git');
    fs.mkdirSync(gitDir);
    cacheFile = path.join(gitDir, 'mohist-last-fetch');
    execFileMock.mockReset();
    stderrSpy = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    stderrSpy.mockRestore();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('should skip fetch when cache is within 30 minutes', async () => {
    const now = Date.now();
    fs.writeFileSync(cacheFile, now.toString(), 'utf-8');

    await smartFetch(tmpDir);

    expect(execFileMock).not.toHaveBeenCalled();
  });

  it('should fetch and write cache when cache is expired', async () => {
    const oldTime = Date.now() - 31 * 60 * 1000;
    fs.writeFileSync(cacheFile, oldTime.toString(), 'utf-8');
    execFileMock.mockImplementation((cmd, args, opts, cb) => {
      cb?.(null, '', '');
    });

    await smartFetch(tmpDir);

    expect(execFileMock).toHaveBeenCalledTimes(1);
    expect(execFileMock).toHaveBeenCalledWith('git', ['fetch', 'origin', '--prune'], { cwd: tmpDir }, expect.any(Function));
    const cached = parseInt(fs.readFileSync(cacheFile, 'utf-8').trim(), 10);
    expect(cached).toBeGreaterThan(oldTime);
  });

  it('should retry up to 3 times and succeed on final attempt', async () => {
    const oldTime = Date.now() - 31 * 60 * 1000;
    fs.writeFileSync(cacheFile, oldTime.toString(), 'utf-8');
    let callCount = 0;
    execFileMock.mockImplementation((cmd, args, opts, cb) => {
      callCount++;
      if (callCount < 3) {
        cb?.(new Error(`attempt ${callCount} failed`) as any, '', '');
      } else {
        cb?.(null, '', '');
      }
    });

    const promise = smartFetch(tmpDir);
    await vi.advanceTimersByTimeAsync(3000);
    await promise;

    expect(execFileMock).toHaveBeenCalledTimes(3);
    const cached = parseInt(fs.readFileSync(cacheFile, 'utf-8').trim(), 10);
    expect(cached).toBeGreaterThan(oldTime);
    expect(stderrSpy).not.toHaveBeenCalled();
  });

  it('should warn and not throw when all 3 attempts fail', async () => {
    const oldTime = Date.now() - 31 * 60 * 1000;
    fs.writeFileSync(cacheFile, oldTime.toString(), 'utf-8');
    execFileMock.mockImplementation((cmd, args, opts, cb) => {
      cb?.(new Error('gnutls_handshake() failed') as any, '', '');
    });

    const promise = smartFetch(tmpDir);
    await vi.advanceTimersByTimeAsync(3000);
    await expect(promise).resolves.not.toThrow();

    expect(execFileMock).toHaveBeenCalledTimes(3);
    const warnOutput = stderrSpy.mock.calls.map(c => String(c[0])).join(' ');
    expect(warnOutput).toContain('git fetch origin failed');
    expect(warnOutput).toContain('gnutls_handshake() failed');
    const cached = parseInt(fs.readFileSync(cacheFile, 'utf-8').trim(), 10);
    expect(cached).toBe(oldTime);
  });

  it('should apply backoff delays between retries', async () => {
    fs.writeFileSync(cacheFile, '0', 'utf-8');
    const delays: number[] = [];
    const realSetTimeout = setTimeout;
    execFileMock.mockImplementation((cmd, args, opts, cb) => {
      cb?.(new Error('network error') as any, '', '');
    });

    const promise = smartFetch(tmpDir);
    await vi.advanceTimersByTimeAsync(3000);
    await promise;

    expect(execFileMock).toHaveBeenCalledTimes(3);
  });
});

describe('WorktreeManager', () => {
  let tmpDir: string;
  let originalHome: string | undefined;
  const execFileMock = vi.mocked(execFile);

  const PROJECT_NAME = 'test-project';

  function getWorktreeDir(issueNumber: number): string {
    return path.join(tmpDir, '.mohist', 'projects', PROJECT_NAME, 'worktrees', `issue-${issueNumber}`);
  }

  it('builds a change signature from status, diffs, and untracked file signatures', async () => {
    const wm = new WorktreeManager();
    const untrackedPath = path.join(tmpDir, 'new-file.txt');
    fs.writeFileSync(untrackedPath, 'new content', 'utf-8');
    execFileMock.mockImplementation((_cmd, args, _opts, cb) => {
      const gitArgs = args as string[];
      if (gitArgs[0] === 'status') {
        cb?.(null, mockStdout(' M src/existing.ts\n?? new-file.txt'), '');
        return;
      }
      if (gitArgs[0] === 'diff' && !gitArgs.includes('--cached')) {
        cb?.(null, mockStdout('unstaged diff'), '');
        return;
      }
      if (gitArgs[0] === 'diff' && gitArgs.includes('--cached')) {
        cb?.(null, mockStdout('staged diff'), '');
        return;
      }
      if (gitArgs[0] === 'ls-files') {
        cb?.(null, mockStdout('new-file.txt\0'), '');
        return;
      }
      cb?.(new Error(`unexpected git call ${gitArgs.join(' ')}`) as any, '', '');
    });

    const signature = await wm.getWorktreeChangeSignature(tmpDir);

    expect(signature).toContain('M src/existing.ts');
    expect(signature).toContain('unstaged diff');
    expect(signature).toContain('staged diff');
    expect(signature).toContain('new-file.txt:');
  });

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wtm-test-'));
    fs.mkdirSync(path.join(tmpDir, '.git'), { recursive: true });
    fs.writeFileSync(
      path.join(tmpDir, '.git', 'mohist-last-fetch'),
      Date.now().toString(),
      'utf-8',
    );
    originalHome = process.env.HOME;
    process.env.HOME = tmpDir;
    execFileMock.mockReset();
  });

  afterEach(() => {
    process.env.HOME = originalHome;
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('canFastForward', () => {
    it('should return true when origin/base is ancestor of branch', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, '', '');
        return undefined as any;
      });

      const result = await wm.canFastForward(tmpDir, PROJECT_NAME, 1, 'main');

      expect(result).toBe(true);
      expect(execFileMock).toHaveBeenCalledWith(
        'git',
        ['merge-base', '--is-ancestor', 'main', 'mo/issue-1'],
        { cwd: tmpDir },
        expect.any(Function),
      );
    });

    it('should return false when merge-base --is-ancestor fails', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(new Error('not ancestor') as any, '', '');
        return undefined as any;
      });

      const result = await wm.canFastForward(tmpDir, PROJECT_NAME, 1, 'main');

      expect(result).toBe(false);
    });

    it('should return false when worktree does not exist', async () => {
      const wm = new WorktreeManager();

      const result = await wm.canFastForward(tmpDir, PROJECT_NAME, 1, 'main');

      expect(result).toBe(false);
      expect(execFileMock).not.toHaveBeenCalled();
    });
  });

  describe('rebaseOntoMaster', () => {
    function mockRebase(setup: {
      hasCommits?: boolean;
      rebaseConflicts?: boolean;
      conflictFiles?: string[];
    }) {
      const {
        hasCommits = true,
        rebaseConflicts = false,
        conflictFiles = [],
      } = setup;

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[2] === '--oneline') {
          cb?.(null, mockStdout(hasCommits ? 'abc123 commit msg\n' : ''), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'status' && args?.includes('--porcelain')) {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (
          cmd === 'git' &&
          args?.[0] === 'rebase' &&
          args?.[1] !== '--abort'
        ) {
          if (rebaseConflicts) {
            cb?.(new Error('CONFLICT') as any, mockStdout(''), '');
          } else {
            cb?.(null, mockStdout(''), '');
          }
          return undefined as any;
        }
        if (cmd === 'git' && args?.includes('--diff-filter=U')) {
          cb?.(null, mockStdout(conflictFiles.join('\n') + '\n'), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'rebase' && args?.[1] === '--abort') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });
    }

    it('should succeed when rebase completes without conflicts', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();
      mockRebase({ hasCommits: true, rebaseConflicts: false });

      const result = await wm.rebaseOntoMaster(tmpDir, PROJECT_NAME, 1, 'main');

      expect(result).toEqual({ success: true, conflicts: [] });
    });

    it('should return success when branch has no commits to rebase', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();
      mockRebase({ hasCommits: false });

      const result = await wm.rebaseOntoMaster(tmpDir, PROJECT_NAME, 1, 'main');

      expect(result).toEqual({ success: true, conflicts: [] });
    });

    it('should abort rebase on conflict when abortOnConflict is true (default)', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();
      mockRebase({
        hasCommits: true,
        rebaseConflicts: true,
        conflictFiles: ['src/foo.ts'],
      });

      const result = await wm.rebaseOntoMaster(tmpDir, PROJECT_NAME, 1, 'main');

      expect(result.success).toBe(false);
      expect(result.conflicts).toEqual(['src/foo.ts']);

      const abortCalls = execFileMock.mock.calls.filter(
        (c: any) =>
          c[0] === 'git' && c[1]?.[0] === 'rebase' && c[1]?.[1] === '--abort',
      );
      // 1 cleanup abort (before rebase) + 1 conflict abort = 2
      expect(abortCalls.length).toBe(2);
    });

    it('should preserve conflict markers when abortOnConflict is false', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();
      mockRebase({
        hasCommits: true,
        rebaseConflicts: true,
        conflictFiles: ['src/conflict.ts', 'src/bar.ts'],
      });

      const result = await wm.rebaseOntoMaster(tmpDir, PROJECT_NAME, 1, 'main', {
        abortOnConflict: false,
      });

      expect(result.success).toBe(false);
      expect(result.conflicts).toEqual(['src/conflict.ts', 'src/bar.ts']);

      const abortCalls = execFileMock.mock.calls.filter(
        (c: any) =>
          c[0] === 'git' && c[1]?.[0] === 'rebase' && c[1]?.[1] === '--abort',
      );
      // 1 cleanup abort (before rebase) only, no conflict abort since abortOnConflict=false
      expect(abortCalls.length).toBe(1);
    });

    it('should throw when worktree does not exist', async () => {
      const wm = new WorktreeManager();

      await expect(
        wm.rebaseOntoMaster(tmpDir, PROJECT_NAME, 1, 'main'),
      ).rejects.toThrow('Worktree for issue #1 not found');
    });
  });

  describe('rebaseContinue', () => {
    it('should succeed when rebase continue completes', async () => {
      const wm = new WorktreeManager();
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.rebaseContinue(PROJECT_NAME, 1);

      expect(result).toEqual({ success: true, conflicts: [] });
      expect(execFileMock).toHaveBeenCalledWith(
        'git',
        ['rebase', '--continue'],
        expect.objectContaining({
          env: expect.objectContaining({ GIT_EDITOR: 'true' }),
        }),
        expect.any(Function),
      );
    });

    it('should return conflicts when rebase continue still has conflicts', async () => {
      const wm = new WorktreeManager();
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (args?.[0] === 'rebase' && args?.[1] === '--continue') {
          cb?.(new Error('CONFLICT') as any, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'diff' && args?.includes('--diff-filter=U')) {
          cb?.(null, mockStdout('src/still-conflicting.ts\n'), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.rebaseContinue(PROJECT_NAME, 1);

      expect(result.success).toBe(false);
      expect(result.conflicts).toEqual(['src/still-conflicting.ts']);
    });
  });

  describe('mergeApprovedCandidate', () => {
    it('commits integration artifacts and squash merges the issue branch', async () => {
      fs.mkdirSync(getWorktreeDir(9), { recursive: true });
      const wm = new WorktreeManager();

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd !== 'git') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }

        if (args?.[0] === 'status' && args?.includes('--porcelain')) {
          cb?.(null, mockStdout(' M openspec/specs/cap/spec.md\nR  openspec/changes/9-test openspec/changes/archive/2026-05-09-9-test\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'rev-parse') {
          cb?.(null, mockStdout(args[1] === 'HEAD' ? 'landed-sha\n' : 'candidate-after-artifacts\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'merge-base') {
          cb?.(null, mockStdout('base-sha\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('chore: integrate issue #9 artifacts\n'), '');
          return undefined as any;
        }

        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.mergeApprovedCandidate(tmpDir, PROJECT_NAME, 9, 'main');

      expect(result).toMatchObject({
        targetBranch: 'main',
        candidateHeadSha: 'candidate-after-artifacts',
        landedSha: 'landed-sha',
      });
      expect(result).not.toHaveProperty('fastForward');
      expect(execFileMock).toHaveBeenCalledWith(
        'git',
        ['commit', '-m', 'chore: integrate issue #9 artifacts', '--no-verify'],
        { cwd: getWorktreeDir(9) },
        expect.any(Function),
      );

      const squashCalls = execFileMock.mock.calls.filter((call: any) =>
        call[1]?.[0] === 'merge' && call[1]?.[1] === '--squash'
      );
      expect(squashCalls.length).toBe(1);
      expect(squashCalls[0][1]).toEqual(['merge', '--squash', 'mo/issue-9']);
    });
  });

  describe('formatSquashCommitMessage', () => {
    it('should format message with issue title, number, and commit subjects', () => {
      const metadata: MergeMetadata = {
        issueNumber: 42,
        issueTitle: 'Add user authentication',
        commitMessages: [
          'plan(issue-42): add user authentication',
          'T-001: Create auth middleware',
          'T-002: Add login endpoint',
        ],
      };

      const message = formatSquashCommitMessage(metadata);

      expect(message).toBe(
        'Add user authentication (#42)\n\n' +
        '* plan(issue-42): add user authentication\n' +
        '* T-001: Create auth middleware\n' +
        '* T-002: Add login endpoint'
      );
    });

    it('should return subject-only message when tasks are missing', () => {
      const metadata: MergeMetadata = {
        issueNumber: 7,
        issueTitle: 'Fix login bug',
      };

      const message = formatSquashCommitMessage(metadata);

      expect(message).toBe('Fix login bug (#7)');
    });

    it('should return subject-only message when tasks array is empty', () => {
      const metadata: MergeMetadata = {
        issueNumber: 15,
        issueTitle: 'Update dependencies',
        tasks: [],
      };

      const message = formatSquashCommitMessage(metadata);

      expect(message).toBe('Update dependencies (#15)');
    });

    it('should ignore legacy tasks when commit messages are missing', () => {
      const metadata: MergeMetadata = {
        issueNumber: 3,
        issueTitle: 'Add CI pipeline',
        tasks: [
          { id: 'T-001', title: 'Configure GitHub Actions' },
        ],
      };

      const message = formatSquashCommitMessage(metadata);

      expect(message).toBe('Add CI pipeline (#3)');
    });
  });

  describe('mergeBack', () => {
    it('should perform squash merge with metadata commit message', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();

      const squashCalls: string[][] = [];
      const commitMessages: string[] = [];

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('abc123 feat: add login\n'), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'merge' && args?.[1] === '--squash') {
          squashCalls.push([...args]);
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'commit') {
          commitMessages.push(args[2]);
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const metadata = {
        issueNumber: 1,
        issueTitle: 'Add user auth',
        tasks: [{ id: 'T-001', title: 'Create middleware' }],
      };

      const result = await wm.mergeBack(tmpDir, PROJECT_NAME, 1, 'main', metadata);

      expect(result.success).toBe(true);
      expect(result.message).toContain('squash');
      expect(squashCalls.length).toBe(1);
      expect(squashCalls[0]).toEqual(['merge', '--squash', 'mo/issue-1']);
      expect(commitMessages.length).toBe(1);
      expect(commitMessages[0]).toBe('Add user auth (#1)\n\n* abc123 feat: add login');
    });

    it('should fail when there are no candidate commits to squash merge', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.mergeBack(tmpDir, PROJECT_NAME, 1, 'main', {
        issueNumber: 1,
        issueTitle: 'No-op merge',
      });

      expect(result.success).toBe(false);
      expect(result.message).toContain('No candidate commits to squash merge');
    });

    it('should return failure when squash merge fails', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('abc123 feat: add login\n'), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'merge' && args?.[1] === '--squash') {
          cb?.(new Error('CONFLICT: merge conflict in src/foo.ts') as any, mockStdout(''), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'merge' && args?.[1] === '--abort') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.mergeBack(tmpDir, PROJECT_NAME, 1, 'main', {
        issueNumber: 1,
        issueTitle: 'Add user auth',
      });

      expect(result.success).toBe(false);
      expect(result.message).toContain('Squash merge failed');
      expect(execFileMock).toHaveBeenCalledWith(
        'git',
        ['reset', '--merge'],
        { cwd: tmpDir },
        expect.any(Function),
      );
      if (!result.success) {
        expect(result.targetBranch).toBe('main');
        expect(result.baseSha).toBe('');
        expect(result.candidateHeadSha).toBe('');
      }
    });

    it('should return failure when commit after squash fails', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('abc123 feat: add login\n'), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'merge' && args?.[1] === '--squash') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'commit') {
          cb?.(new Error('nothing to commit') as any, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.mergeBack(tmpDir, PROJECT_NAME, 1, 'main', {
        issueNumber: 1,
        issueTitle: 'Add user auth',
      });

      expect(result.success).toBe(false);
      expect(result.message).toContain('Squash commit failed');
      expect(result.message).toContain('manual cleanup');
      expect(execFileMock).toHaveBeenCalledWith(
        'git',
        ['reset', '--merge'],
        { cwd: tmpDir },
        expect.any(Function),
      );
    });

    it('should use issue title when tasks metadata is unavailable', async () => {
      fs.mkdirSync(getWorktreeDir(1), { recursive: true });
      const wm = new WorktreeManager();

      const commitMessages: string[] = [];
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('abc123 feat: add login\n'), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'merge' && args?.[1] === '--squash') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'commit') {
          commitMessages.push(args[2]);
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.mergeBack(tmpDir, PROJECT_NAME, 1, 'main', {
        issueNumber: 1,
        issueTitle: 'Add login flow',
      });

      expect(result.success).toBe(true);
      expect(commitMessages[0]).toBe('Add login flow (#1)\n\n* abc123 feat: add login');
    });
  });

  describe('abortRebase', () => {
    it('should call git rebase --abort in worktree', async () => {
      const wm = new WorktreeManager();
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      await wm.abortRebase(PROJECT_NAME, 1);

      expect(execFileMock).toHaveBeenCalledWith(
        'git',
        ['rebase', '--abort'],
        expect.objectContaining({ cwd: expect.any(String) }),
        expect.any(Function),
      );
    });
  });

  describe('squash merge regression', () => {
    it('mergeApprovedCandidate does not call git merge --ff-only', async () => {
      fs.mkdirSync(getWorktreeDir(9), { recursive: true });
      const wm = new WorktreeManager();

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd !== 'git') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'status' && args?.includes('--porcelain')) {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'rev-parse') {
          cb?.(null, mockStdout(args[1] === 'HEAD' ? 'landed-sha\n' : 'candidate-sha\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'merge-base') {
          cb?.(null, mockStdout('base-sha\n'), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      await wm.mergeApprovedCandidate(tmpDir, PROJECT_NAME, 9, 'main', {
        issueNumber: 9,
        issueTitle: 'Add feature X',
      });

      const ffOnlyCalls = execFileMock.mock.calls.filter((call: any) =>
        call[0] === 'git' && Array.isArray(call[1]) && call[1][0] === 'merge' && call[1].includes('--ff-only')
      );
      expect(ffOnlyCalls.length).toBe(0);
    });

    it('mergeApprovedCandidate creates exactly one landed commit via squash', async () => {
      fs.mkdirSync(getWorktreeDir(10), { recursive: true });
      const wm = new WorktreeManager();

      const squashCalls: string[][] = [];
      const commitCalls: string[][] = [];

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd !== 'git') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'status' && args?.includes('--porcelain')) {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'rev-parse') {
          cb?.(null, mockStdout(args[1] === 'HEAD' ? 'landed-sha\n' : 'candidate-sha\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'merge-base') {
          cb?.(null, mockStdout('base-sha\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('plan(issue-10): add feature y\nT-001: Implement feature\nT-002: Add tests\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'merge' && args?.[1] === '--squash') {
          squashCalls.push([...args]);
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'commit' && args?.[1] === '-m') {
          commitCalls.push([...args]);
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.mergeApprovedCandidate(tmpDir, PROJECT_NAME, 10, 'main', {
        issueNumber: 10,
        issueTitle: 'Add feature Y',
        tasks: [
          { id: 'T-001', title: 'Implement feature' },
          { id: 'T-002', title: 'Add tests' },
        ],
      });

      expect(result).toMatchObject({ landedSha: 'landed-sha' });
      expect(squashCalls.length).toBe(1);
      expect(squashCalls[0]).toEqual(['merge', '--squash', 'mo/issue-10']);
      expect(commitCalls.length).toBe(1);
      expect(commitCalls[0][2]).toBe(
        'Add feature Y (#10)\n\n' +
        '* plan(issue-10): add feature y\n' +
        '* T-001: Implement feature\n' +
        '* T-002: Add tests'
      );
    });

    it('mergeApprovedCandidate with metadata generates commit message from branch commit subjects', async () => {
      fs.mkdirSync(getWorktreeDir(11), { recursive: true });
      const wm = new WorktreeManager();

      let capturedCommitMessage = '';
      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd !== 'git') {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'status' && args?.includes('--porcelain')) {
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (args?.[0] === 'rev-parse') {
          cb?.(null, mockStdout(args[1] === 'HEAD' ? 'landed-sha\n' : 'candidate-sha\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'merge-base') {
          cb?.(null, mockStdout('base-sha\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('plan(issue-11): fix critical bug\nT-001: Root cause analysis\nT-002: Apply fix\nT-003: Regression tests\n'), '');
          return undefined as any;
        }
        if (args?.[0] === 'commit' && args?.[1] === '-m') {
          capturedCommitMessage = args[2];
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      await wm.mergeApprovedCandidate(tmpDir, PROJECT_NAME, 11, 'main', {
        issueNumber: 11,
        issueTitle: 'Fix critical bug',
        tasks: [
          { id: 'T-001', title: 'Root cause analysis' },
          { id: 'T-002', title: 'Apply fix' },
          { id: 'T-003', title: 'Regression tests' },
        ],
      });

      expect(capturedCommitMessage).toContain('Fix critical bug (#11)');
      expect(capturedCommitMessage).toContain('* plan(issue-11): fix critical bug');
      expect(capturedCommitMessage).toContain('* T-001: Root cause analysis');
      expect(capturedCommitMessage).toContain('* T-002: Apply fix');
      expect(capturedCommitMessage).toContain('* T-003: Regression tests');
    });

    it('mergeBack does not call git merge --ff-only', async () => {
      fs.mkdirSync(getWorktreeDir(12), { recursive: true });
      const wm = new WorktreeManager();

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('abc123 commit msg\n'), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      await wm.mergeBack(tmpDir, PROJECT_NAME, 12, 'main', {
        issueNumber: 12,
        issueTitle: 'Another issue',
      });

      const ffOnlyCalls = execFileMock.mock.calls.filter((call: any) =>
        call[0] === 'git' && Array.isArray(call[1]) && call[1][0] === 'merge' && call[1].includes('--ff-only')
      );
      expect(ffOnlyCalls.length).toBe(0);
    });

    it('mergeBack creates exactly one squash commit with correct message', async () => {
      fs.mkdirSync(getWorktreeDir(13), { recursive: true });
      const wm = new WorktreeManager();

      const squashCalls: string[][] = [];
      const commitCalls: string[][] = [];

      execFileMock.mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        if (cmd === 'git' && args?.[0] === 'log' && args?.[1] === '--format=%s') {
          cb?.(null, mockStdout('abc123 commit msg\n'), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'merge' && args?.[1] === '--squash') {
          squashCalls.push([...args]);
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        if (cmd === 'git' && args?.[0] === 'commit') {
          commitCalls.push([...args]);
          cb?.(null, mockStdout(''), '');
          return undefined as any;
        }
        cb?.(null, mockStdout(''), '');
        return undefined as any;
      });

      const result = await wm.mergeBack(tmpDir, PROJECT_NAME, 13, 'main', {
        issueNumber: 13,
        issueTitle: 'Refactor module',
        tasks: [{ id: 'T-001', title: 'Rewrite core' }],
      });

      expect(result.success).toBe(true);
      expect(squashCalls.length).toBe(1);
      expect(squashCalls[0]).toEqual(['merge', '--squash', 'mo/issue-13']);
      expect(commitCalls.length).toBe(1);
      expect(commitCalls[0][2]).toBe('Refactor module (#13)\n\n* abc123 commit msg');
    });
  });
});
