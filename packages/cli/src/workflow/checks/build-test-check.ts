import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';
import { Stage } from '../../types';
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
  public readonly reaction: ReactionConfig = {
    type: 'auto-fix',
    maxAttempts: 2,
    fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build },
  };
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

  async fix(ctx: CheckContext): Promise<void> {
    const workflow = loadWorkflow(this.worktreePath);
    const config = typeof workflow === 'string'
      ? DEFAULT_CHECKS_CONFIG.buildTest
      : loadChecksConfig(workflow).buildTest;

    // Run build once to capture the error log for the auto-fix prompt
    let buildLog = '';
    try {
      await execFileAsync(config.command, [], {
        cwd: this.worktreePath,
        timeout: config.timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });
      // Build passed unexpectedly — nothing to fix
      log.info('BuildTestCheck auto-fix: build passed unexpectedly', {
        issueNumber: ctx.issue.number,
      });
      return;
    } catch (err: any) {
      buildLog = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
    }

    log.info('BuildTestCheck auto-fix: spawning coder agent', {
      issueNumber: ctx.issue.number,
    });

    try {
      const { withSession } = await import('../../agent-runtime/agent-session');
      const prompt = buildCheckAutoFixPrompt(buildLog);

      const result = await withSession({
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
        title: 'Auto-fix: test failures',
        onBeforeKill: async (cwd: string) => {
          try {
            const { stdout: statusOut } = await execFileAsync('git', ['status', '--porcelain', '--ignore-submodules'], { cwd });
            if (!statusOut.trim()) return false;
            await execFileAsync('git', ['add', '-A'], { cwd });
            const { stdout: remaining } = await execFileAsync('git', ['status', '--porcelain', '--ignore-submodules'], { cwd });
            if (!remaining.trim()) return false;
            await execFileAsync('git', ['commit', '-m', `WIP: check-auto-fix-${ctx.issue.number} timeout`, '--no-verify'], { cwd });
            return true;
          } catch {
            return false;
          }
        },
      });

      log.info('Auto-fix agent completed', {
        issueNumber: ctx.issue.number,
        success: result.success,
        textLength: result.text?.length ?? 0,
      });
    } catch (err) {
      log.error('Auto-fix agent failed', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
}
