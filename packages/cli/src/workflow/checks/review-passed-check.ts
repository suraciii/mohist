import type { Check, CheckContext, CheckResult } from './index';
import { parseVerdict, extractFixSuggestions, readReportFile } from '../utils';
import { Log } from '../../util/log';

const log = Log.create({ service: 'review-passed-check' });

export interface ReviewPassedCheckOptions {
  reviewOutputPath?: string;
}

export class ReviewPassedCheck implements Check {
  public readonly name = 'review-passed';
  private reviewOutputPath: string;

  constructor(options?: ReviewPassedCheckOptions) {
    this.reviewOutputPath = options?.reviewOutputPath ?? 'review.md';
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const reviewReport = readReportFile(ctx.changeDir, this.reviewOutputPath);

    if (!reviewReport) {
      return {
        name: this.name,
        status: 'error',
        message: 'review.md not found — ai-review task may have failed',
      };
    }

    const verdict = parseVerdict(reviewReport);
    if (verdict === null) {
      log.error('ReviewPassedCheck could not parse verdict — artifact should have been validated by ai-review', {
        issueNumber: ctx.issue.number,
      });
      return {
        name: this.name,
        status: 'error',
        message: 'Could not parse verdict — ai-review task may have failed to produce valid artifact',
      };
    }

    const fixSuggestions = verdict === 'FAIL' ? extractFixSuggestions(reviewReport) : '';
    const snapshotSha = await this.getCandidateHeadSha(ctx);

    return {
      name: this.name,
      status: verdict === 'PASS' ? 'pass' : 'fail',
      message: verdict === 'PASS' ? 'Review passed' : 'Review failed',
      output: {
        verdict,
        reviewReport,
        fixSuggestions,
        ...(snapshotSha ? { snapshotSha } : {}),
      },
    };
  }

  private async getCandidateHeadSha(ctx: CheckContext): Promise<string | null> {
    try {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project && ctx.worktreeManager?.getPath(project.name, ctx.issue.number);
      if (!worktreePath) return null;
      return await ctx.worktreeManager!.getHeadSha(worktreePath);
    } catch (err) {
      log.warn('Failed to resolve review snapshot SHA', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      return null;
    }
  }
}
