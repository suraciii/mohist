import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from '@mohist/workflow/checks';
import { Log } from '../../../util/log';

const execFileAsync = promisify(execFile);

const log = Log.create({ service: 'health-gate-check' });

const MAX_LOG_LENGTH = 50000;

function truncateLog(text: string, maxLength: number = MAX_LOG_LENGTH): string {
  if (text.length <= maxLength) return text;
  const half = Math.floor(maxLength / 2);
  return text.slice(0, half) + '\n\n...[truncated]...\n\n' + text.slice(-half);
}

function extractKeyErrorLines(stderr: string, maxLines: number = 15): string {
  if (!stderr) return '';
  const lines = stderr.split('\n');
  const errorLines: string[] = [];
  const errorPatterns = [
    /error/i,
    /fail/i,
    /cannot find/i,
    /not found/i,
    /unexpected/i,
    /syntax error/i,
    /type error/i,
    /referenceerror/i,
    /typescript/i,
  ];
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

function formatHealthGateErrorMessage(
  command: string,
  stderr: string,
  stdout: string,
  exitCode: number | undefined,
  isTimeout: boolean,
): string {
  if (isTimeout) {
    return `${command} — 超时`;
  }

  const combined = [stdout, stderr].filter(Boolean).join('\n');
  const keyErrors = extractKeyErrorLines(combined);

  const parts: string[] = [];
  if (typeof exitCode === 'number') {
    parts.push(`${command} 失败 (exit code ${exitCode})`);
  } else {
    parts.push(`${command} 失败`);
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

export interface HealthGateCheckOptions {
  worktreePath: string;
  policy: HealthGatePolicy;
  stage: string;
  name?: string;
}

export interface HealthGatePolicy {
  enabled: boolean;
  command: string;
  timeout: number;
  autoFix?: boolean;
  maxFixAttempts?: number;
}

export class HealthGateCheck implements Check {
  public readonly name: string;
  private worktreePath: string;
  private policy: HealthGatePolicy;
  private stage: string;

  constructor(options: HealthGateCheckOptions) {
    this.worktreePath = options.worktreePath;
    this.policy = options.policy;
    this.stage = options.stage;
    this.name = options.name ?? `health:${this.stage}`;
  }

  async run(_ctx: CheckContext): Promise<CheckResult> {
    const candidateHeadSha = await this.resolveHeadSha();

    if (!this.policy.enabled) {
      return {
        name: this.name,
        status: 'pass',
        message: `${this.name} 已禁用`,
        output: {
          kind: 'health-gate',
          stage: this.stage,
          command: this.policy.command,
          timeout: this.policy.timeout,
          duration: 0,
          enabled: false,
          logExcerpt: '',
          candidateHeadSha,
        },
      };
    }

    const { command, timeout } = this.policy;
    const startTime = Date.now();

    log.info('HealthGate check running', { name: this.name, command, timeout });

    try {
      const { stdout, stderr } = await execFileAsync(command, [], {
        cwd: this.worktreePath,
        timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });

      const duration = Date.now() - startTime;
      log.info('HealthGate check passed', { name: this.name, duration });

      return {
        name: this.name,
        status: 'pass',
        message: `${this.name} 通过`,
        output: {
          kind: 'health-gate',
          stage: this.stage,
          command,
          timeout,
          duration,
          enabled: true,
          logExcerpt: truncateLog(stdout + '\n' + stderr, 5000),
          candidateHeadSha,
        },
      };
    } catch (err: any) {
      const duration = Date.now() - startTime;
      const isTimeout = err.killed === true;
      const stderr = err.stderr || '';
      const stdout = err.stdout || '';

      let exitCode = err.code;
      if (typeof exitCode !== 'number' && err.message) {
        const match = err.message.match(/exit code (\d+)/);
        if (match) exitCode = parseInt(match[1], 10);
      }

      log.warn('HealthGate check failed', { name: this.name, isTimeout, exitCode });

      return {
        name: this.name,
        status: 'fail',
        message: formatHealthGateErrorMessage(
          this.policy.command,
          stderr,
          stdout,
          exitCode,
          isTimeout,
        ),
        output: {
          kind: 'health-gate',
          stage: this.stage,
          command,
          timeout,
          duration,
          enabled: true,
          exitCode: typeof exitCode === 'number' ? exitCode : undefined,
          timedOut: isTimeout,
          summary: formatHealthGateErrorMessage(
            this.policy.command,
            stderr,
            stdout,
            exitCode,
            isTimeout,
          ),
          logExcerpt: truncateLog([stdout, stderr, err.message].filter(Boolean).join('\n'), 5000),
          candidateHeadSha,
        },
      };
    }
  }

  private async resolveHeadSha(): Promise<string | undefined> {
    try {
      const { stdout } = await execFileAsync('git', ['rev-parse', 'HEAD'], {
        cwd: this.worktreePath,
        timeout: 5000,
      });
      return stdout.trim() || undefined;
    } catch {
      return undefined;
    }
  }

}
