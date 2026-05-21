import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from '../../checks';
import { Log } from '../../../util/log';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'shell-command-check' });

export class ShellCommandCheck implements Check {
  constructor(
    public readonly name: string,
    private readonly options: { command: string; timeout?: number; cwd?: string },
  ) {}

  async run(ctx: CheckContext): Promise<CheckResult> {
    const cwd = this.options.cwd ?? ctx.acpOptions.cwd;
    const timeout = this.options.timeout ?? 5 * 60 * 1000;
    const startTime = Date.now();
    log.info('Shell workflow check running', { checkName: this.name, command: this.options.command, timeout });

    try {
      const { stdout, stderr } = await execFileAsync(this.options.command, [], {
        cwd,
        timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });
      return {
        name: this.name,
        status: 'pass',
        message: `${this.name} passed`,
        output: {
          kind: 'shell-check',
          command: this.options.command,
          duration: Date.now() - startTime,
          stdout: truncate(stdout),
          stderr: truncate(stderr),
        },
      };
    } catch (err: any) {
      const output = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
      return {
        name: this.name,
        status: 'fail',
        message: `${this.name} failed${typeof err.code === 'number' ? ` (exit code ${err.code})` : ''}`,
        output: {
          kind: 'shell-check',
          command: this.options.command,
          duration: Date.now() - startTime,
          exitCode: typeof err.code === 'number' ? err.code : undefined,
          logExcerpt: truncate(output),
          timedOut: err.killed === true,
        },
      };
    }
  }
}

function truncate(text: string, maxLength = 10000): string {
  if (text.length <= maxLength) return text;
  return `${text.slice(0, maxLength / 2)}\n...[truncated]...\n${text.slice(-maxLength / 2)}`;
}
