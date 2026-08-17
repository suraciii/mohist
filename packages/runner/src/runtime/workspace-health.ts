/**
 * Shared internal semantic model for workspace health.
 *
 * Both the `mohist/workspace-prepare` action and `WorkspaceManager`
 * preparation / verification use these snapshot and evaluation types so
 * that detached HEAD, branch mismatch, dirty worktrees, and residual
 * rebase / merge / cherry-pick state are judged identically.
 *
 * This module deliberately owns no Git process adapter. Each consumer
 * keeps its own narrow command runner (the action via `RunnerGitRunner`,
 * the manager via `runCommand`) and shares only the model, the evaluator,
 * and the diagnostic formatting. This keeps the shared surface small and
 * internal while preventing the two paths from disagreeing about what a
 * healthy workspace looks like.
 */

/** Recorded as the observed head ref when HEAD is detached at a commit. */
export const DETACHED_HEAD_REF = '(detached)'

export interface WorkspaceResidualState {
  rebaseMerge: boolean
  rebaseApply: boolean
  mergeHead: boolean
  cherryPickHead: boolean
}

export interface WorkspaceHeadState {
  commit: string
  /** Branch name, or `(detached)` when HEAD is detached at a commit. */
  ref: string
}

export interface WorkspaceProbeFailure {
  /** Step that failed, e.g. `head`, `head-ref`, `residual`, `status`. */
  step: string
  message: string
  exitCode: number | null
}

export interface WorkspaceHealthSnapshot {
  residual: WorkspaceResidualState
  head: WorkspaceHeadState
  porcelain: string
  probeFailure: WorkspaceProbeFailure | null
}

export function isResidualFree(residual: WorkspaceResidualState): boolean {
  return !residual.rebaseMerge && !residual.rebaseApply && !residual.mergeHead && !residual.cherryPickHead
}

export interface WorkspaceHealthEvaluation {
  /** All conditions satisfied: probe ok, aligned, clean, non-residual. */
  healthy: boolean
  /** HEAD attached to the exact expected branch. */
  aligned: boolean
  /** No staged, unstaged, or untracked changes. */
  clean: boolean
  /** No rebase / merge / cherry-pick residual markers. */
  noResidual: boolean
  probeFailure: WorkspaceProbeFailure | null
  /** Human-readable observed condition used in failure diagnostics. */
  condition: string
}

/**
 * Evaluate a health snapshot against the expected run branch. A workspace
 * is healthy only when every probe succeeded, HEAD names exactly the
 * expected branch, the worktree is clean, and no residual operation state
 * remains.
 */
export function evaluateWorkspaceHealth(
  snapshot: WorkspaceHealthSnapshot,
  expectedBranch: string,
): WorkspaceHealthEvaluation {
  const aligned = snapshot.head.ref === expectedBranch
  const clean = snapshot.porcelain.trim() === ''
  const noResidual = isResidualFree(snapshot.residual)
  const healthy = snapshot.probeFailure === null && aligned && clean && noResidual
  return {
    healthy,
    aligned,
    clean,
    noResidual,
    probeFailure: snapshot.probeFailure,
    condition: workspaceConditionLabel(snapshot, expectedBranch),
  }
}

/** Branch label for diagnostics: the branch name, `(detached)`, or `(unknown)` on probe failure. */
export function observedBranchLabel(snapshot: WorkspaceHealthSnapshot): string {
  if (snapshot.probeFailure) return '(unknown)'
  return snapshot.head.ref === DETACHED_HEAD_REF ? '(detached)' : snapshot.head.ref
}

/** Ref label for diagnostics: branch name, detached commit sha, or `(unknown)` on probe failure. */
export function observedRefLabel(snapshot: WorkspaceHealthSnapshot): string {
  if (snapshot.probeFailure) return '(unknown)'
  if (snapshot.head.ref === DETACHED_HEAD_REF) return snapshot.head.commit || '(unknown)'
  return snapshot.head.ref
}

/** Compact residual-state label, e.g. `rebase,merge` or `none`. */
export function residualLabel(residual: WorkspaceResidualState): string {
  const names: string[] = []
  if (residual.rebaseMerge || residual.rebaseApply) names.push('rebase')
  if (residual.mergeHead) names.push('merge')
  if (residual.cherryPickHead) names.push('cherry-pick')
  return names.length > 0 ? names.join(',') : 'none'
}

export interface WorkspaceHealthDiagnosticInput {
  /** The operation that failed, e.g. `checkout`, `abort-rebase`, `verify`, `status`. */
  operation: string
  expectedBranch: string
  snapshot: WorkspaceHealthSnapshot
  /** Operation-specific detail appended after the observed condition. */
  detail?: string
}

/**
 * Build a durable, actionable failure diagnostic carrying the expected
 * branch, the observed branch or detached ref, the dirty flag, residual
 * state, the failed operation, and the observed workspace condition.
 */
export function workspaceHealthDiagnostic(input: WorkspaceHealthDiagnosticInput): string {
  const { operation, expectedBranch, snapshot, detail } = input
  const fields = [
    `operation=${operation}`,
    `expectedBranch=${expectedBranch}`,
    `observedBranch=${observedBranchLabel(snapshot)}`,
    `observedRef=${observedRefLabel(snapshot)}`,
    `dirty=${snapshot.porcelain.trim() !== ''}`,
    `residual=${residualLabel(snapshot.residual)}`,
  ]
  const base = `workspace health failure: ${fields.join(' ')}; ${workspaceConditionLabel(snapshot, expectedBranch)}`
  return detail ? `${base}; ${detail}` : base
}

function workspaceConditionLabel(snapshot: WorkspaceHealthSnapshot, expectedBranch: string): string {
  if (snapshot.probeFailure) {
    return `probe failed (${snapshot.probeFailure.step}): ${snapshot.probeFailure.message}`
  }
  const parts: string[] = []
  if (snapshot.head.ref === DETACHED_HEAD_REF) {
    parts.push(`HEAD is detached at ${snapshot.head.commit || 'unknown commit'}`)
  } else if (snapshot.head.ref !== expectedBranch) {
    parts.push(`HEAD is on '${snapshot.head.ref}' (expected '${expectedBranch}')`)
  } else {
    parts.push(`HEAD is on '${snapshot.head.ref}'`)
  }
  parts.push(snapshot.porcelain.trim() === '' ? 'worktree is clean' : 'worktree is dirty')
  const residual = residualLabel(snapshot.residual)
  if (residual !== 'none') parts.push(`${residual} operation in progress`)
  return parts.join('; ')
}
