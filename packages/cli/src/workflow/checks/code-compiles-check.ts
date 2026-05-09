import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from './index';
import { loadWorkflow, loadChecksConfig, DEFAULT_CHECKS_CONFIG } from '../workflow-loader';
import { Log } from '../../util/log';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'code-compiles-check' });

export interface CodeCompilesCheckOptions {
  worktreePath: string;
}

export class CodeCompilesCheck implements Check {
  public readonly name = 'code-compiles';
  private worktreePath: string;

  constructor(options: CodeCompilesCheckOptions) {
    this.worktreePath = options.worktreePath;
  }

  async run(_ctx: CheckContext): Promise<CheckResult> {
    const command = this.getBuildCommand();
    const timeout = this.getBuildTimeout();

    log.info('Code compiles check running', { command, timeout });

    try {
      await execFileAsync(command, [], {
        cwd: this.worktreePath,
        timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });

      log.info('Code compiles check passed');
      return {
        name: this.name,
        status: 'pass',
        message: 'Code compiles successfully',
      };
    } catch (err: any) {
      const output = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
      const exitCode = err.code;

      log.warn('Code compiles check failed', {
        exitCode: typeof exitCode === 'number' ? exitCode : 'unknown',
        isTimeout: err.killed === true,
      });

      return {
        name: this.name,
        status: 'fail',
        message: `Build failed${typeof exitCode === 'number' ? ` (exit code ${exitCode})` : ''}`,
        output: {
          buildLog: output.length > 10000 ? output.slice(0, 5000) + '\n...\n' + output.slice(-5000) : output,
        },
      };
    }
  }

  private getBuildCommand(): string {
    const workflow = loadWorkflow(this.worktreePath);
    if (typeof workflow === 'string') {
      return 'npm run build';
    }
    const config = loadChecksConfig(workflow);
    return config.buildTest.command.split('&&')[0].trim() || 'npm run build';
  }

  private getBuildTimeout(): number {
    const workflow = loadWorkflow(this.worktreePath);
    if (typeof workflow === 'string') {
      return DEFAULT_CHECKS_CONFIG.buildTest.timeout;
    }
    return loadChecksConfig(workflow).buildTest.timeout;
  }
}
