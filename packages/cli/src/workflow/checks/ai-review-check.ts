import type { Check, CheckContext, CheckResult } from './index';
import { parseStructuredResult, buildStructuredResult, isParseError } from '../result-contracts';
import { REVIEW_RESULT_CONTRACT } from '../domain';
import { extractFixSuggestions, readReportFile } from '../utils';
import { Log } from '../../util/log';
import type { ResultContract } from '../../types/workflow-results';

const log = Log.create({ service: 'ai-review-check' });

export interface AiReviewCheckOptions {
  reviewOutputPath?: string;
}

function makeContract(artifactPath: string): ResultContract {
  return {
    ...REVIEW_RESULT_CONTRACT,
    outputSource: { type: 'artifact', path: artifactPath },
  };
}

export class AiReviewCheck implements Check {
  public readonly name = 'ai-review';
  private reviewOutputPath: string;
  private contract: ResultContract;

  constructor(options?: AiReviewCheckOptions) {
    this.reviewOutputPath = options?.reviewOutputPath ?? 'review.md';
    this.contract = makeContract(this.reviewOutputPath);
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const reviewReport = readReportFile(ctx.changeDir, this.reviewOutputPath);
    const sourceContent = reviewReport ?? null;
    const parsed = parseStructuredResult(this.contract, sourceContent);

    if (isParseError(parsed)) {
      const message = describeParseError(parsed);
      log.warn('AiReviewCheck: structured result parse error', {
        issueNumber: ctx.issue.number,
        error: parsed.error,
        source: parsed.source,
      });
      return {
        name: this.name,
        status: 'error',
        message,
      };
    }

    const fixSuggestions = parsed.verdict === 'FAIL' ? extractFixSuggestions(reviewReport!) : '';
    const structured = buildStructuredResult(parsed);

    return {
      name: this.name,
      status: parsed.verdict === 'PASS' ? 'pass' : 'fail',
      message: parsed.verdict === 'PASS' ? 'AI review passed' : 'AI review failed',
      output: {
        verdict: parsed.verdict,
        reviewReport,
        fixSuggestions,
        structuredResult: structured,
      },
    };
  }
}

function describeParseError(err: import('../result-contracts').ParseError): string {
  switch (err.error) {
    case 'source-missing':
      return `${err.source} not found — review round may not have completed`;
    case 'no-marker':
      return `No valid promise marker found in ${err.source} — review task may have failed to produce valid artifact`;
    case 'duplicate-markers':
      return `Multiple promise markers found in ${err.source} — review task produced ambiguous output`;
    case 'malformed-marker':
      return `Malformed promise marker in ${err.source}: ${err.raw}`;
    case 'source-unavailable':
      return `Output source ${err.source} unavailable${err.cause ? `: ${err.cause}` : ''}`;
  }
}
