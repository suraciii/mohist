import type { AgentStatus } from '../../../entities/agent'
import type { Issue, WorkflowTimeline } from '../../../entities/issue'

export type RuntimeSummary =
  | 'running'
  | 'recoverable-interrupted'
  | 'queued'
  | 'approval-required'
  | 'blocked'
  | 'failed'
  | 'done'
  | 'cancelled'

export type RuntimeActionKind =
  | 'approve'
  | 'send-back'
  | 'retry'
  | 'resume'
  | 'rerun'
  | 'stop'
  | 'start'

export interface RuntimeCurrentTask {
  kind: 'task' | 'check'
  title: string
  status: string | null
}

export interface RuntimeAvailableAction {
  kind: RuntimeActionKind
  label: string
  enabled: boolean
  reason?: string
}

export interface RuntimeDecision {
  summary: RuntimeSummary
  headline: string
  rationale: string
  currentTask: RuntimeCurrentTask | null
  nextAction: string
  primary: RuntimeAvailableAction | null
  actions: RuntimeAvailableAction[]
  stopRecoverable: boolean | null
  waitReason: string | null
  driftNote: string | null
  blockedReason: string | null
  approvalStage: string | null
}

export interface RuntimeDecisionInput {
  issue: Pick<Issue,
    | 'status'
    | 'workflowStage'
    | 'workflowStatus'
    | 'health'
    | 'approvalState'
    | 'blockedReason'
    | 'attention'
    | 'recovery'
    | 'convergence'
    | 'drift'
    | 'workflowStageProgress'
    | 'isDraft'
    | 'canStart'
    | 'blocker'
  > & { prerequisites?: Issue['prereq'] } | null | undefined
  timeline?: Pick<WorkflowTimeline, 'currentStage' | 'status' | 'stages' | 'pendingWork' | 'availableActions'> | null
  agentStatus?: Pick<AgentStatus, 'runnerAvailable' | 'runnerMessage' | 'capacity' | 'activeAgents'> | null
  issueNumber?: number
  hasActiveAgent?: boolean
  hasAnyActiveAgent?: boolean
}
