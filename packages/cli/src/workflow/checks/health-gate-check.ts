import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult, ReactionConfig } from './index';
import type { HealthGatePolicy } from '../workflow-loader';
import { Log } from '../../util/log';
import { Stage } from '../../types';

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
}

export class HealthGateCheck implements Check {
  public readonly name: string;
  public readonly reaction: ReactionConfig;
  private worktreePath: string;
  private policy: HealthGatePolicy;
  private stage: string;

  constructor(options: HealthGateCheckOptions) {
    this.worktreePath = options.worktreePath;
    this.policy = options.policy;
    this.stage = options.stage;
    this.name = `health:${this.stage}`;
    this.reaction = {
      type: this.policy.autoFix ? 'auto-fix' : this.policy.fallbackReaction.type,
      maxAttempts: this.policy.maxFixAttempts,
      escalateTarget: this.policy.fallbackReaction.escalateTarget,
      fallbackReaction: this.policy.fallbackReaction.fallbackReaction,
    };
  }

  async run(_ctx: CheckContext): Promise<CheckResult> {
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
        },
      };
    }
  }

  async fix(ctx: CheckContext): Promise<void> {
    let commandOutput = '';
    try {
      await execFileAsync(this.policy.command, [], {
        cwd: this.worktreePath,
        timeout: this.policy.timeout,
        maxBuffer: 10 * 1024 * 1024,
        shell: true,
      });
      log.info('HealthGateCheck auto-fix: command passed unexpectedly', {
        issueNumber: ctx.issue.number,
      });
      return;
    } catch (err: any) {
      commandOutput = [err.stdout, err.stderr, err.message].filter(Boolean).join('\n');
    }

    const truncatedLog = commandOutput.length > 8000
      ? commandOutput.slice(0, 4000) + '\n\n...[truncated]...\n\n' + commandOutput.slice(-4000)
      : commandOutput;

    const prompt = [
      '## Task',
      '',
      `${this.stage} 阶段健康检查失败，请修复代码使健康检查通过。`,
      '',
      `## Health Gate: ${this.stage}`,
      `Command: ${this.policy.command}`,
      '',
      '## Error Output',
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

    log.info('HealthGateCheck auto-fix: spawning coder agent', {
      issueNumber: ctx.issue.number,
      stage: this.stage,
    });

    try {
      const { withSession } = await import('../../agent-runtime/agent-session');
      const { createWorkflowSessionObservers } = await import('../../agent-runtime');
      const stageForSession = this.stage === 'postMerge' ? Stage.Check : this.stage as Stage;

      const fixObservers = createWorkflowSessionObservers({
        eventBus: ctx.eventBus,
        stage: stageForSession,
        title: `Auto-fix: ${this.stage} health gate failure`,
      });

      const result = await withSession({
        cwd: this.worktreePath,
        task: prompt,
        taskId: `health-gate-fix-${ctx.issue.number}`,
        issueId: ctx.issue.id,
        projectId: ctx.projectId,
        issueNumber: ctx.issue.number,
        opencodeBinPath: ctx.acpOptions?.opencodeBinPath,
        model: ctx.acpOptions?.model,
        stage: stageForSession,
        timeout: 10 * 60 * 1000,
        title: `Auto-fix: ${this.stage} health gate failure`,
        observers: fixObservers,
        onBeforeKill: async (cwd: string) => {
          try {
            const { stdout: statusOut } = await execFileAsync('git', ['status', '--porcelain', '--ignore-submodules'], { cwd });
            if (!statusOut.trim()) return false;
            await execFileAsync('git', ['add', '-A'], { cwd });
            const { stdout: remaining } = await execFileAsync('git', ['status', '--porcelain', '--ignore-submodules'], { cwd });
            if (!remaining.trim()) return false;
            await execFileAsync('git', ['commit', '-m', `WIP: health-gate-fix-${ctx.issue.number} timeout`, '--no-verify'], { cwd });
            return true;
          } catch {
            return false;
          }
        },
      });

      log.info('HealthGateCheck auto-fix agent completed', {
        issueNumber: ctx.issue.number,
        success: result.success,
        textLength: result.text?.length ?? 0,
      });
    } catch (err) {
      log.error('HealthGateCheck auto-fix agent failed', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
}