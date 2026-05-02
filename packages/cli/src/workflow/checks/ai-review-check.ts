import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';
import { Stage } from '../../types';
import { parseVerdict, extractFixSuggestions, readReportFile } from '../utils';
import { Log } from '../../util/log';

const log = Log.create({ service: 'ai-review-check' });

export interface AiReviewCheckOptions {
  reviewOutputPath?: string;
  selfCheckOutputPath?: string;
}

export class AiReviewCheck implements Check {
  public readonly name = 'ai-review';
  public readonly reaction: ReactionConfig = {
    type: 'escalate',
    escalateTarget: Stage.Plan,
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
}
