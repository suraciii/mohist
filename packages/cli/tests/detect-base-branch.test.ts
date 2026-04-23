import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

const exec = promisify(execFile);

describe('detectBaseBranch', () => {
  let tmpDir: string;
  let originDir: string;
  let cloneDir: string;

  beforeEach(async () => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'detect-branch-test-'));
    originDir = path.join(tmpDir, 'origin.git');
    cloneDir = path.join(tmpDir, 'clone');
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  async function initOriginAndClone(defaultBranch: string) {
    await exec('git', ['init', '-b', defaultBranch, originDir], { cwd: tmpDir });
    await exec('git', ['config', 'user.email', 'test@test.com'], { cwd: originDir });
    await exec('git', ['config', 'user.name', 'Test'], { cwd: originDir });
    fs.writeFileSync(path.join(originDir, 'README.md'), 'hello');
    await exec('git', ['add', '.'], { cwd: originDir });
    await exec('git', ['commit', '-m', 'init'], { cwd: originDir });
    await exec('git', ['clone', originDir, cloneDir], { cwd: tmpDir });
  }

  it('should return main when path does not exist', async () => {
    const { detectBaseBranch } = await import('../src/git/detect-base-branch');
    expect(await detectBaseBranch('/nonexistent/path/xyz')).toBe('main');
  });

  it('should return main when not a git repo', async () => {
    const { detectBaseBranch } = await import('../src/git/detect-base-branch');
    expect(await detectBaseBranch(tmpDir)).toBe('main');
  });

  it('should return master when origin/HEAD points to master', async () => {
    await initOriginAndClone('master');
    const { detectBaseBranch } = await import('../src/git/detect-base-branch');
    expect(await detectBaseBranch(cloneDir)).toBe('master');
  });

  it('should return main when origin/HEAD points to main', async () => {
    await initOriginAndClone('main');
    const { detectBaseBranch } = await import('../src/git/detect-base-branch');
    expect(await detectBaseBranch(cloneDir)).toBe('main');
  });

  it('should detect via origin/master when origin/HEAD unset', async () => {
    await initOriginAndClone('master');
    await exec('git', ['remote', 'set-head', 'origin', '--delete'], { cwd: cloneDir });
    const { detectBaseBranch } = await import('../src/git/detect-base-branch');
    expect(await detectBaseBranch(cloneDir)).toBe('master');
  });

  it('should return HEAD branch when no remote and HEAD is on develop', async () => {
    fs.mkdirSync(cloneDir, { recursive: true });
    await exec('git', ['init', '-b', 'develop'], { cwd: cloneDir });
    await exec('git', ['config', 'user.email', 'test@test.com'], { cwd: cloneDir });
    await exec('git', ['config', 'user.name', 'Test'], { cwd: cloneDir });
    fs.writeFileSync(path.join(cloneDir, 'README.md'), 'hello');
    await exec('git', ['add', '.'], { cwd: cloneDir });
    await exec('git', ['commit', '-m', 'init'], { cwd: cloneDir });
    const { detectBaseBranch } = await import('../src/git/detect-base-branch');
    expect(await detectBaseBranch(cloneDir)).toBe('develop');
  });

  it('should return main when HEAD is detached with no remote', async () => {
    await initOriginAndClone('master');
    const { stdout: headSha } = await exec('git', ['rev-parse', 'HEAD'], { cwd: cloneDir });
    await exec('git', ['checkout', headSha.trim()], { cwd: cloneDir });
    await exec('git', ['remote', 'remove', 'origin'], { cwd: cloneDir });
    const { detectBaseBranch } = await import('../src/git/detect-base-branch');
    expect(await detectBaseBranch(cloneDir)).toBe('main');
  });
});
