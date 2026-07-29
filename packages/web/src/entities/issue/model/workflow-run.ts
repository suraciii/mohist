import type { WorkItemOrigin, StageApprovalState, WorkflowTaskCause, WorkflowFailureDetails } from './stage-state'

/**
 * Tracks server `WorkflowRunStatus` (packages/server .../WorkflowRun.cs).
 * Each value MUST be the wire token emitted by `WorkflowStatusMapper.WireStatus(WorkflowRunStatus)`.
 * Source of truth is the server enum — extend this union when that enum gains a value.
 */
export type WorkflowRunStatus =
  | 'created'
  | 'pending'
  | 'ready'
  | 'running'
  | 'awaiting-approval'
  | 'paused'
  | 'stopped'
  | 'completed'
  | 'failed'

export type WorkflowTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped'
export type WorkflowCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error'

/**
 * Tracks server `StageRunStatus` (packages/server .../StageRun.cs).
 * Each value MUST be the wire token emitted by `WorkflowStatusMapper.WireStatus(StageRunStatus)`.
 * `passed` and `skipped` are client-only projections that no server enum value emits.
 * Source of truth is the server enum — extend this union when that enum gains a value
 * (server already emits `completed`; do not remove it).
 */
export type WorkflowStageRunStatus =
  | 'pending'
  | 'running'
  | 'awaiting-approval'
  | 'completed'
  | 'passed'
  | 'failed'
  | 'skipped'

export interface WorkflowTaskResetCause {
  type: 'workflow-policy'
  taskId?: string
  eventName?: string
  message?: string
}

export interface WorkflowCheckFailurePolicy {
  checkName: string
  fixTaskId: string
  fixTaskTitle: string
  maxAttempts: number
}

export interface WorkflowStageDefinition {
  stage: import('./issue').WorkflowStage
  checkFailurePolicies?: WorkflowCheckFailurePolicy[]
}

export type WorkflowDefinitionSource =
  | { type: 'builtin'; id: string }
  | { type: 'project'; path: string }
  | { type: 'runtime'; id: string }

export interface WorkflowDefinitionMetadata {
  workflowId: string
  name?: string
  source: WorkflowDefinitionSource
  capturedAt: string
  stageOrder: import('./issue').WorkflowStage[]
  stageDefinitions?: WorkflowStageDefinition[]
}

export interface WorkflowTask {
  id: string
  taskId: string
  title: string
  status: WorkflowTaskStatus
  origin?: WorkItemOrigin | null
  taskOrder: number
  attempts: number
  duration: number
  artifacts: string[]
  output: Record<string, unknown> | null
  error?: import('./stage-state').WorkflowExecutionError | null
  reason: string | null
  causedBy: WorkflowTaskCause | null
  resetBy: WorkflowTaskResetCause | null
  startedAt: string | null
  completedAt: string | null
}

export interface WorkflowCheck {
  checkName: string
  title: string
  status: WorkflowCheckStatus
  message: string | null
  output: unknown
  error?: import('./stage-state').WorkflowExecutionError | null
  runCount: number
  lastRunAt: string | null
  origin?: WorkItemOrigin | null
}

export interface WorkflowStageRun {
  stage: import('./issue').WorkflowStage
  status: WorkflowStageRunStatus
  definition?: WorkflowStageDefinition | null
  tasks: WorkflowTask[]
  checks: WorkflowCheck[]
  approvalStatus: string | null
  approvalOutput: unknown | null
  approvalRequestedAt: string | null
  approvalRespondedAt: string | null
  approval?: StageApprovalState | null
  failure?: WorkflowFailureDetails | null
  attempts: number
  startedAt: string | null
  completedAt: string | null
  updatedAt?: string
}
