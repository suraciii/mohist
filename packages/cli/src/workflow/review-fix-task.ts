import type { CheckResult, StageContext, StageTaskResult } from './stage-context';
import { emitStageTaskUpdate } from './stage-context';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { Log } from '../util/log';

const log = Log.create({ service: 'review-fix-task' });

export interface ReviewFixTaskOptions {
  worktreePath: string;
  failedCheck: CheckResult;
  attempt: number;
}

function buildReviewFixPrompt(ctx: StageContext, options: ReviewFixTaskOptions): string {
  const output = options.failedCheck.output as { verdict?: string; reviewReport?: string; fixSuggestions?: string } | undefined;
  const fixSuggestions = output?.fixSuggestions ?? '';
  const reviewReport = output?.reviewReport ?? '';
  const trimmedReport = reviewReport.length > 12000 ? reviewReport.slice(-12000) : reviewReport;
  const trimmedSuggestions = fixSuggestions.length > 8000 ? fixSuggestions.slice(-8000) : fixSuggestions;

  return [
    `## Review Fix Required`,
    '',
    `Issue #${ctx.issue.number}: ${ctx.issue.title}`,
    `Failed check: ${options.failedCheck.name}`,
    '',
    `## Review Report`,
    '',
    trimmedReport,
    '',
    `## Fix Suggestions`,
    '',
    trimmedSuggestions || 'No structured fix suggestions found. Read the review report carefully and address all FAIL items.',
    '',
    `## Instructions`,
    '',
    `1. Read the review report and fix suggestions carefully.`,
    `2. Apply the minimal code or artifact changes required to resolve every FAIL item.`,
    `3. Do not make unrelated refactors.`,
    `4. Do not modify review.md or review-self-check.md.`,
  ].join('\n');
}

export async function runReviewFixTask(
  ctx: StageContext,
  options: ReviewFixTaskOptions,
): Promise<StageTaskResult> {
  const startedAt = Date.now();
  const taskId = 'fix-review-findings';
  const title = 'Fix review findings';
  const stage = 'check';
  const attempt = options.attempt;

  emitStageTaskUpdate(
    ctx.eventBus,
    ctx.issue.id,
    ctx.issue.projectId,
    stage,
    taskId,
    title,
    'started',
    attempt,
    [],
  );

  const observers = createWorkflowSessionObservers({
    eventBus: ctx.eventBus,
    workflowLogRepo: ctx.workflowLogRepo,
    sessionStreamLogRepo: ctx.sessionStreamLogRepo,
    coderSessionRepo: ctx.coderSessionRepo,
    stage,
    title,
  });

  const acpOptions: AgentSessionOptions = {
    ...ctx.acpOptions,
    cwd: options.worktreePath,
    issueId: ctx.issue.id,
    projectId: ctx.issue.projectId,
    issueNumber: ctx.issue.number,
    executionId: `check-${ctx.issue.number}-${taskId}-${attempt}`,
    stage,
    title,
    observers,
  };

  let session: AgentSession | undefined;
  try {
    session = await AgentSession.create(acpOptions);
    const result = await session.execute(buildReviewFixPrompt(ctx, options), {
      kind: 'recovery',
      title,
    });
    const duration = Date.now() - startedAt;
    const status = result.success ? 'completed' : 'failed';

    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      stage,
      taskId,
      title,
      result.success ? 'completed' : 'failed',
      attempt,
      [],
    );

    return {
      taskId,
      title,
      status,
      artifacts: [],
      attempts: attempt,
      duration,
      output: {
        kind: 'review-fix-task',
        checkName: options.failedCheck.name,
        attempt,
        success: result.success,
        error: result.error,
        acpSessionId: result.acpSessionId,
        summary: result.success
          ? `${title} completed; re-running ${options.failedCheck.name}`
          : `${title} failed: ${result.error ?? 'unknown error'}`,
      },
    };
  } catch (err) {
    const duration = Date.now() - startedAt;
    const error = err instanceof Error ? err.message : String(err);
    log.warn('Review fix task failed', {
      issueNumber: ctx.issue.number,
      taskId,
      error,
    });
    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      stage,
      taskId,
      title,
      'failed',
      attempt,
      [],
    );
    return {
      taskId,
      title,
      status: 'failed',
      artifacts: [],
      attempts: attempt,
      duration,
      output: {
        kind: 'review-fix-task',
        checkName: options.failedCheck.name,
        attempt,
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
