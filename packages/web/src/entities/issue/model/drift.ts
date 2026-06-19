export type RebaseDecision = 'skip' | 'suggest' | 'enqueue' | 'defer' | 'needs-attention'
export type DeferReason = 'agent-running' | 'task-running' | 'waiting-for-task-boundary' | 'rebase-already-pending'

export interface BaseDriftInfo {
  drifted: boolean
  decision: RebaseDecision | null
  safeWindow: boolean | null
  deferReason: DeferReason | null
  observedBaseSha: string | null
  currentBaseSha: string | null
  candidateHeadSha: string | null
  mergeBaseSha: string | null
  conflicts: string[] | null
  nextAction: string | null
}

export interface RebaseConflictState {
  issueNumber: number
  conflicts: string[]
  status: string
  error?: string
}
