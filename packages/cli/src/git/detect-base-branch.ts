import { execFile } from 'child_process';
import * as fs from 'fs';
import { promisify } from 'util';

const execFileAsync = promisify(execFile);

export async function detectBaseBranch(projectPath: string): Promise<string> {
  try {
    if (!fs.existsSync(projectPath)) {
      return 'main';
    }

    await execFileAsync('git', ['rev-parse', '--git-dir'], {
      cwd: projectPath,
      timeout: 5000,
    });

    const ref = await execFileAsync(
      'git',
      ['symbolic-ref', 'refs/remotes/origin/HEAD'],
      { cwd: projectPath, encoding: 'utf-8', timeout: 5000 }
    ).catch(() => ({ stdout: '' }));

    const trimmed = ref.stdout.trim();
    const match = trimmed.match(/^refs\/remotes\/origin\/(.+)$/);
    return match ? match[1] : 'main';
  } catch {
    return 'main';
  }
}