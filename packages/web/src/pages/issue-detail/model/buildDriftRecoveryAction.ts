import type { DriftRecoveryAction, RebaseRecovery } from '../../../widgets/issue-workflow'

export interface BuildDriftRecoveryActionInput {
  drift: {
    drifted: boolean
    decision: 'skip' | 'suggest' | 'enqueue' | 'defer' | 'needs-attention' | null
  } | null | undefined
  rebase: RebaseRecovery
  baseBranchFallback?: string | null
}

export function buildDriftRecoveryAction(
  input: BuildDriftRecoveryActionInput,
): DriftRecoveryAction | null {
  const { drift, rebase, baseBranchFallback } = input
  if (!drift?.drifted) return null
  if (drift.decision !== 'needs-attention') return null

  return {
    baseBranch: rebase.workspace.baseBranch ?? baseBranchFallback ?? 'master',
    branch: rebase.workspace.branch,
    trigger: rebase.trigger,
    isPending: rebase.isPending,
    isQueued: rebase.isQueued,
    isRebasing: rebase.isRebasing,
    isConflictResolving: rebase.isConflictResolving,
    isConflictFailed: rebase.isConflictFailed,
    canRequest: rebase.canRequest,
    hasConflicts: rebase.hasConflicts,
    error: rebase.error,
  }
}
