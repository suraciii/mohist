import { Log } from '../../../../util/log';
import { extractFixSuggestions, parseDimensions } from '../../../utils';
import type { CheckContext }  from '@mohist/workflow/checks';
import { registerMarkerFormat }  from '@mohist/workflow/checks/marker-format-registry';
import { enrichReviewStructuredResult } from './review-contracts';
import { extractStructuredResultMetadata } from './review-metadata';

const log = Log.create({ service: 'mohist-default-marker-formats' });

export function registerMohistDefaultMarkerFormats(): void {
  registerMarkerFormat('mohist/self-review', {
    enrichStructuredResult: enrichReviewStructuredResult,
    enrichOutput: ({ content, output }) => ({
      ...output,
      selfReviewNotes: content,
      dimensions: parseDimensions(content),
    }),
  });

  registerMarkerFormat('mohist/review', {
    enrichStructuredResult: enrichReviewStructuredResult,
    metadata: (contract, content) => {
      const metadata = extractStructuredResultMetadata(contract, content);
      return {
        repairedItemIds: metadata.repairedItemIds,
        verification: metadata.verification,
      };
    },
    enrichOutput: async ({ ctx, content, output }) => {
      const snapshotSha = await getCandidateHeadSha(ctx);
      return {
        ...output,
        reviewReport: content,
        fixSuggestions: output.verdict === 'FAIL' ? extractFixSuggestions(content) : '',
        ...(snapshotSha ? { snapshotSha } : {}),
      };
    },
  });
}

async function getCandidateHeadSha(ctx: CheckContext): Promise<string | null> {
  try {
    const project = ctx.projectRepo?.findById(ctx.issue.projectId);
    const getPath = ctx.worktreeManager?.getPath;
    const worktreePath = project && getPath ? getPath(project.name, ctx.issue.number) : null;
    if (!worktreePath) return null;
    if (ctx.worktreeManager?.isWorktreeClean) {
      const clean = await ctx.worktreeManager.isWorktreeClean(worktreePath);
      if (!clean) return null;
    }
    if (!ctx.worktreeManager?.getHeadSha) return null;
    return await ctx.worktreeManager.getHeadSha(worktreePath);
  } catch (err) {
    log.warn('Failed to resolve marker check snapshot SHA', {
      issueNumber: ctx.issue.number,
      error: err instanceof Error ? err.message : String(err),
    });
    return null;
  }
}
