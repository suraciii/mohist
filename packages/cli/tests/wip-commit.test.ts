import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { WorktreeManager } from '../src/git/worktree-manager';
import { categorizeFailure, FAILURE_CATEGORY_CONFIGS } from '../src/openspec/ralph-executor';
import { buildTaskContext } from '../src/openspec/context-assembler';
import type { OpenSpecChange } from '../src/openspec/detector';
import type { Task } from '../src/openspec/context-assembler';

const execFileAsync = promisify(execFile);

async function initGitRepo(dir: string): Promise<void> {
  await execFileAsync('git', ['init'], { cwd: dir });
  await execFileAsync('git', ['config', 'user.email', 'test@test.com'], { cwd: dir });
  await execFileAsync('git', ['config', 'user.name', 'Test'], { cwd: dir });
  fs.writeFileSync(path.join(dir, 'README.md'), 'init');
  await execFileAsync('git', ['add', '-A'], { cwd: dir });
  await execFileAsync('git', ['commit', '-m', 'init'], { cwd: dir });
}

async function getGitLog(dir: string): Promise<Array<{ hash: string; message: string; author: string }>> {
  const { stdout } = await execFileAsync(
    'git', ['log', '--pretty=format:%H%x00%s%x00%aE', '--no-merges'],
    { cwd: dir }
  );
  if (!stdout.trim()) return [];
  return stdout.trim().split('\n').map(line => {
    const parts = line.split('\0');
    return { hash: parts[0], message: parts[1], author: parts[2] };
  });
}

