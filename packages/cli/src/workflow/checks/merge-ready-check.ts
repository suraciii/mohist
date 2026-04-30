import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from './index';

const execFileAsync = promisify(execFile);

export class MergeReadyCheck implements Check {
  public readonly name = 'MergeReadyCheck';

  async run(ctx: CheckContext): Promise<CheckResult> {
    try {
      const { stdout: statusOut } = await execFileAsync(
        'git',
        ['status', '--porcelain'],
        { cwd: ctx.changeDir }
      );

      const { stdout: logOut } = await execFileAsync(
        'git',
        ['log', '--oneline', '-3'],
        { cwd: ctx.changeDir }
      );

      const hasConflictingFiles = statusOut.includes('UU') || statusOut.includes('AA') || statusOut.includes('DD');

      if (hasConflictingFiles) {
        return {
          name: this.name,
          status: 'fail',
          message: 'Merge ready check failed: unresolved merge conflicts',
          output: { conflictingFiles: true, statusOutput: statusOut },
        };
      }

      return {
        name: this.name,
        status: 'pass',
        message: 'Branch is merge-ready',
        output: { statusOutput: statusOut, recentCommits: logOut },
      };
    } catch (err) {
      const error = err instanceof Error ? err.message : String(err);
      return {
        name: this.name,
        status: 'error',
        message: `Merge ready check error: ${error}`,
      };
    }
  }
}
