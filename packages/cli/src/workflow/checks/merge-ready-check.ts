import type { Check, CheckContext, CheckResult } from './index';

export class MergeReadyCheck implements Check {
  public readonly name = 'merge-ready';

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.worktreeManager) {
      return {
        name: this.name,
        status: 'error',
        message: 'worktreeManager not available in CheckContext',
      };
    }
    if (!ctx.projectRepo) {
      return {
        name: this.name,
        status: 'error',
        message: 'projectRepo not available in CheckContext',
      };
    }

    const project = ctx.projectRepo.findById(ctx.issue.projectId);
    if (!project) {
      return {
        name: this.name,
        status: 'error',
        message: `Project not found: ${ctx.issue.projectId}`,
      };
    }

    try {
      const canFastForward = await ctx.worktreeManager.canFastForward(
        project.path,
        project.name,
        ctx.issue.number,
        project.baseBranch
      );

      const worktreeStatus = await ctx.worktreeManager.getWorktreeStatus(
        project.path,
        project.name,
        ctx.issue.number
      );

      const canMerge = canFastForward || worktreeStatus.canFastForward ||
        (!worktreeStatus.conflictingFiles || worktreeStatus.conflictingFiles.length === 0);
      const status = canMerge ? 'pass' : 'fail';

      return {
        name: this.name,
        status,
        message: status === 'pass'
          ? 'Merge ready'
          : 'Merge not ready — candidate cannot be cleanly merged',
        output: {
          kind: 'merge-ready',
          targetBranch: project.baseBranch,
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
        message: `Merge ready check error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }
}