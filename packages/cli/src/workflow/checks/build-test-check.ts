import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from './index';
import { loadChecksConfig, DEFAULT_CHECKS_CONFIG, loadWorkflow } from '../workflow-loader';
import { Log } from '../../util/log';

const execFileAsync = promisify(execFile);

const log = Log.create({ service: 'build-test-check' });

function truncateLog(text: string, maxLength: number): string {
  if (text.length <= maxLength) return text;
  const half = Math.floor(maxLength / 2);
  return text.slice(0, half) + '\n\n...[truncated]...\n\n' + text.slice(-half);
}

export interface BuildTestCheckOptions {
  worktreePath: string;
}

export class BuildTestCheck implements Check {
  public readonly name = 'build-test';
  private worktreePath: string;

  constructor(options: BuildTestCheckOptions) {
    this.worktreePath = options.worktreePath;
  }

  async run(_ctx: CheckContext): Promise<CheckResult> {
    const workflow = loadWorkflow(this.worktreePath);
    const config = typeof workflow === 'string'
      ? DEFAULT_CHECKS_CONFIG.buildTest
      : loadChecksConfig(workflow).buildTest;

    const { command, timeout, autoFix, maxFixAttempts } = config;
    const startTime = Date.now();

    for (let attempt = 0; attempt <= (autoFix ? maxFixAttempts : 0); attempt++) {
      log.info('Build & Test check running', {
        command,
        timeout,
        attempt: attempt + 1,
        autoFix,
      });

      try {
        const { stdout, stderr } = await execFileAsync(command, [], {
          cwd: this.worktreePath,
          timeout,
          maxBuffer: 10 * 1024 * 1024,
          shell: true,
        });

        const duration = Date.now() - startTime;
        log.info('Build & Test check passed', { duration, attempt: attempt + 1 });

        return {
          name: this.name,
          status: 'pass',
          message: `Build & test passed${attempt > 0 ? ` (auto-fixed on attempt ${attempt + 1})` : ' on first attempt'}`,
          output: {
            duration,
            autoFixed: attempt > 0,
            buildLog: truncateLog(stdout + '\n' + stderr, 50000),
          },
        };
      } catch (err: any) {
        const output = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
        const isTimeout = err.killed === true;
        const duration = Date.now() - startTime;

        if (!autoFix || attempt >= maxFixAttempts) {
          log.warn('Build & Test check failed, no more attempts', {
            attempt: attempt + 1,
            isTimeout,
          });

          return {
            name: this.name,
            status: 'fail',
            message: isTimeout
              ? `Build & test timed out after ${timeout}ms`
              : `Build & test failed: ${err.message ?? 'unknown error'}`,
            output: {
              duration,
              buildLog: truncateLog(output, 50000),
            },
          };
        }

        log.info('Build & Test check failed, would auto-fix', {
          attempt: attempt + 1,
          maxFixAttempts,
        });
      }
    }

    const duration = Date.now() - startTime;
    return {
      name: this.name,
      status: 'fail',
      message: `Build & test failed after ${maxFixAttempts} auto-fix attempt(s)`,
      output: { duration },
    };
  }
}