describe('WorktreeManager WIP commit', () => {
  let tmpDir: string;
  let repoDir: string;
  let manager: WorktreeManager;

  beforeEach(async () => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-wip-test-'));
    repoDir = path.join(tmpDir, 'repo');
    fs.mkdirSync(repoDir);
    await initGitRepo(repoDir);
    manager = new WorktreeManager();
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('createWipCommit', () => {
    it('should create commit with correct message and author when changes exist', async () => {
      fs.mkdirSync(path.join(repoDir, 'src'), { recursive: true });
      fs.writeFileSync(path.join(repoDir, 'src', 'index.ts'), 'console.log("hello")');

      const result = await manager.createWipCommit(repoDir, 'T-003', 1);

      expect(result).not.toBeNull();
      expect(typeof result).toBe('string');
      expect(result!.length).toBeGreaterThan(0);

      const log = await getGitLog(repoDir);
      const wipCommit = log.find(c => c.message.startsWith('WIP: T-003'));
      expect(wipCommit).toBeDefined();
      expect(wipCommit!.message).toBe('WIP: T-003 timeout (attempt 1)');
      expect(wipCommit!.author).toBe('mohist@wip');
    });

    it('should create commit with correct attempt number', async () => {
      fs.writeFileSync(path.join(repoDir, 'changed.txt'), 'content');

      const result = await manager.createWipCommit(repoDir, 'T-005', 3);

      expect(result).not.toBeNull();
      const log = await getGitLog(repoDir);
      const wipCommit = log.find(c => c.message.startsWith('WIP:'));
      expect(wipCommit!.message).toBe('WIP: T-005 timeout (attempt 3)');
    });

    it('should return null when no changes', async () => {
      const result = await manager.createWipCommit(repoDir, 'T-001', 1);
      expect(result).toBeNull();
    });

    it('should preserve multiple WIP commits for same task', async () => {
      fs.writeFileSync(path.join(repoDir, 'file1.txt'), 'v1');
      await manager.createWipCommit(repoDir, 'T-003', 1);

      fs.writeFileSync(path.join(repoDir, 'file1.txt'), 'v2');
      await manager.createWipCommit(repoDir, 'T-003', 2);

      const log = await getGitLog(repoDir);
      const wipCommits = log.filter(c => c.message.startsWith('WIP: T-003'));
      expect(wipCommits).toHaveLength(2);
      expect(wipCommits[0].message).toBe('WIP: T-003 timeout (attempt 2)');
      expect(wipCommits[1].message).toBe('WIP: T-003 timeout (attempt 1)');
    });
  });

  describe('findWipCommit', () => {
    it('should return commit info when WIP commit exists', async () => {
      fs.mkdirSync(path.join(repoDir, 'src'), { recursive: true });
      fs.writeFileSync(path.join(repoDir, 'src', 'index.ts'), 'content');
      await manager.createWipCommit(repoDir, 'T-003', 1);

      const info = await manager.findWipCommit(repoDir, 'T-003');

      expect(info).not.toBeNull();
      expect(info!.hash).toBeTruthy();
      expect(info!.message).toBe('WIP: T-003 timeout (attempt 1)');
      expect(info!.changedFiles.length).toBeGreaterThan(0);
      expect(info!.diffStat).toBeTruthy();
    });

    it('should return the most recent WIP commit for a task', async () => {
      fs.writeFileSync(path.join(repoDir, 'file1.txt'), 'v1');
      await manager.createWipCommit(repoDir, 'T-003', 1);

      fs.writeFileSync(path.join(repoDir, 'file1.txt'), 'v2');
      fs.writeFileSync(path.join(repoDir, 'file2.txt'), 'new');
      await manager.createWipCommit(repoDir, 'T-003', 2);

      const info = await manager.findWipCommit(repoDir, 'T-003');

      expect(info).not.toBeNull();
      expect(info!.message).toBe('WIP: T-003 timeout (attempt 2)');
    });

    it('should return null when no WIP commit exists', async () => {
      const info = await manager.findWipCommit(repoDir, 'T-999');
      expect(info).toBeNull();
    });

    it('should not match WIP commits from different tasks', async () => {
      fs.writeFileSync(path.join(repoDir, 'file.txt'), 'content');
      await manager.createWipCommit(repoDir, 'T-001', 1);

      const info = await manager.findWipCommit(repoDir, 'T-002');
      expect(info).toBeNull();
    });
  });

  describe('getWipDiffSummary', () => {
    it('should return diff stat when WIP commit exists', async () => {
      fs.mkdirSync(path.join(repoDir, 'src'), { recursive: true });
      fs.writeFileSync(path.join(repoDir, 'src', 'index.ts'), 'content');
      await manager.createWipCommit(repoDir, 'T-003', 1);

      const summary = await manager.getWipDiffSummary(repoDir, 'T-003');
      expect(summary).not.toBeNull();
      expect(summary!.length).toBeGreaterThan(0);
    });

    it('should return null when no WIP commit exists', async () => {
      const summary = await manager.getWipDiffSummary(repoDir, 'T-999');
      expect(summary).toBeNull();
    });
  });
});

describe('categorizeFailure with WIP', () => {
  it('should return timeout_with_wip for timeout error with wipCommitted=true', () => {
    expect(categorizeFailure('Timed out after 1800s', { wipCommitted: true })).toBe('timeout_with_wip');
    expect(categorizeFailure('Request timeout', { wipCommitted: true })).toBe('timeout_with_wip');
    expect(categorizeFailure('Operation timed out', { wipCommitted: true })).toBe('timeout_with_wip');
  });

  it('should return timeout for timeout error with wipCommitted=false', () => {
    expect(categorizeFailure('Timed out after 1800s', { wipCommitted: false })).toBe('timeout');
    expect(categorizeFailure('Request timeout', { wipCommitted: false })).toBe('timeout');
  });

  it('should return timeout for timeout error without options', () => {
    expect(categorizeFailure('Timed out after 1800s')).toBe('timeout');
    expect(categorizeFailure('Request timeout')).toBe('timeout');
  });

  it('should not affect non-timeout errors', () => {
    expect(categorizeFailure('Cannot find module express', { wipCommitted: true })).toBe('dependency');
    expect(categorizeFailure('npm install failed', { wipCommitted: true })).toBe('environment');
    expect(categorizeFailure('Test assertion failed', { wipCommitted: true })).toBe('ac_not_met');
  });

  it('should have correct config for timeout_with_wip', () => {
    expect(FAILURE_CATEGORY_CONFIGS.timeout_with_wip.maxAttempts).toBe(2);
    expect(FAILURE_CATEGORY_CONFIGS.timeout_with_wip.retryable).toBe(true);
  });
});

describe('buildTaskContext with WIP resume', () => {
  let tmpDir: string;
  let changeDir: string;
  let change: OpenSpecChange;

  const sampleTask: Task = {
    id: 'T-003',
    order: 3,
    title: 'Test Task',
    description: 'Test description',
    passes: false,
    attempts: 0,
  };

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-wip-context-'));
    changeDir = path.join(tmpDir, '42-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Proposal');
    fs.writeFileSync(path.join(changeDir, 'design.md'), '# Design');

    change = {
      changePath: changeDir,
      tasksPath: path.join(changeDir, 'tasks.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('should include [WIP Resume] section when wipResumeContext provided', () => {
    const wipContext = [
      'Task T-003 timed out on attempt 1.',
      'A WIP commit was saved with the following progress:',
      '',
      'Modified files:',
      '- src/index.ts',
      '- src/utils.ts',
      '',
      'Diff summary:',
      ' src/index.ts | 10 +++++-----',
      ' src/utils.ts  |  5 +++--',
      '',
      'Continue from this state.',
    ].join('\n');

    const result = buildTaskContext({
      change,
      task: sampleTask,
      learnings: [],
      wipResumeContext: wipContext,
    });

    expect(result.fullPrompt).toContain('[WIP Resume]');
    expect(result.fullPrompt).toContain('Task T-003 timed out on attempt 1.');
    expect(result.fullPrompt).toContain('Modified files:');
    expect(result.fullPrompt).toContain('- src/index.ts');
    expect(result.fullPrompt).toContain('Diff summary:');
  });

  it('should not include [WIP Resume] section when wipResumeContext absent', () => {
    const result = buildTaskContext({
      change,
      task: sampleTask,
      learnings: [],
    });

    expect(result.fullPrompt).not.toContain('[WIP Resume]');
  });

  it('should include [WIP Resume] section before [Previous Attempt Failed] in retry', () => {
    const wipContext = 'Some WIP context';

    const result = buildTaskContext({
      change,
      task: sampleTask,
      learnings: [],
      failureReason: 'Timed out after 1800s',
      isRetry: true,
      wipResumeContext: wipContext,
    });

    const wipIndex = result.fullPrompt.indexOf('[WIP Resume]');
    const retryIndex = result.fullPrompt.indexOf('[Previous Attempt Failed]');
    expect(wipIndex).toBeGreaterThan(-1);
    expect(retryIndex).toBeGreaterThan(-1);
    expect(wipIndex).toBeLessThan(retryIndex);
  });
});
