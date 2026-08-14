import type { WorkflowStage } from './issue'
import type { StageStateStatus, StageApprovalState, StageTaskStatus, WorkflowAgentResultAttention, WorkflowAgentResultSettlement } from './stage-state'
import type { WorkflowTaskRequiredFile, WorkflowArtifactSummary } from './artifact'

export interface WorkflowTimelinePendingWork {
  workId: string
  workType: string
  stage: string | null
  title: string | null
  uses: string | null
}

export interface WorkflowTimelineTask {
  id: string
  title: string
  uses: string | null
  status: StageTaskStatus
  sessionName?: string | null
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  attempts: number
  message: string | null
  requiredFiles?: WorkflowTaskRequiredFile[]
  classification?: 'UserFacing' | 'Orchestration'
  artifactSummaries?: WorkflowArtifactSummary[]
  output?: Record<string, unknown> | null
  error?: import('./stage-state').WorkflowExecutionError | null
  agentResultSettlement?: WorkflowAgentResultSettlement | null
}

export interface WorkflowTimelineCheck {
  name: string
  title: string
  uses: string | null
  status: string
  message: string | null
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  error?: import('./stage-state').WorkflowExecutionError | null
}

export interface WorkflowTimelineStage {
  stage: WorkflowStage
  status: StageStateStatus
  order: number
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  tasks: WorkflowTimelineTask[]
  checks: WorkflowTimelineCheck[]
  approval: StageApprovalState | null
}

export interface WorkflowTimelineAction {
  name: string
  label: string
  target: string | null
}

export interface WorkflowTimeline {
  workflowRunId: string
  status: string
  currentStage: string | null
  pendingWork: WorkflowTimelinePendingWork | null
  stages: WorkflowTimelineStage[]
  availableActions: WorkflowTimelineAction[]
  agentResultAttention?: WorkflowAgentResultAttention | null
}
