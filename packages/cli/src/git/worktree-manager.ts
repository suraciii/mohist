import { execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { promisify } from 'util';

const execFileAsync = promisify(execFile);

export interface WorktreeInfo {
  worktreePath: string;
  branch: string;
  issueNumber: number;
}

function slugify(name: string): string {
  return name
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
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

export class WorktreeManager {

  async create(
    projectPath: string,
    projectName: string,
    issueNumber: number
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
    if (!fs.existsSync(baseDir)) {
      fs.mkdirSync(baseDir, { recursive: true });
    }

    try {
      await execFileAsync(
        'git',
        ['worktree', 'add', worktreePath, '-b', branch],
        { cwd: projectPath }
      );
    } catch (error: any) {
      throw new Error(
        `Failed to create worktree: ${error.message || error}`
      );
    }

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

  exists(projectName: string, issueNumber: number): boolean {
    const worktreePath = getWorktreePath(projectName, issueNumber);
    return fs.existsSync(worktreePath);
  }

  async prune(projectPath: string): Promise<void> {
    try {
      await execFileAsync('git', ['worktree', 'prune'], {
        cwd: projectPath,
      });
    } catch (error: any) {
      throw new Error(`Failed to prune worktrees: ${error.message || error}`);
    }
  }
}
