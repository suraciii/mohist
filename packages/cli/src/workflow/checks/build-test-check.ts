import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from './index';

const execFileAsync = promisify(execFile);

export interface BuildTestCheckOptions {
  command: string;
  args?: string[];
  cwd?: string;
}

export class BuildTestCheck implements Check {
  public readonly name = 'BuildTestCheck';
  private command: string;
  private args: string[];
  private cwd?: string;

  constructor(options?: BuildTestCheckOptions) {
    this.command = options?.command ?? 'npm';
    this.args = options?.args ?? ['test'];
    this.cwd = options?.cwd;
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const cwd = this.cwd ?? ctx.changeDir;

    try {
      const { stdout, stderr } = await execFileAsync(this.command, this.args, {
        cwd,
        timeout: 300000,
      });

      const output = stdout || stderr;

      return {
        name: this.name,
        status: 'pass',
        message: 'Build test passed',
        output,
      };
    } catch (err) {
      const error = err instanceof Error ? err.message : String(err);
      const stderr = (err as { stderr?: string }).stderr ?? '';

      return {
        name: this.name,
        status: 'fail',
        message: `Build test failed: ${error}`,
        output: stderr || error,
      };
    }
  }
}
