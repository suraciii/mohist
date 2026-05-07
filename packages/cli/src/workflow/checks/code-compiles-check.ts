import { execFile } from 'child_process';
import { promisify } from 'util';
import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';
import { Stage } from '../../types';
import { loadWorkflow, loadChecksConfig, DEFAULT_CHECKS_CONFIG } from '../workflow-loader';
import { Log } from '../../util/log';

const execFileAsync = promisify(execFile);
const log = Log.create({ service: 'code-compiles-check' });

const CODE_COMPILES_REACTION: ReactionConfig = {
  type: 'auto-fix',
  maxAttempts: 2,
  fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan },
};

export interface CodeCompilesCheckOptions {
  worktreePath: string;
}

export class CodeCompilesCheck implements Check {
  public readonly name = 'code-compiles';
  public readonly reaction: ReactionConfig = CODE_COMPILES_REACTION;
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

  async fix(ctx: CheckContext): Promise<void> {
    try {
      const { runAcpSession } = await import('../../agent-runtime/acp-session');
      const prompt = [
        '## Task',
        '',
        'Build check failed. Fix the compilation errors so the build passes.',
        '',
        '## Process',
        '',
        '1. Run the build command to see the errors',
        '2. Fix each compilation error',
        '3. Verify the build passes',
        '',
        '## Rules',
        '',
        '- Apply ONLY the minimal fixes needed',
        '- Do NOT refactor or change unrelated code',
      ].join('\n');

      await runAcpSession({
        cwd: this.worktreePath,
        task: prompt,
        taskId: `build-auto-fix-${ctx.issue.number}`,
        issueId: ctx.issue.id,
        projectId: ctx.projectId,
        workflowLogRepo: ctx.acpOptions?.workflowLogRepo,
        eventBus: ctx.eventBus,
        coderSessionRepo: ctx.acpOptions?.coderSessionRepo,
        issueNumber: ctx.issue.number,
        opencodeBinPath: ctx.acpOptions?.opencodeBinPath,
        model: ctx.acpOptions?.model,
        stage: 'build',
        timeout: 10 * 60 * 1000,
        title: 'Auto-fix: compilation errors',
        onBeforeKill: async (cwd: string) => {
          try {
            const { stdout: statusOut } = await execFileAsync('git', ['status', '--porcelain', '--ignore-submodules'], { cwd });
            if (!statusOut.trim()) return false;
            await execFileAsync('git', ['add', '-A'], { cwd });
            const { stdout: remaining } = await execFileAsync('git', ['status', '--porcelain', '--ignore-submodules'], { cwd });
            if (!remaining.trim()) return false;
            await execFileAsync('git', ['commit', '-m', `WIP: build-auto-fix-${ctx.issue.number} timeout`, '--no-verify'], { cwd });
            return true;
          } catch {
            return false;
          }
        },
      });
    } catch (err) {
      log.error('Auto-fix agent failed', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
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
