import type { Check, CheckContext, CheckResult } from './index';
import { AcpRoundRunner, type AcpRoundRunnerOptions, type RoundConfig } from '../acp-round-runner';
import { buildReviewerPrompt, buildReviewSelfCheckPrompt } from '../../agents/artifact-prompt';
import { parseVerdict, extractFixSuggestions, readReportFile } from '../utils';
import { Log } from '../../util/log';

const log = Log.create({ service: 'ai-review-check' });

export interface AiReviewCheckOptions {
  reviewOutputPath?: string;
  selfCheckOutputPath?: string;
}

export class AiReviewCheck implements Check {
  public readonly name = 'ai-review';
  private reviewOutputPath: string;
  private selfCheckOutputPath: string;

  constructor(options?: AiReviewCheckOptions) {
    this.reviewOutputPath = options?.reviewOutputPath ?? 'review.md';
    this.selfCheckOutputPath = options?.selfCheckOutputPath ?? 'review-self-check.md';
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const changeDir = ctx.changeDir;

    const rounds: RoundConfig[] = [
      {
        type: 'review',
        label: 'review',
        outputPath: changeDir + '/' + this.reviewOutputPath,
        verifyArtifact: () => readReportFile(changeDir, this.reviewOutputPath) !== null,
        buildPrompt: (issue, dir) => buildReviewerPrompt(issue, dir),
      },
      {
        type: 'review-self-check',
        label: 'review-self-check',
        outputPath: changeDir + '/' + this.selfCheckOutputPath,
        verifyArtifact: () => readReportFile(changeDir, this.selfCheckOutputPath) !== null,
        buildPrompt: (issue, dir) => buildReviewSelfCheckPrompt(issue, dir),
      },
    ];

    const runnerOptions: AcpRoundRunnerOptions = {
      issue: ctx.issue,
      changeDir,
      rounds,
      acpOptions: {
        ...ctx.acpOptions,
        executionId: `review-${ctx.issue.number}`,
      },
      stage: 'review',
      projectId: ctx.projectId,
      eventBus: ctx.eventBus,
    };

    const runner = new AcpRoundRunner(runnerOptions);
    const result = await runner.execute();

    if (!result.success) {
      return {
        name: this.name,
        status: 'error',
        message: result.message ?? 'Review round execution failed',
      };
    }

    const reviewReport = readReportFile(changeDir, this.reviewOutputPath);

    if (!reviewReport) {
      return {
        name: this.name,
        status: 'error',
        message: 'review.md not found after review round',
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