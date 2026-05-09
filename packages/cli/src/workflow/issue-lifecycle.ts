import { Stage, IssueStatus, type Issue, type ApprovalStatus } from '../types';
import { MergeState } from '../types';

export function isCurrentStageApproval(
  issue: Issue,
  stage?: Stage,
  status?: ApprovalStatus,
): boolean {
  const targetStage = stage ?? issue.stage;
  if (issue.approvalState?.stage !== targetStage) {
    return false;
  }
  if (status !== undefined && issue.approvalState?.status !== status) {
    return false;
  }
  return true;
}

export type MergeDeliveryStatus =
  | 'merged'
  | 'queued'
  | 'rebasing'
  | 'merging'
  | 'resolving'
  | 'conflict'
  | 'build-failed'
  | 'blocked'
  | 'not-ready'
  | 'not-merged'
  | 'unknown'
  | 'done-not-merged'
  | 'integrating';

export function classifyMergeDelivery(issue: Issue): MergeDeliveryStatus {
  const { stage, status, mergeState } = issue;

  if (stage === Stage.Done || status === IssueStatus.Completed) {
    if (mergeState === MergeState.Merged) {
      return 'merged';
    }
    return 'done-not-merged';
  }

  if (stage === Stage.Integrate) {
    return 'integrating';
  }

  if (mergeState === null || mergeState === undefined) {
    if (stage === Stage.Draft || stage === Stage.Plan || stage === Stage.Build) {
      return 'not-ready';
    }
    if (stage === Stage.Check) {
      return 'not-ready';
    }
    return 'unknown';
  }

  switch (mergeState) {
    case MergeState.Merged:
      return 'merged';
    case MergeState.Pending:
      return 'queued';
    case MergeState.Rebasing:
      return 'rebasing';
    case MergeState.Merging:
      return 'merging';
    case MergeState.Resolving:
      return 'resolving';
    case MergeState.Conflict:
      return 'conflict';
    case MergeState.BuildFailed:
      return 'build-failed';
    case MergeState.Blocked:
      return 'blocked';
    default:
      return 'unknown';
  }
}
