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

function extractKeyErrorLines(stderr: string, maxLines: number = 15): string {
  if (!stderr) return '';
  const lines = stderr.split('\n');
  const errorLines: string[] = [];
  const errorPatterns = [/error/i, /fail/i, /cannot find/i, /not found/i, /unexpected/i, /syntax error/i, /type error/i, /referenceerror/i, /typescript/i];
  for (const line of lines) {
    if (errorPatterns.some(p => p.test(line))) {
      errorLines.push(line);
    }
    if (errorLines.length >= maxLines) break;
  }
  if (errorLines.length === 0) {
    const tail = lines.filter(l => l.trim()).slice(-maxLines);
    return tail.join('\n');
  }
  return errorLines.join('\n');
}

function formatBuildErrorMessage(err: any): string {
  const isTimeout = err.killed === true;
  if (isTimeout) {
    return 'Build & test 超时';
  }

  const stderr = err.stderr || '';
  const stdout = err.stdout || '';
  const combined = [stdout, stderr].filter(Boolean).join('\n');

  let exitCode = err.code;
  if (typeof exitCode !== 'number' && err.message) {
    const match = err.message.match(/exit code (\d+)/);
    if (match) exitCode = parseInt(match[1], 10);
  }

  const keyErrors = extractKeyErrorLines(combined);

  const parts: string[] = [];
  if (typeof exitCode === 'number') {
    parts.push(`Build & test 失败 (exit code ${exitCode})`);
  } else {
    parts.push('Build & test 失败');
  }

  if (keyErrors) {
    const oneLine = keyErrors.split('\n').filter(l => l.trim()).slice(0, 3).join(' | ');
    if (oneLine.length > 200) {
      parts.push(oneLine.slice(0, 200) + '...');
    } else {
      parts.push(oneLine);
    }
  }

  return parts.join(' — ');
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

    const { command, timeout } = config;
    const startTime = Date.now();

    log.info('Build & Test check running', { command, timeout });

    try {
      const { stdout, stderr } = await execFileAsync(command, [], {
        cwd: this.worktreePath,
        timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });

      const duration = Date.now() - startTime;
      log.info('Build & Test check passed', { duration });

      return {
        name: this.name,
        status: 'pass',
        message: 'Build & test 通过',
        output: {
          duration,
          buildLog: truncateLog(stdout + '\n' + stderr, 50000),
        },
      };
    } catch (err: any) {
      const output = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
      const isTimeout = err.killed === true;

      log.warn('Build & Test check failed', { isTimeout });

      return {
        name: this.name,
        status: 'fail',
        message: formatBuildErrorMessage(err),
        output: {
          duration: Date.now() - startTime,
          buildLog: truncateLog(output, 50000),
        },
      };
    }
  }

}
