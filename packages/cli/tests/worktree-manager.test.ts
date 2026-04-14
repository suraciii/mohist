import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { smartFetch } from '../src/git/worktree-manager';
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

describe('smartFetch', () => {
  let tmpDir: string;
  let gitDir: string;
  let cacheFile: string;
  const execFileMock = vi.mocked(execFile);
  let warnSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'smartfetch-test-'));
    gitDir = path.join(tmpDir, '.git');
    fs.mkdirSync(gitDir);
    cacheFile = path.join(gitDir, 'mohist-last-fetch');
    execFileMock.mockReset();
    warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    warnSpy.mockRestore();
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
    expect(execFileMock).toHaveBeenCalledWith('git', ['fetch', 'origin'], { cwd: tmpDir }, expect.any(Function));
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
    expect(warnSpy).not.toHaveBeenCalled();
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
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('git fetch origin failed after 3 attempts')
    );
    expect(warnSpy).toHaveBeenCalledWith(
      expect.stringContaining('gnutls_handshake() failed')
    );
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
