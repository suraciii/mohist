export type WorkItemAttemptState = 'running' | 'completed' | 'failed' | 'interrupted'

/**
 * Client-side projection derived from server `WorkflowRunStatus`
 * (packages/server .../WorkflowRun.cs). Not a 1:1 mirror: `waiting-for-recovery`
 * is a recovery-domain synthesis that no server enum value emits directly.
 * The recovery domain owns the projection rule — extend this union when the
 * recovery projection gains a new state.
 */
export type WorkflowRecoverySummary = 'running' | 'awaiting-approval' | 'waiting-for-recovery' | 'completed'

export interface RecoveryProjection {
  currentWorkItem: {
    type: 'task' | 'check'
    id: string
    title: string
  } | null
  latestAttemptState: WorkItemAttemptState | null
  workflowSummaryState: WorkflowRecoverySummary | null
  allowedActions: string[]
}

export interface WorkflowConvergenceState {
  failedCheck?: string
  blockingItemCount: number
  directlyRepairedCount: number
  reactionAttempts: number
  attemptedItemIds: string[]
  resolvedItemIds: string[]
  unresolvedItemIds: string[]
  newBlockingItemIds: string[]
  nonBlockingItemIds: string[]
  blockedReason?: string
}
