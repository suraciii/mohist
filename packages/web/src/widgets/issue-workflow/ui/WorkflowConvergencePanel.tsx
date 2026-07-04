import type { WorkflowConvergenceState } from '../../../entities/issue'

interface WorkflowConvergencePanelProps {
  convergence: WorkflowConvergenceState | null | undefined
}

export function WorkflowConvergencePanel({ convergence }: WorkflowConvergencePanelProps) {
  if (!convergence) return null

  const {
    failedCheck,
    blockedReason,
    blockingItemCount,
    directlyRepairedCount,
    reactionAttempts,
    resolvedItemIds,
    unresolvedItemIds,
    nonBlockingItemIds,
  } = convergence

  if (!failedCheck && blockingItemCount === 0 && reactionAttempts === 0) {
    return null
  }

  return (
    <div className="rounded-lg border border-danger-border bg-danger-subtle p-4 space-y-3">
      <div className="flex items-center gap-2">
        <svg className="h-4 w-4 text-danger" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
        <span className="text-sm font-semibold text-danger">Workflow Blocked</span>
      </div>

      {failedCheck && (
        <div className="text-xs text-danger">
          <span className="font-medium">Failed check:</span> {failedCheck}
        </div>
      )}

      {blockedReason && (
        <p className="text-xs text-danger">{blockedReason}</p>
      )}

      <div className="grid grid-cols-2 gap-2 text-xs">
        <div className="flex justify-between">
          <span className="text-danger">Blocking items:</span>
          <span className="font-medium text-danger">{blockingItemCount}</span>
        </div>
        <div className="flex justify-between">
          <span className="text-danger">Directly repaired:</span>
          <span className="font-medium text-danger">{directlyRepairedCount}</span>
        </div>
        <div className="flex justify-between">
          <span className="text-danger">Reaction attempts:</span>
          <span className="font-medium text-danger">{reactionAttempts}</span>
        </div>
        <div className="flex justify-between">
          <span className="text-danger">Resolved:</span>
          <span className="font-medium text-success">{resolvedItemIds.length}</span>
        </div>
        <div className="flex justify-between">
          <span className="text-danger">Unresolved:</span>
          <span className="font-medium text-danger">{unresolvedItemIds.length}</span>
        </div>
      </div>

      {nonBlockingItemIds.length > 0 && (
        <div className="pt-2 border-t border-danger-border">
          <div className="text-xs text-warning">
            <span className="font-medium">Follow-up items:</span> {nonBlockingItemIds.length}
          </div>
          <div className="mt-1 text-xs text-warning">
            These do not block the current workflow.
          </div>
        </div>
      )}

      {resolvedItemIds.length > 0 && unresolvedItemIds.length === 0 && blockingItemCount === 0 && (
        <div className="pt-2 border-t border-success-border">
          <div className="flex items-center gap-1.5 text-xs text-success">
            <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
            </svg>
            <span className="font-medium">All blocking items resolved</span>
          </div>
        </div>
      )}
    </div>
  )
}
