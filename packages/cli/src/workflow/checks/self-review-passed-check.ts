import type { Check, CheckContext, CheckResult } from './index';
import { parseStructuredResult, buildStructuredResult, isParseError } from '../result-contracts';
import { SELF_REVIEW_RESULT_CONTRACT } from '../domain';
import { parseDimensions, readReportFile } from '../utils';

export class SelfReviewPassedCheck implements Check {
  public readonly name = 'self-review-passed';

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.changeDir) {
      return { name: this.name, status: 'error', message: 'No change directory' };
    }

    const report = readReportFile(ctx.changeDir, 'self-review.md');
    const sourceContent = report ?? null;
    const parsed = parseStructuredResult(SELF_REVIEW_RESULT_CONTRACT, sourceContent);

    if (isParseError(parsed)) {
      const message = describeParseError(parsed);
      return { name: this.name, status: 'error', message };
    }

    const structured = buildStructuredResult(parsed);
    const dimensions = parseDimensions(report!);

    if (parsed.verdict === 'PASS') {
      return {
        name: this.name,
        status: 'pass',
        message: 'Self-review passed',
        output: {
          verdict: parsed.verdict,
          selfReviewNotes: report,
          dimensions,
          structuredResult: structured,
        },
      };
    }

    return {
      name: this.name,
      status: 'fail',
      message: 'Self-review verdict: FAIL',
      output: {
        verdict: parsed.verdict,
        selfReviewNotes: report,
        dimensions,
        structuredResult: structured,
      },
    };
  }
}

function describeParseError(err: import('../result-contracts').ParseError): string {
  switch (err.error) {
    case 'source-missing':
      return `${err.source} not found or empty`;
    case 'no-marker':
      return `No valid promise marker found in ${err.source} — self-review task may have failed to produce valid artifact`;
    case 'duplicate-markers':
      return `Multiple promise markers found in ${err.source} — self-review task produced ambiguous output`;
    case 'malformed-marker':
      return `Malformed promise marker in ${err.source}: ${err.raw}`;
    case 'source-unavailable':
      return `Output source ${err.source} unavailable${err.cause ? `: ${err.cause}` : ''}`;
  }
}
