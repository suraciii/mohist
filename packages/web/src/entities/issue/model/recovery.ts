export type WorkItemAttemptState = 'running' | 'completed' | 'failed'
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
