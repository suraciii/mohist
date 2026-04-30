import { execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { promisify } from 'util';
import { slugify } from '../utils/slugify';
import { Log } from '../util/log';

const log = Log.create({ service: 'worktree' });

const execFileAsync = promisify(execFile);

const FETCH_CACHE_FILE = 'mohist-last-fetch';
const FETCH_CACHE_MAX_AGE_MS = 30 * 60 * 1000;

export interface WorktreeInfo {
  worktreePath: string;
  branch: string;
  issueNumber: number;
}

export interface WipCommitInfo {
  hash: string;
  message: string;
  changedFiles: string[];
  diffStat: string;
}

const WIP_AUTHOR_NAME = 'mohist-wip';
const WIP_AUTHOR_EMAIL = 'mohist@wip';

function getWorktreeBaseDir(projectName: string): string {
  const home = process.env.HOME || '';
  const slug = slugify(projectName);
  return path.join(home, '.mohist', 'projects', slug, 'worktrees');
}

function getBranchName(issueNumber: number): string {
  return `mo/issue-${issueNumber}`;
}

function getWorktreePath(projectName: string, issueNumber: number): string {
  return path.join(getWorktreeBaseDir(projectName), `issue-${issueNumber}`);
}

function getLastFetchTime(projectPath: string): number {
  const cacheFile = path.join(projectPath, '.git', FETCH_CACHE_FILE);
  try {
    const content = fs.readFileSync(cacheFile, 'utf-8').trim();
    return parseInt(content, 10);
  } catch {
    return 0;
  }
}

function writeLastFetchTime(projectPath: string, time: number): void {
  const cacheFile = path.join(projectPath, '.git', FETCH_CACHE_FILE);
  fs.writeFileSync(cacheFile, time.toString(), 'utf-8');
}

async function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

const FETCH_MAX_ATTEMPTS = 3;
const FETCH_BACKOFF_MS = [1000, 2000];

export async function smartFetch(projectPath: string): Promise<void> {
  const lastFetch = getLastFetchTime(projectPath);
  if (Date.now() - lastFetch <= FETCH_CACHE_MAX_AGE_MS) {
    return;
  }

  let lastError: Error | undefined;
  for (let attempt = 0; attempt < FETCH_MAX_ATTEMPTS; attempt++) {
    try {
      await execFileAsync('git', ['fetch', 'origin', '--prune'], { cwd: projectPath });
      writeLastFetchTime(projectPath, Date.now());
      return;
    } catch (err: any) {
      lastError = err instanceof Error ? err : new Error(String(err));
      if (attempt < FETCH_MAX_ATTEMPTS - 1) {
        await sleep(FETCH_BACKOFF_MS[attempt]);
      }
    }
  }

  log.warn('git fetch origin failed, continuing with local refs', {
    attempts: FETCH_MAX_ATTEMPTS,
    error: lastError?.message || String(lastError),
  });
}

async function branchExists(projectPath: string, branch: string): Promise<boolean> {
  try {
    await execFileAsync('git', ['rev-parse', '--verify', branch], { cwd: projectPath });
    return true;
  } catch {
    return false;
  }
}

export interface WorktreeStatus {
  exists: boolean;
  branch: string;
  baseBranch?: string;
  ahead: number;
  behind: number;
  canFastForward: boolean;
  isRebaseInProgress: boolean;
  rebaseInProgress?: boolean;
  conflictingFiles?: string[];
}

export interface RebaseResult {
  success: boolean;
  conflicts: string[];
}

export class WorktreeManager {

  async create(
    projectPath: string,
    projectName: string,
    issueNumber: number,
    baseBranch: string = 'main'
  ): Promise<string> {
    const worktreePath = getWorktreePath(projectName, issueNumber);
    const branch = getBranchName(issueNumber);

    if (this.exists(projectName, issueNumber)) {
      return worktreePath;
    }

    try {
      await execFileAsync('git', ['rev-parse', '--git-dir'], {
        cwd: projectPath,
      });
    } catch {
      throw new Error('Project is not a git repository');
    }

    try {
      await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: projectPath });
    } catch {
      throw new Error(
        'Repository has no commits. Create an initial commit before starting an issue.'
      );
    }

    const baseDir = getWorktreeBaseDir(projectName);
    fs.mkdirSync(baseDir, { recursive: true });

    await smartFetch(projectPath);

    let startPoint = `origin/${baseBranch}`;
    const originExists = await branchExists(projectPath, `origin/${baseBranch}`);

    if (!originExists) {
      const localExists = await branchExists(projectPath, baseBranch);
      if (localExists) {
        startPoint = baseBranch;
      } else {
        throw new Error(`Branch '${baseBranch}' not found locally or on origin`);
      }
    }

    try {
      await execFileAsync(
        'git',
        ['worktree', 'add', worktreePath, '-b', branch, startPoint],
        { cwd: projectPath }
      );
    } catch (error: any) {
      throw new Error(
        `Failed to create worktree: ${error.message || error}`
      );
    }

    try {
      await execFileAsync('git', ['merge-base', branch, startPoint], {
        cwd: projectPath,
      });
    } catch {
      await this.remove(projectPath, projectName, issueNumber);
      throw new Error(
        `Branch '${branch}' has no common ancestor with '${baseBranch}'. Check project base branch configuration.`
      );
    }

    log.info('Worktree created', { projectName, issueNumber, branch, worktreePath });
    return worktreePath;
  }

  async mergeBack(
    projectPath: string,
    projectName: string,
    issueNumber: number,
    baseBranch: string = 'main'
  ): Promise<{ success: boolean; message: string }> {
    const branch = getBranchName(issueNumber);

    try {
      const { stdout: logOut } = await execFileAsync('git', ['log', `${baseBranch}..${branch}`, '--oneline'], { cwd: projectPath });
      if (logOut.trim().length === 0) {
        log.info('No commits to merge back', { issueNumber, branch, baseBranch });
        return { success: true, message: `No commits to merge for issue #${issueNumber}` };
      }
    } catch {
      // ignore, proceed to merge attempt
    }

    try {
      await execFileAsync('git', ['checkout', baseBranch], { cwd: projectPath });
    } catch (err) {
      return { success: false, message: `Failed to checkout ${baseBranch}: ${err instanceof Error ? err.message : String(err)}` };
    }

    try {
      await execFileAsync('git', ['merge', '--ff-only', branch], { cwd: projectPath });
      log.info('Fast-forward merge succeeded', { issueNumber, branch, baseBranch });
      return { success: true, message: `Merged ${branch} into ${baseBranch} (fast-forward)` };
    } catch {
      log.info('Fast-forward not possible, attempting rebase then merge', { issueNumber, branch, baseBranch });
    }

    const worktreePath = getWorktreePath(projectName, issueNumber);
    if (!worktreePath) {
      return { success: false, message: `Worktree not found for issue #${issueNumber}` };
    }

    await smartFetch(projectPath);

    try {
      await execFileAsync('git', ['rebase', '--abort'], { cwd: worktreePath });
    } catch { /* no rebase in progress */ }

    try {
      await execFileAsync('git', ['rebase', baseBranch], { cwd: worktreePath });
      log.info('Rebase succeeded cleanly', { issueNumber, branch, baseBranch });
    } catch {
      log.info('Rebase has conflicts, auto-resolving with theirs', { issueNumber });

      for (let attempt = 0; attempt < 50; attempt++) {
        try {
          await execFileAsync('git', ['checkout', '--theirs', '.'], { cwd: worktreePath });
          await execFileAsync('git', ['add', '.'], { cwd: worktreePath });
          await execFileAsync('git', ['rebase', '--continue'], {
            cwd: worktreePath,
            env: { ...process.env, GIT_EDITOR: 'true' },
          });
          log.info('Rebase continued after auto-resolve', { issueNumber, attempt });
          break;
        } catch (continueErr) {
          const stillRebasing = await this.isRebaseInProgress(projectName, issueNumber).catch(() => false);
          if (!stillRebasing) {
            log.warn('Rebase aborted during auto-resolve', { issueNumber, attempt });
            try { await execFileAsync('git', ['rebase', '--abort'], { cwd: worktreePath }); } catch { /* */ }
            return { success: false, message: `Rebase failed during auto-resolve: ${continueErr instanceof Error ? continueErr.message : String(continueErr)}` };
          }
          log.info('More conflicts after continue, retrying', { issueNumber, attempt });
        }
      }

      const stillRebasing = await this.isRebaseInProgress(projectName, issueNumber).catch(() => false);
      if (stillRebasing) {
        try { await execFileAsync('git', ['rebase', '--abort'], { cwd: worktreePath }); } catch { /* */ }
        return { success: false, message: `Rebase auto-resolve exceeded max attempts for issue #${issueNumber}` };
      }
    }

    try {
      await execFileAsync('git', ['checkout', baseBranch], { cwd: projectPath });
      await execFileAsync('git', ['merge', '--ff-only', branch], { cwd: projectPath });
      log.info('Merge succeeded after rebase', { issueNumber, branch, baseBranch });
      return { success: true, message: `Merged ${branch} into ${baseBranch} (rebase + fast-forward)` };
    } catch (err) {
      return { success: false, message: `Merge failed after rebase: ${err instanceof Error ? err.message : String(err)}` };
    }
  }

  async canFastForward(
    projectPath: string,
    projectName: string,
    issueNumber: number,
    baseBranch: string = 'main'
  ): Promise<boolean> {
    const branch = getBranchName(issueNumber);

    if (!this.exists(projectName, issueNumber)) {
      return false;
    }

    try {
      await execFileAsync(
        'git',
        ['merge-base', '--is-ancestor', baseBranch, branch],
        { cwd: projectPath }
      );
      return true;
    } catch {
      return false;
    }
  }

  async rebaseOntoMaster(
    projectPath: string,
    projectName: string,
    issueNumber: number,
    baseBranch: string = 'main',
    options?: { abortOnConflict?: boolean }
  ): Promise<RebaseResult> {
    const abortOnConflict = options?.abortOnConflict !== false;
    const worktreePath = getWorktreePath(projectName, issueNumber);
    const branch = getBranchName(issueNumber);

    if (!this.exists(projectName, issueNumber)) {
      throw new Error(`Worktree for issue #${issueNumber} not found`);
    }

    await smartFetch(projectPath);

    try {
      await execFileAsync('git', ['rebase', '--abort'], { cwd: worktreePath });
      log.info('Aborted stale rebase before starting new one', { issueNumber });
    } catch {
      // no rebase in progress, that's fine
    }

    try {
      const { stdout: statusOut } = await execFileAsync(
        'git', ['status', '--porcelain', '--ignore-submodules'],
        { cwd: worktreePath }
      );
      const uncommitted = statusOut.trim().split('\n').filter(l => l.trim());
      if (uncommitted.length > 0) {
        await execFileAsync('git', ['add', '--', ':!.opencode/'], { cwd: worktreePath });
        const remaining = await execFileAsync('git', ['status', '--porcelain', '--ignore-submodules'], { cwd: worktreePath });
        if (remaining.stdout.trim()) {
          await execFileAsync('git', ['commit', '-m', `chore: commit remaining changes for issue #${issueNumber}`, '--no-verify'], { cwd: worktreePath });
        }
      }
    } catch (err) {
      log.warn('Failed to commit uncommitted changes before rebase', { issueNumber, error: err instanceof Error ? err.message : String(err) });
    }

    try {
      await execFileAsync('git', ['rebase', baseBranch], { cwd: worktreePath });
      log.info('Rebase succeeded', { issueNumber, branch, baseBranch });
      return { success: true, conflicts: [] };
    } catch (err: any) {
      const conflicts = await this.getConflictingFiles(worktreePath);
      log.warn('Rebase conflicts detected', { issueNumber, conflicts, abortOnConflict });

      if (abortOnConflict) {
        try {
          await execFileAsync('git', ['rebase', '--abort'], { cwd: worktreePath });
          log.info('Rebase aborted due to conflicts', { issueNumber });
        } catch (abortErr) {
          log.warn('Failed to abort rebase', { issueNumber, error: abortErr instanceof Error ? abortErr.message : String(abortErr) });
        }
      }

      return { success: false, conflicts };
    }
  }

  async abortRebase(
    projectName: string,
    issueNumber: number
  ): Promise<void> {
    const worktreePath = getWorktreePath(projectName, issueNumber);
    await execFileAsync('git', ['rebase', '--abort'], { cwd: worktreePath });
    log.info('Rebase aborted', { issueNumber });
  }

  async isRebaseInProgress(
    projectName: string,
    issueNumber: number
  ): Promise<boolean> {
    const worktreePath = getWorktreePath(projectName, issueNumber);
    try {
      const { stdout } = await execFileAsync('git', ['rev-parse', '--git-dir'], { cwd: worktreePath });
      const gitDir = stdout.trim();
      return fs.existsSync(path.resolve(gitDir, 'rebase-merge')) || fs.existsSync(path.resolve(gitDir, 'rebase-apply'));
    } catch {
      return false;
    }
  }

  async getWorktreeStatus(
    projectPath: string,
    projectName: string,
    issueNumber: number,
    baseBranch: string = 'main'
  ): Promise<WorktreeStatus> {
    const empty: WorktreeStatus = {
      exists: false,
      branch: '',
      baseBranch,
      ahead: 0,
      behind: 0,
      canFastForward: false,
      isRebaseInProgress: false,
    };

    if (!this.exists(projectName, issueNumber)) {
      return empty;
    }

    const branch = getBranchName(issueNumber);
    const rebaseInProgress = await this.isRebaseInProgress(projectName, issueNumber);

    let conflictingFiles: string[] | undefined;
    if (rebaseInProgress) {
      const worktreePath = getWorktreePath(projectName, issueNumber);
      conflictingFiles = await this.getConflictingFiles(worktreePath);
    }

    try {
      const { stdout } = await execFileAsync(
        'git',
        ['rev-list', '--left-right', '--count', `${baseBranch}...${branch}`],
        { cwd: projectPath }
      );
      const parts = stdout.trim().split('\t');
      const behind = parseInt(parts[0], 10) || 0;
      const ahead = parseInt(parts[1], 10) || 0;

      return {
        exists: true,
        branch,
        baseBranch,
        ahead,
        behind,
        canFastForward: behind === 0,
        isRebaseInProgress: rebaseInProgress,
        rebaseInProgress,
        conflictingFiles,
      };
    } catch {
      return {
        exists: true,
        branch,
        baseBranch,
        ahead: 0,
        behind: 0,
        canFastForward: false,
        isRebaseInProgress: rebaseInProgress,
        rebaseInProgress,
        conflictingFiles,
      };
    }
  }

  async rebaseContinue(
    projectName: string,
    issueNumber: number
  ): Promise<RebaseResult> {
    const worktreePath = getWorktreePath(projectName, issueNumber);

    try {
      await execFileAsync('git', ['rebase', '--continue'], {
        cwd: worktreePath,
        env: { ...process.env, GIT_EDITOR: 'true' },
      });
      log.info('Rebase continued successfully', { issueNumber });
      return { success: true, conflicts: [] };
    } catch (err: any) {
      const conflicts = await this.getConflictingFiles(worktreePath);
      log.warn('Rebase still has conflicts after continue', { issueNumber, conflicts });
      return { success: false, conflicts };
    }
  }

  private async getConflictingFiles(worktreePath: string): Promise<string[]> {
    try {
      const { stdout } = await execFileAsync(
        'git', ['diff', '--name-only', '--diff-filter=U'],
        { cwd: worktreePath }
      );
      return stdout.trim().split('\n').filter(l => l.trim());
    } catch {
      return [];
    }
  }

  async remove(
    projectPath: string,
    projectName: string,
    issueNumber: number
  ): Promise<void> {
    const worktreePath = getWorktreePath(projectName, issueNumber);
    const branch = getBranchName(issueNumber);

    try {
      await execFileAsync(
        'git',
        ['worktree', 'remove', worktreePath, '--force'],
        { cwd: projectPath }
      );
    } catch (error: any) {
      const msg = error.message || String(error);
      if (!msg.includes("not a worktree") && !msg.includes("no such file")) {
        throw new Error(`Failed to remove worktree: ${msg}`);
      }
    }

    try {
      await execFileAsync(
        'git',
        ['branch', '-d', branch],
        { cwd: projectPath }
      );
    } catch (error: any) {
      const msg = error.message || String(error);
      if (!msg.includes("not found") && !msg.includes("no such branch") && !msg.includes("Cannot delete branch")) {
        throw new Error(`Failed to delete branch: ${msg}`);
      }
    }

    log.info('Worktree removed', { projectName, issueNumber, branch });
  }

  async list(projectPath: string): Promise<WorktreeInfo[]> {
    let stdout: string;
    try {
      const result = await execFileAsync('git', ['worktree', 'list', '--porcelain'], {
        cwd: projectPath,
      });
      stdout = result.stdout;
    } catch (error: any) {
      throw new Error(`Failed to list worktrees: ${error.message || error}`);
    }

    const worktrees: WorktreeInfo[] = [];
    const blocks = stdout.split('\n\n').filter((b) => b.trim());

    for (const block of blocks) {
      const lines = block.split('\n');
      let wtPath = '';
      let branch = '';

      for (const line of lines) {
        if (line.startsWith('worktree ')) {
          wtPath = line.slice('worktree '.length);
        } else if (line.startsWith('branch ')) {
          branch = line.slice('branch '.length);
          if (branch.startsWith('refs/heads/')) {
            branch = branch.slice('refs/heads/'.length);
          }
        }
      }

      const match = branch.match(/^mo\/issue-(\d+)$/);
      if (match) {
        worktrees.push({
          worktreePath: wtPath,
          branch,
          issueNumber: parseInt(match[1], 10),
        });
      }
    }

    return worktrees;
  }

  async createWipCommit(
    worktreePath: string,
    taskId: string,
    attemptNumber: number
  ): Promise<string | null> {
    try {
      const { stdout: statusOut } = await execFileAsync(
        'git', ['status', '--porcelain', '--ignore-submodules'],
        { cwd: worktreePath }
      );
      if (!statusOut.trim()) {
        return null;
      }

      await execFileAsync('git', ['add', '-A'], { cwd: worktreePath });

      const { stdout: remaining } = await execFileAsync(
        'git', ['status', '--porcelain', '--ignore-submodules'],
        { cwd: worktreePath }
      );
      if (!remaining.trim()) {
        return null;
      }

      const message = `WIP: ${taskId} timeout (attempt ${attemptNumber})`;
      await execFileAsync(
        'git',
        ['commit', '-m', message, '--no-verify', '--author', `${WIP_AUTHOR_NAME} <${WIP_AUTHOR_EMAIL}>`],
        { cwd: worktreePath }
      );

      const { stdout: hash } = await execFileAsync(
        'git', ['rev-parse', 'HEAD'],
        { cwd: worktreePath }
      );

      log.info('WIP commit created', { worktreePath, taskId, attemptNumber, hash: hash.trim() });
      return hash.trim();
    } catch (err) {
      log.warn('Failed to create WIP commit', {
        worktreePath,
        taskId,
        error: err instanceof Error ? err.message : String(err),
      });
      return null;
    }
  }

  async findWipCommit(worktreePath: string, taskId: string): Promise<WipCommitInfo | null> {
    try {
      const pattern = `WIP: ${taskId} timeout*`;
      const { stdout } = await execFileAsync(
        'git',
        ['log', `--author=${WIP_AUTHOR_EMAIL}`, '--grep', pattern, '-1', '--pretty=format:%H%n%s'],
        { cwd: worktreePath }
      );

      if (!stdout.trim()) {
        return null;
      }

      const lines = stdout.trim().split('\n');
      const hash = lines[0];
      const message = lines.slice(1).join('\n');

      const { stdout: nameOnlyOut } = await execFileAsync(
        'git', ['diff-tree', '--no-commit-id', '--name-only', '-r', hash],
        { cwd: worktreePath }
      );
      const changedFiles = nameOnlyOut.trim().split('\n').filter(l => l.trim());

      const { stdout: diffStatOut } = await execFileAsync(
        'git', ['diff', '--stat', `${hash}^..${hash}`],
        { cwd: worktreePath }
      );

      return { hash, message, changedFiles, diffStat: diffStatOut.trim() };
    } catch {
      return null;
    }
  }

  async getWipDiffSummary(worktreePath: string, taskId: string): Promise<string | null> {
    const wip = await this.findWipCommit(worktreePath, taskId);
    if (!wip) {
      return null;
    }
    return wip.diffStat;
  }

  getPath(projectName: string, issueNumber: number): string | null {
    const worktreePath = getWorktreePath(projectName, issueNumber);
    return fs.existsSync(worktreePath) ? worktreePath : null;
  }

  exists(projectName: string, issueNumber: number): boolean {
    const worktreePath = getWorktreePath(projectName, issueNumber);
    return fs.existsSync(worktreePath);
  }

  async prune(projectPath: string): Promise<void> {
    try {
      await execFileAsync('git', ['worktree', 'prune'], {
        cwd: projectPath,
      });
      log.info('Worktrees pruned', { projectPath });
    } catch (error: any) {
      throw new Error(`Failed to prune worktrees: ${error.message || error}`);
    }
  }
}
