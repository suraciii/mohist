import type { CheckResult, StageContext, StageTaskResult } from './stage-context';
import { emitStageTaskUpdate } from './stage-context';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { Log } from '../util/log';

const log = Log.create({ service: 'health-fix-task' });

export interface HealthFixTaskOptions {
  taskId: 'fix-plan-health' | 'fix-build-health' | 'fix-check-health';
  title: string;
  stage: 'plan' | 'build' | 'check';
  worktreePath: string;
  healthCommand: string;
  failedCheck: CheckResult;
  attempt: number;
}

function stringifyOutput(output: unknown): string {
  if (!output) return '';
  if (typeof output === 'string') return output;
  try {
    return JSON.stringify(output, null, 2);
  } catch {
    return String(output);
  }
}

function buildHealthFixPrompt(ctx: StageContext, options: HealthFixTaskOptions): string {
  const checkOutput = stringifyOutput(options.failedCheck.output);
  const trimmedOutput = checkOutput.length > 12000 ? checkOutput.slice(-12000) : checkOutput;

  return [
    `## Health Gate Fix Required`,
    '',
    `Issue #${ctx.issue.number}: ${ctx.issue.title}`,
    `Stage: ${options.stage}`,
    `Failed check: ${options.failedCheck.name}`,
    `Health command: ${options.healthCommand}`,
    '',
    `## Failure Summary`,
    '',
    options.failedCheck.message ?? 'Health gate failed.',
    '',
    `## Check Output`,
    '',
    '```json',
    trimmedOutput,
    '```',
    '',
    `## Instructions`,
    '',
    `1. Read the failed health gate output carefully.`,
    `2. Apply the minimal code or artifact changes required to make the health command pass.`,
    `3. Do not make unrelated refactors.`,
    `4. Run the health command to verify your fix.`,
    `5. Commit your fix with a descriptive message if you changed tracked files.`,
  ].join('\n');
}

export async function runHealthFixTask(
  ctx: StageContext,
  options: HealthFixTaskOptions,
): Promise<StageTaskResult> {
  const startedAt = Date.now();
  emitStageTaskUpdate(
    ctx.eventBus,
    ctx.issue.id,
    ctx.issue.projectId,
    options.stage,
    options.taskId,
    options.title,
    'started',
    options.attempt,
    [],
  );

  const observers = createWorkflowSessionObservers({
    eventBus: ctx.eventBus,
    workflowLogRepo: ctx.workflowLogRepo,
    sessionStreamLogRepo: ctx.sessionStreamLogRepo,
    coderSessionRepo: ctx.coderSessionRepo,
    stage: options.stage,
    title: options.title,
  });

  const acpOptions: AgentSessionOptions = {
    ...ctx.acpOptions,
    cwd: options.worktreePath,
    issueId: ctx.issue.id,
    projectId: ctx.issue.projectId,
    issueNumber: ctx.issue.number,
    executionId: `${options.stage}-${ctx.issue.number}-${options.taskId}-${options.attempt}`,
    stage: options.stage,
    title: options.title,
    observers,
  };

  let session: AgentSession | undefined;
  try {
    session = await AgentSession.create(acpOptions);
    const result = await session.execute(buildHealthFixPrompt(ctx, options), {
      kind: 'recovery',
      title: options.title,
    });
    const duration = Date.now() - startedAt;
    const status = result.success ? 'completed' : 'failed';

    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      options.stage,
      options.taskId,
      options.title,
      result.success ? 'completed' : 'failed',
      options.attempt,
      [],
    );

    return {
      taskId: options.taskId,
      title: options.title,
      status,
      artifacts: [],
      attempts: options.attempt,
      duration,
      output: {
        kind: 'health-fix-task',
        stage: options.stage,
        checkName: options.failedCheck.name,
        healthCommand: options.healthCommand,
        attempt: options.attempt,
        success: result.success,
        error: result.error,
        acpSessionId: result.acpSessionId,
        summary: result.success
          ? `${options.title} completed; re-running ${options.failedCheck.name}`
          : `${options.title} failed: ${result.error ?? 'unknown error'}`,
      },
    };
  } catch (err) {
    const duration = Date.now() - startedAt;
    const error = err instanceof Error ? err.message : String(err);
    log.warn('Health fix task failed', {
      issueNumber: ctx.issue.number,
      taskId: options.taskId,
      error,
    });
    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      options.stage,
      options.taskId,
      options.title,
      'failed',
      options.attempt,
      [],
    );
    return {
      taskId: options.taskId,
      title: options.title,
      status: 'failed',
      artifacts: [],
      attempts: options.attempt,
      duration,
      output: {
        kind: 'health-fix-task',
        stage: options.stage,
        checkName: options.failedCheck.name,
        healthCommand: options.healthCommand,
        attempt: options.attempt,
        success: false,
        error,
      },
    };
  } finally {
    if (session) {
      await session.close().catch(() => {});
    }
  }
}
