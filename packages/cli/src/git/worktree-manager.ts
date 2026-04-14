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
      await execFileAsync('git', ['fetch', 'origin'], { cwd: projectPath });
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

    log.info('Worktree created', { projectName, issueNumber, branch, worktreePath });
    return worktreePath;
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
      if (!msg.includes("not found") && !msg.includes("no such branch")) {
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
