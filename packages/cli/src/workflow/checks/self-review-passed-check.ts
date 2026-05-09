import type { Check, CheckContext, CheckResult } from './index';
import { parseVerdict, readReportFile } from '../utils';

export class SelfReviewPassedCheck implements Check {
  public readonly name = 'self-review-passed';

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.changeDir) {
      return { name: this.name, status: 'fail', message: 'No change directory' };
    }
    const report = readReportFile(ctx.changeDir, 'self-review.md');
    if (!report) {
      return { name: this.name, status: 'fail', message: 'self-review.md not found or empty' };
    }
    const verdict = parseVerdict(report);
    if (verdict === null) {
      return { name: this.name, status: 'error', message: 'Could not parse verdict from self-review.md' };
    }
    if (verdict === 'PASS') {
      return { name: this.name, status: 'pass', message: 'Self-review passed' };
    }
    return { name: this.name, status: 'fail', message: 'Self-review verdict: FAIL' };
  }
}
