import { execFile } from 'child_process';
import * as fs from 'fs';
import { promisify } from 'util';

const execFileAsync = promisify(execFile);

const GIT_OPTS = { encoding: 'utf-8' as const, timeout: 5000 };

async function tryGit(projectPath: string, args: string[]): Promise<string | null> {
  try {
    const { stdout } = await execFileAsync('git', args, { cwd: projectPath, ...GIT_OPTS });
    return stdout.trim();
  } catch {
    return null;
  }
}

export async function detectBaseBranch(projectPath: string): Promise<string> {
  if (!fs.existsSync(projectPath)) {
    return 'main';
  }

  const gitDir = await tryGit(projectPath, ['rev-parse', '--git-dir']);
  if (!gitDir) {
    return 'main';
  }

  const headRef = await tryGit(projectPath, ['symbolic-ref', 'refs/remotes/origin/HEAD']);
  if (headRef) {
    const match = headRef.match(/^refs\/remotes\/origin\/(.+)$/);
    if (match) return match[1];
  }

  const mainSha = await tryGit(projectPath, ['rev-parse', '--verify', 'origin/main']);
  if (mainSha) return 'main';

  const masterSha = await tryGit(projectPath, ['rev-parse', '--verify', 'origin/master']);
  if (masterSha) return 'master';

  const headBranch = await tryGit(projectPath, ['rev-parse', '--abbrev-ref', 'HEAD']);
  if (headBranch && headBranch !== 'HEAD') return headBranch;

  return 'main';
}