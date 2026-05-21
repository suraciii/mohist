import type { Check, CheckContext, CheckResult } from '../../checks';

export class MergeReadinessCheck implements Check {
  public readonly name = 'merge-readiness';

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!ctx.worktreeManager?.checkSquashMergeability) {
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

    const checkSquashMergeability = ctx.worktreeManager.checkSquashMergeability;
    const project = ctx.projectRepo.findById(ctx.issue.projectId);
    if (!project) {
      return {
        name: this.name,
        status: 'error',
        message: `Project not found: ${ctx.issue.projectId}`,
      };
    }

    try {
      const snapshot = await checkSquashMergeability(
        project.path,
        project.name,
        ctx.issue.number,
        project.baseBranch
      );

      const status = snapshot.canMerge ? 'pass' : 'fail';

      return {
        name: this.name,
        status,
        message: status === 'pass'
          ? 'Merge readiness check passed'
          : 'Merge readiness check failed — candidate cannot be cleanly squash-merged',
        output: {
          kind: 'merge-readiness',
          targetBranch: snapshot.targetBranch,
          strategy: snapshot.strategy,
          baseSha: snapshot.baseSha,
          candidateHeadSha: snapshot.candidateHeadSha,
          mergeBaseSha: snapshot.mergeBaseSha,
          canMerge: snapshot.canMerge,
          conflictFiles: snapshot.conflictFiles,
          checkedAt: snapshot.checkedAt,
          error: snapshot.error,
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
