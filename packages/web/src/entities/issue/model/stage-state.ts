import type { WorkflowStage } from './issue'
import type { WorkflowArtifactSummary, WorkflowTaskRequiredFile } from './artifact'
import type { BaseDriftInfo } from './drift'

export type StageTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped' | 'blocked'
export type StageCheckStatus = 'pending' | 'running' | 'completed' | 'passed' | 'failed' | 'error'

/**
 * Tracks the wire status of a workflow stage as emitted by
 * `WorkflowStatusMapper.BuildStatusView`. `blocked` is derived there from a
 * blocked Agent settlement in the stage (nonterminal attention); it is not a
 * server enum value.
 */
export type StageStateStatus = 'pending' | 'running' | 'awaiting-approval' | 'completed' | 'passed' | 'failed' | 'skipped' | 'blocked'

export interface StageTaskCause {
  type: 'check-failure' | 'health-check-failure' | 'retry' | 'rebase' | 'merge-conflict' | 'unknown'
  checkName?: string
  taskId?: string
  message?: string
}

export interface WorkflowTaskCause {
  type: 'check-failure' | 'task-failure' | 'branch-changed' | 'conflict' | 'retry' | 'user-action' | 'system-policy'
  checkName?: string
  taskId?: string
  message?: string
}

export interface WorkItemOrigin {
  source: 'builtin' | 'project' | 'runtime'
  uses: string
}

export interface WorkflowFailureDetails {
  reason: string
  stage: WorkflowStage
  taskId?: string
  checkName?: string
  message?: string | null
  error?: WorkflowExecutionError | null
  causedBy?: WorkflowTaskCause | null
}

export interface WorkflowExecutionError {
  code: string
  message: string
}

export interface StageTaskState {
  taskId: string
  title: string
  status: StageTaskStatus
  sessionName?: string | null
  source?: 'static' | 'dynamic'
  origin?: WorkItemOrigin | null
  order: number
  attempts: number
  duration: number
  artifacts: string[]
  artifactSummaries?: WorkflowArtifactSummary[]
  output: Record<string, unknown> | null
  error?: WorkflowExecutionError | null
  startedAt: string | null
  completedAt: string | null
  updatedAt: string
  reason?: string
  causedBy?: StageTaskCause
  requiredFiles?: WorkflowTaskRequiredFile[]
  classification?: 'UserFacing' | 'Orchestration'
}

export interface StageCheckState {
  checkName: string
  title?: string
  status: StageCheckStatus
  message: string | null
  output: unknown
  error?: WorkflowExecutionError | null
  runCount: number
  lastRunAt: string | null
  origin?: WorkItemOrigin | null
  updatedAt: string
}

export interface StageApprovalState {
  status: string
  output: unknown
  requestedAt: string | null
  respondedAt: string | null
}

export interface StageStateRead {
  stage: WorkflowStage
  status: StageStateStatus
  tasks: StageTaskState[]
  checks: StageCheckState[]
  approval: StageApprovalState | null
  attempts: number
  startedAt: string | null
  completedAt: string | null
  updatedAt: string
  failure?: WorkflowFailureDetails | null
}

export interface IssueStageStateResponse {
  issueNumber: number
  stages: StageStateRead[]
  drift?: BaseDriftInfo | null
}
