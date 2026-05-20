import type { CheckResult, StageContext, StageTaskResult } from './stage-context';
import { emitStageTaskUpdate } from './stage-context';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import { createWorkflowSessionObservers } from '../agent-runtime';
import { formatAgentPrompt } from '../agents/agent-prompt-schema';
import { formatIssueInfo, listOpenSpecContextFiles } from '../agents/workflow-context';
import { loadAgentConfig } from '../agents/agent-config';
import { buildFailedCheckContext } from './reaction-context';
import type { FailedCheckContext } from '../types/workflow-results';
import { Log } from '../util/log';

const log = Log.create({ service: 'review-fix-task' });

export interface ReviewFixTaskOptions {
  worktreePath: string;
  failedCheck: CheckResult;
  attempt: number;
}

function buildStructuredItemsSection(ctx: FailedCheckContext): string {
  if (ctx.blockingItems.length === 0) return '';

  const itemsSection = ctx.blockingItems.map(item => {
    const parts = [
      `- [ID: ${item.id}]`,
      `  Severity: ${item.severity}`,
      item.scope ? `  Scope: ${item.scope}` : '',
      `  Evidence: ${item.evidence}`,
      item.suggestedAction ? `  SuggestedAction: ${item.suggestedAction}` : '',
      item.verification ? `  Verification: ${item.verification}` : '',
    ];
    return parts.filter(Boolean).join('\n');
  }).join('\n\n');

  const nonBlockingSection = ctx.nonBlockingItems.length > 0
    ? '\n\nNon-blocking / Follow-up Items (do NOT fix these unless they directly overlap with a blocking item):\n' +
      ctx.nonBlockingItems.map(item =>
        `- [ID: ${item.id}] Severity: ${item.severity} Status: ${item.status ?? 'open'} — ${item.evidence}`
      ).join('\n')
    : '';

  const snapshotSection = ctx.snapshot?.sha
    ? `\n\nCandidate Snapshot SHA: ${ctx.snapshot.sha}`
    : '';

  return [
    `Blocking Items (${ctx.blockingItems.length}):`,
    'You MUST resolve ALL of these items:',
    '',
    itemsSection,
    nonBlockingSection,
    snapshotSection,
  ].join('\n');
}

function buildReviewFixPrompt(ctx: StageContext, options: ReviewFixTaskOptions, changeDir: string | null): string {
  const failedCheckContext = buildFailedCheckContext(options.failedCheck);
  const structuredItemsBlock = failedCheckContext.blockingItems.length > 0
    ? buildStructuredItemsSection(failedCheckContext)
    : null;

  const task = [
    `Change Directory: ${changeDir ?? options.worktreePath}`,
    '',
    formatIssueInfo(ctx.issue),
    '',
    `Failed check: ${options.failedCheck.name}`,
    '',
  ];

  if (structuredItemsBlock) {
    task.push(
      structuredItemsBlock,
      '',
    );
  } else {
    const output = options.failedCheck.output as { verdict?: string; reviewReport?: string; fixSuggestions?: string } | undefined;
    const fixSuggestions = output?.fixSuggestions ?? '';
    const reviewReport = output?.reviewReport ?? '';
    const trimmedReport = reviewReport.length > 12000 ? reviewReport.slice(-12000) : reviewReport;
    const trimmedSuggestions = fixSuggestions.length > 8000 ? fixSuggestions.slice(-8000) : fixSuggestions;

    task.push(
      'Review Report:',
      trimmedReport,
      '',
      'Fix Suggestions:',
      trimmedSuggestions || 'No structured fix suggestions found. Read the review report carefully and address all FAIL items.',
      '',
    );
  }

  return formatAgentPrompt({
    role: 'Fix review findings for this issue',
    projectContext: loadAgentConfig(options.worktreePath).context,
    contextFiles: listOpenSpecContextFiles(changeDir, { includeReports: true, includeSessionMemories: true }),
    task: task.join('\n'),
    contract: 'Apply the minimal code or artifact changes required to resolve every listed blocking item. Do not modify review.md or review-self-check.md. Report which item IDs you attempted, resolved, and left unresolved.',
    instruction: [
      '1. Read the issue and every @file context reference before editing.',
      '2. Read the blocking items carefully. You must address ALL listed blocking items.',
      '3. Apply only the minimal changes required to resolve every blocking item.',
      '4. Do not make unrelated refactors.',
      '5. Add or update focused tests when the fix changes behavior.',
      '6. Non-blocking follow-up items should be left for later unless they directly overlap with a blocking item.',
    ].join('\n'),
  });
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
  const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);

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
    const result = await session.execute(buildReviewFixPrompt(ctx, options, changeDir), {
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
      reason: `${title} triggered by failed check: ${options.failedCheck.name}`,
      causedBy: {
        type: 'check-failure',
        checkName: options.failedCheck.name,
        message: (options.failedCheck.output as { verdict?: string })?.verdict === 'FAIL'
          ? `AI review returned FAIL verdict`
          : undefined,
      },
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
      reason: `${title} triggered by failed check: ${options.failedCheck.name}`,
      causedBy: {
        type: 'check-failure',
        checkName: options.failedCheck.name,
        message: error,
      },
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
