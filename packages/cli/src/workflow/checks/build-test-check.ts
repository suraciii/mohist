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

function buildCheckAutoFixPrompt(buildLog: string): string {
  const truncatedLog = buildLog.length > 8000
    ? buildLog.slice(0, 4000) + '\n\n...[truncated]...\n\n' + buildLog.slice(-4000)
    : buildLog;

  return [
    '## Task',
    '',
    'Build & test 检查失败，请修复代码使 build 和 test 通过。',
    '',
    '## Build/Test Error Output',
    '',
    '```',
    truncatedLog,
    '```',
    '',
    '## Process',
    '',
    '1. Read the error output above carefully',
    '2. Identify the root cause of each error',
    '3. Fix the source code files that cause the errors',
    '4. Do NOT modify test expectations to hide real bugs — only fix the source code',
    '5. If a test is genuinely wrong, fix the test',
    '',
    '## Rules',
    '',
    '- Apply ONLY the minimal fixes needed to resolve the errors',
    '- Do NOT refactor or change unrelated code',
    '- If the error is in a dependency, update the dependency or work around it',
    '- If you cannot fix an error, leave a TODO comment explaining why',
  ].join('\n');
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

  async run(ctx: CheckContext): Promise<CheckResult> {
    const workflow = loadWorkflow(this.worktreePath);
    const config = typeof workflow === 'string'
      ? DEFAULT_CHECKS_CONFIG.buildTest
      : loadChecksConfig(workflow).buildTest;

    const { command, timeout, autoFix, maxFixAttempts } = config;
    const startTime = Date.now();

    let lastBuildLog = '';

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
          message: `Build & test 通过${attempt > 0 ? ` (第 ${attempt + 1} 次尝试自动修复成功)` : ''}`,
          output: {
            duration,
            autoFixed: attempt > 0,
            buildLog: truncateLog(stdout + '\n' + stderr, 50000),
          },
        };
      } catch (err: any) {
        const output = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
        lastBuildLog = output;
        const isTimeout = err.killed === true;

        if (!autoFix || attempt >= maxFixAttempts) {
          log.warn('Build & Test check failed, no more attempts', {
            attempt: attempt + 1,
            isTimeout,
          });

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

        log.info('Build & Test check failed, spawning coder agent to auto-fix', {
          attempt: attempt + 1,
          maxFixAttempts,
          issueNumber: ctx.issue.number,
        });

        const fixResult = await this.runAutoFixAgent(ctx, output);
        if (!fixResult) {
          log.warn('Auto-fix agent failed or unavailable, retrying build without fix', {
            issueNumber: ctx.issue.number,
            attempt: attempt + 1,
          });
        }
      }
    }

    const duration = Date.now() - startTime;
    return {
      name: this.name,
      status: 'fail',
      message: `Build & test 失败 — ${maxFixAttempts} 次自动修复尝试均未成功`,
      output: { duration, buildLog: truncateLog(lastBuildLog, 50000) },
    };
  }

  private async runAutoFixAgent(ctx: CheckContext, buildLog: string): Promise<boolean> {
    try {
      const { runAcpSession } = await import('../../agent-runtime/acp-session');
      const prompt = buildCheckAutoFixPrompt(buildLog);

      const result = await runAcpSession({
        cwd: this.worktreePath,
        task: prompt,
        taskId: `check-auto-fix-${ctx.issue.number}`,
        issueId: ctx.issue.id,
        projectId: ctx.projectId,
        workflowLogRepo: ctx.acpOptions?.workflowLogRepo,
        eventBus: ctx.eventBus,
        coderSessionRepo: ctx.acpOptions?.coderSessionRepo,
        issueNumber: ctx.issue.number,
        opencodeBinPath: ctx.acpOptions?.opencodeBinPath,
        model: ctx.acpOptions?.model,
        stage: 'check',
        timeout: 10 * 60 * 1000,
      });

      log.info('Auto-fix agent completed', {
        issueNumber: ctx.issue.number,
        success: result.success,
        textLength: result.text?.length ?? 0,
      });

      return result.success;
    } catch (err) {
      log.error('Auto-fix agent failed', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      return false;
    }
  }
}
