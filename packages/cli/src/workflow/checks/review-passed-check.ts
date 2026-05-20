import type { Check, CheckContext, CheckResult } from './index';
import { parseStructuredResult, buildStructuredResult, isParseError } from '../result-contracts';
import { REVIEW_RESULT_CONTRACT } from './review-result-contracts';
import { extractFixSuggestions, readReportFile } from '../utils';
import { extractRepairResultFromArtifact } from '../task-runtime/self-repair';
import { Log } from '../../util/log';
import type { ResultContract } from '../../types/workflow-results';

const log = Log.create({ service: 'review-passed-check' });

export interface ReviewPassedCheckOptions {
  reviewOutputPath?: string;
}

function makeContract(artifactPath: string): ResultContract {
  return {
    ...REVIEW_RESULT_CONTRACT,
    outputSource: { type: 'artifact', path: artifactPath },
  };
}

export class ReviewPassedCheck implements Check {
  public readonly name = 'review-passed';
  private reviewOutputPath: string;
  private contract: ResultContract;

  constructor(options?: ReviewPassedCheckOptions) {
    this.reviewOutputPath = options?.reviewOutputPath ?? 'review.md';
    this.contract = makeContract(this.reviewOutputPath);
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const reviewReport = readReportFile(ctx.changeDir, this.reviewOutputPath);
    const sourceContent = reviewReport ?? null;
    const parsed = parseStructuredResult(this.contract, sourceContent);

    if (isParseError(parsed)) {
      const message = this.describeParseError(parsed);
      log.error('ReviewPassedCheck: structured result parse error', {
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
    const snapshotSha = await this.getCandidateHeadSha(ctx);
    const structured = buildStructuredResult(parsed);

    const repairResult = extractRepairResultFromArtifact(this.contract, sourceContent);

    const repairedItemIds = repairResult.repairedItemIds.length > 0
      ? repairResult.repairedItemIds
      : structured.repairedItemIds;

    const finalStructured = {
      ...structured,
      ...(repairedItemIds && repairedItemIds.length > 0 ? { repairedItemIds } : {}),
      ...(repairResult.verification.length > 0 ? { verification: repairResult.verification } : {}),
    };

    return {
      name: this.name,
      status: parsed.verdict === 'PASS' ? 'pass' : 'fail',
      message: parsed.verdict === 'PASS' ? 'Review passed' : 'Review failed',
      output: {
        verdict: parsed.verdict,
        reviewReport,
        fixSuggestions,
        ...(snapshotSha ? { snapshotSha } : {}),
        structuredResult: finalStructured,
      },
    };
  }

  private describeParseError(err: import('../result-contracts').ParseError): string {
    switch (err.error) {
      case 'source-missing':
        return `${err.source} not found — ai-review task may have failed`;
      case 'no-marker':
        return `No valid promise marker found in ${err.source} — ai-review task may have failed to produce valid artifact`;
      case 'duplicate-markers':
        return `Multiple promise markers found in ${err.source} — ai-review task produced ambiguous output`;
      case 'malformed-marker':
        return `Malformed promise marker in ${err.source}: ${err.raw}`;
      case 'source-unavailable':
        return `Output source ${err.source} unavailable${err.cause ? `: ${err.cause}` : ''}`;
    }
  }

  private async getCandidateHeadSha(ctx: CheckContext): Promise<string | null> {
    try {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project && ctx.worktreeManager?.getPath(project.name, ctx.issue.number);
      if (!worktreePath) return null;
      if (ctx.worktreeManager?.isWorktreeClean) {
        const clean = await ctx.worktreeManager.isWorktreeClean(worktreePath);
        if (!clean) return null;
      }
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
