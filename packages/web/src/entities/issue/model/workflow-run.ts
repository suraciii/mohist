import type { WorkItemOrigin, StageApprovalState, WorkflowTaskCause, WorkflowFailureDetails } from './stage-state'

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
}
/**
 * Availability and content of the semantic definition one Run actually
 * bound at start time (#1099). When `available` is true, `content` is the
 * Run's retained start-time snapshot — never the latest Profile — and the
 * read is complete, without truncation. When false, `reason` states the
 * known cause and nothing is substituted.
 */
export interface WorkflowRunBindingDefinition {
  available: boolean
  source: 'run-snapshot' | null
  reason: 'no-snapshot' | 'unreadable-snapshot' | 'unreadable-run-state' | null
  content: WorkflowBoundDefinition | null
}

/**
 * The bound semantic definition snapshot (the server's WorkflowDefinition
 * shape). Web reads identify the method a Run followed — stages, tasks,
 * checks — and render full content through the existing YAML reader, so
 * this type carries identity plus the raw structure.
 */
export interface WorkflowBoundDefinition {
  stages: {
    stage: string
    requiresApproval?: boolean
    tasks: { id: string; title?: string; uses?: string }[]
    checks: { id: string; title?: string; uses?: string }[]
  }[]
  approval?: Record<string, unknown> | null
  recoveries?: Record<string, unknown> | null
}

/**
 * The structured actual-binding read behind
 * `GET /api/workflow-runs/{id}/binding` / `mo run view <run> --json binding`:
 * the start-time facts the Run itself retains (identity, bound Profile,
 * timing) plus the bound definition. This is the Run's binding, distinct
 * from the Issue's next-start selection and its inheritance source.
 */
export interface WorkflowRunBinding {
  workflowRunId: string
  projectId: string | null
  issueNumber: number | null
  status: WorkflowRunStatus | string | null
  workflowProfileId: string | null
  explicitWorkflowProfileId: string | null
  createdAt: string | null
  startedAt: string | null
  definition: WorkflowRunBindingDefinition
}

export function isTerminalWorkflowRunStatus(status: WorkflowRunStatus | string | null | undefined): boolean {
  return status === 'stopped' || status === 'completed'
}

export type WorkflowTaskStatus = 'pending' | 'running' | 'completed' | 'failed' | 'skipped' | 'blocked'
export type WorkflowCheckStatus = 'pending' | 'running' | 'passed' | 'failed' | 'error'

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
