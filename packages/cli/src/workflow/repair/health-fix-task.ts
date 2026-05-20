import type { CheckResult, StageContext, StageTaskResult } from '../stage-context';
import { emitStageTaskUpdate } from '../stage-context';
import { AgentSession, type AgentSessionOptions } from '../../agent-runtime/agent-session';
import { createWorkflowSessionObservers } from '../../agent-runtime';
import { formatAgentPrompt } from '../../agents/agent-prompt-schema';
import { formatIssueInfo, listOpenSpecContextFiles } from '../../agents/workflow-context';
import { loadAgentConfig } from '../../agents/agent-config';
import { Log } from '../../util/log';

const log = Log.create({ service: 'health-fix-task' });

export interface HealthFixTaskOptions {
  taskId: 'fix-plan-health' | 'fix-build-health' | 'fix-check-health' | 'fix-integrate-health';
  title: string;
  stage: 'plan' | 'build' | 'check' | 'integrate';
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

function buildHealthFixPrompt(ctx: StageContext, options: HealthFixTaskOptions, changeDir: string | null): string {
  const checkOutput = stringifyOutput(options.failedCheck.output);
  const trimmedOutput = checkOutput.length > 12000 ? checkOutput.slice(-12000) : checkOutput;

  const task = [
    `Change Directory: ${changeDir ?? options.worktreePath}`,
    '',
    formatIssueInfo(ctx.issue),
    '',
    `Stage: ${options.stage}`,
    `Failed check: ${options.failedCheck.name}`,
    `Health command: ${options.healthCommand}`,
    '',
    'Failure Summary:',
    options.failedCheck.message ?? 'Health gate failed.',
    '',
    'Check Output:',
    '```json',
    trimmedOutput,
    '```',
  ].join('\n');

  return formatAgentPrompt({
    role: `Fix ${options.stage} health gate failure`,
    projectContext: loadAgentConfig(options.worktreePath).context,
    contextFiles: listOpenSpecContextFiles(changeDir, { includeReports: true, includeSessionMemories: true }),
    task,
    contract: 'Apply the minimal code or artifact changes required to make the health command pass. Do not make unrelated refactors.',
    instruction: [
      '1. Read the issue and every @file context reference before editing.',
      '2. Read the failed health gate output carefully.',
      '3. Apply the minimal fix required to make the health command pass.',
      '4. Run the health command to verify your fix.',
      '5. Commit your fix with a descriptive message if you changed tracked files.',
    ].join('\n'),
  });
}

export async function runHealthFixTask(
  ctx: StageContext,
  options: HealthFixTaskOptions,
): Promise<StageTaskResult> {
  const startedAt = Date.now();
  const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
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
    const result = await session.execute(buildHealthFixPrompt(ctx, options, changeDir), {
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
      reason: `${options.title} triggered by failed check: ${options.failedCheck.name}`,
      causedBy: {
        type: 'check-failure',
        checkName: options.failedCheck.name,
        message: options.failedCheck.message,
      },
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
      reason: `${options.title} triggered by failed check: ${options.failedCheck.name}`,
      causedBy: {
        type: 'check-failure',
        checkName: options.failedCheck.name,
        message: error,
      },
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
