import type { Check, CheckContext, CheckResult } from './index';
import type { ReactionConfig } from '../stage-context';

export class MergeReadinessCheck implements Check {
  public readonly name = 'merge-readiness';
  public readonly reaction: ReactionConfig = {
    type: 'escalate',
    escalateTarget: undefined,
    fallbackReaction: { type: 'ask-user' },
  };

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.worktreeManager) {
      return {
        name: this.name,
        status: 'error',
        message: 'worktreeManager not available in CheckContext',
      };
    }

    try {
      const canFastForward = await ctx.worktreeManager.canFastForward(
        ctx.acpOptions.cwd,
        ctx.issue.projectId,
        ctx.issue.number,
        'main'
      );

      const worktreeStatus = await ctx.worktreeManager.getWorktreeStatus(
        ctx.acpOptions.cwd,
        ctx.issue.projectId,
        ctx.issue.number
      );

      const status = canFastForward || worktreeStatus.canFastForward ? 'pass' : 'fail';

      return {
        name: this.name,
        status,
        message: status === 'pass'
          ? 'Merge readiness check passed'
          : 'Merge readiness check failed — candidate cannot be cleanly merged',
        output: {
          kind: 'merge-readiness',
          targetBranch: 'main',
          canFastForward: canFastForward || worktreeStatus.canFastForward,
          cleanRebaseFeasible: !worktreeStatus.conflictingFiles || worktreeStatus.conflictingFiles.length === 0,
          conflictFiles: worktreeStatus.conflictingFiles ?? [],
          isRebaseInProgress: worktreeStatus.isRebaseInProgress ?? false,
        },
      };
    } catch (err) {
      return {
        name: this.name,
        status: 'error',
        message: `Merge readiness check error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }
}