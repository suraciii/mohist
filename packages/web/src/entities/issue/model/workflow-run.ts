import type { WorkItemOrigin, StageApprovalState, WorkflowTaskCause, WorkflowFailureDetails, WorkInterruption } from './stage-state'

/**
 * Tracks the wire status of a WorkflowRun as emitted by
 * `WorkflowStatusMapper.BuildStatusView`. `blocked` is not a server enum
 * value: it is derived there from a blocked Agent settlement (nonterminal,
 * actionable attention) while the run's persisted status keeps its own
 * lifecycle value.
 */
export type WorkflowRunStatus =
  | 'created'
  | 'pending'
  | 'ready'
  | 'running'
  | 'recoverable-interrupted'
  | 'awaiting-approval'
  | 'paused'
  | 'stopped'
  | 'completed'
  | 'failed'
  | 'blocked'

export interface WorkflowRunDetail {
  status: {
    workflowRunId: string
    status: WorkflowRunStatus
  }
  issueRef: {
    projectId: string
    number: number
    title: string
  } | null
  workflowProfileId: string | null
  agentAction: string | null
  agentRuntime: string | null
}

export function isTerminalWorkflowRunStatus(status: WorkflowRunStatus | string | null | undefined): boolean {
  return status === 'stopped' || status === 'completed'
}

export type WorkflowTaskStatus = 'pending' | 'running' | 'recoverable-interrupted' | 'completed' | 'failed' | 'skipped' | 'blocked'
export type WorkflowCheckStatus = 'pending' | 'running' | 'recoverable-interrupted' | 'passed' | 'failed' | 'error'

/**
 * Tracks the wire status of a workflow stage as emitted by
 * `WorkflowStatusMapper.BuildStatusView`. `passed` and `skipped` are
 * client-only projections that no server enum value emits. `blocked` is
 * derived server-side from a blocked Agent settlement; it is not a server
 * enum value.
 */
export type WorkflowStageRunStatus =
  | 'pending'
  | 'running'
  | 'awaiting-approval'
  | 'completed'
  | 'passed'
  | 'failed'
  | 'skipped'
  | 'blocked'

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
  interruption?: WorkInterruption | null
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
  interruption?: WorkInterruption | null
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
  interruption?: WorkInterruption | null
}
