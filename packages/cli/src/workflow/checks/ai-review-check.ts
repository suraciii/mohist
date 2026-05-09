import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';
import { Stage } from '../../types';
import { parseVerdict, extractFixSuggestions, readReportFile } from '../utils';
import { Log } from '../../util/log';
import { buildAutoFixPrompt, buildReVerifyPrompt } from '../../agents/artifact-prompt';

const log = Log.create({ service: 'ai-review-check' });

export interface AiReviewCheckOptions {
  reviewOutputPath?: string;
  selfCheckOutputPath?: string;
}

export class AiReviewCheck implements Check {
  public readonly name = 'ai-review';
  public readonly reaction: ReactionConfig = {
    type: 'auto-fix',
    maxAttempts: 1,
    fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build },
  };
  private reviewOutputPath: string;

  constructor(options?: AiReviewCheckOptions) {
    this.reviewOutputPath = options?.reviewOutputPath ?? 'review.md';
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const reviewReport = readReportFile(ctx.changeDir, this.reviewOutputPath);

    if (!reviewReport) {
      return {
        name: this.name,
        status: 'error',
        message: 'review.md not found — review round may not have completed',
      };
    }

    const verdict = parseVerdict(reviewReport);
    if (verdict === null) {
      log.warn('AiReviewCheck could not parse verdict from review report', {
        issueNumber: ctx.issue.number,
      });
      return {
        name: this.name,
        status: 'error',
        message: 'Could not parse verdict from review report',
      };
    }

    const fixSuggestions = verdict === 'FAIL' ? extractFixSuggestions(reviewReport) : '';

    return {
      name: this.name,
      status: verdict === 'PASS' ? 'pass' : 'fail',
      message: verdict === 'PASS' ? 'AI review passed' : 'AI review failed',
      output: {
        verdict,
        reviewReport,
        fixSuggestions,
      },
    };
  }

  async fix(ctx: CheckContext): Promise<void> {
    const reviewReport = readReportFile(ctx.changeDir, this.reviewOutputPath);
    if (!reviewReport) {
      log.warn('AiReviewCheck auto-fix skipped: review report missing', {
        issueNumber: ctx.issue.number,
      });
      return;
    }

    try {
      const { withSession } = await import('../../agent-runtime/agent-session');
      const { createWorkflowSessionObservers } = await import('../../agent-runtime');
      const autoFixPrompt = buildAutoFixPrompt(ctx.issue, ctx.changeDir, reviewReport, this.reviewOutputPath);

      log.info('AiReviewCheck auto-fix: spawning coder agent', {
        issueNumber: ctx.issue.number,
      });

      const autoFixObservers = createWorkflowSessionObservers({
        eventBus: ctx.eventBus,
        workflowLogRepo: ctx.workflowLogRepo,
        sessionStreamLogRepo: ctx.sessionStreamLogRepo,
        coderSessionRepo: ctx.coderSessionRepo,
        stage: 'check',
        taskDescription: autoFixPrompt,
        title: 'Auto-fix: review findings',
      });

      await withSession({
        cwd: ctx.acpOptions.cwd,
        task: autoFixPrompt,
        taskId: `review-auto-fix-${ctx.issue.number}`,
        issueId: ctx.issue.id,
        projectId: ctx.projectId,
        issueNumber: ctx.issue.number,
        opencodeBinPath: ctx.acpOptions?.opencodeBinPath,
        model: ctx.acpOptions?.model,
        stage: 'check',
        timeout: 10 * 60 * 1000,
        title: 'Auto-fix: review findings',
        observers: autoFixObservers,
      });

      const reVerifyPrompt = buildReVerifyPrompt(ctx.issue, ctx.changeDir, reviewReport);

      const reVerifyObservers = createWorkflowSessionObservers({
        eventBus: ctx.eventBus,
        workflowLogRepo: ctx.workflowLogRepo,
        sessionStreamLogRepo: ctx.sessionStreamLogRepo,
        coderSessionRepo: ctx.coderSessionRepo,
        stage: 'check',
        taskDescription: reVerifyPrompt,
        title: 'Re-review after auto-fix',
      });

      const reVerifyResult = await withSession({
        cwd: ctx.acpOptions.cwd,
        task: reVerifyPrompt,
        taskId: `review-reverify-${ctx.issue.number}`,
        issueId: ctx.issue.id,
        projectId: ctx.projectId,
        issueNumber: ctx.issue.number,
        opencodeBinPath: ctx.acpOptions?.opencodeBinPath,
        model: ctx.acpOptions?.model,
        stage: 'check',
        timeout: 10 * 60 * 1000,
        title: 'Re-review after auto-fix',
        observers: reVerifyObservers,
      });

      log.info('AiReviewCheck auto-fix completed', {
        issueNumber: ctx.issue.number,
        success: reVerifyResult.success,
        textLength: reVerifyResult.text?.length ?? 0,
      });
    } catch (err) {
      log.error('AiReviewCheck auto-fix failed', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
}
