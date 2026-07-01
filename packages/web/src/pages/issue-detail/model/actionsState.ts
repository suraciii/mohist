import { IssueHealth, IssueStatus, WorkflowStage, type Issue, type RecoveryProjection } from '../../../entities/issue'
import type { AgentProgress, AgentStatus } from '../../../entities/agent'
import type { WorkflowTimeline } from '../../../entities/issue/model/workflow-timeline'

const SHOW_CHECK_REPAIR_ACTIONS = false

export type StartVariant =
  | { kind: 'draft' }
  | { kind: 'waiting-for'; issue: { number: number; title?: string } }
  | {
      kind: 'ready'
      runnerUnavailable: boolean
      isAgentRunningOnThis: boolean
      isCapacityFull: boolean
      runnerMessage: string | null | undefined
    }

export interface ForceStopContext {
  agentProgress: AgentProgress | null | undefined
  recoveryCanWait: boolean
  recoveryCanStop: boolean
  recoveryAttemptState: RecoveryProjection['latestAttemptState'] | null | undefined
  currentWorkItem: RecoveryProjection['currentWorkItem']
}

export interface BlockedActions {
  showRetry: boolean
  showResume: boolean
  showRerun: boolean
  showStop: boolean
  isInterrupted: boolean
  showProjectedCheckRepair: boolean
  showInspectCurrent: boolean
  currentWorkItem: RecoveryProjection['currentWorkItem']
  showBlockedReason: boolean
  blockedReason: string | null | undefined
}

export interface ErrorMessages {
  closeError: Error | null | undefined
  reopenError: Error | null | undefined
  startError: Error | null | undefined
  rerunError: Error | null | undefined
  retryError: Error | null | undefined
}

export interface ComputeActionsStateInput {
  issue: Pick<
    Issue,
    | 'number'
    | 'status'
    | 'health'
    | 'workflowStage'
    | 'workflowRunId'
    | 'archivedAt'
    | 'isDraft'
    | 'blocker'
    | 'blockedReason'
    | 'recovery'
  >
  agentStatus: AgentStatus | null | undefined
  workflowTimeline: WorkflowTimeline | null | undefined
  errorMessages: ErrorMessages
}

export interface ComputedActionsState {
  showArchivedNote: boolean
  startVariant: StartVariant | null
  showForceStopPanel: boolean
  forceStopContext: ForceStopContext | null
  blockedActions: BlockedActions
  showStandaloneRerun: boolean
  showClose: boolean
  showError: boolean
  errorMessages: ErrorMessages
  showOtherAgents: boolean
  otherAgentsCount: number
}

function computeAllowedActions(
  recovery: RecoveryProjection | null | undefined,
  workflowTimeline: WorkflowTimeline | null | undefined,
): string[] {
  const recoveryAllowed = recovery?.allowedActions ?? []
  const workflowAllowed = workflowTimeline?.availableActions.map((a) => a.name) ?? []
  return Array.from(new Set([...recoveryAllowed, ...workflowAllowed]))
}

function computeCanStopWorkflow(issue: ComputeActionsStateInput['issue']): boolean {
  return !!issue.workflowRunId
    && issue.health !== IssueHealth.Done
    && issue.status !== IssueStatus.Done
    && issue.status !== IssueStatus.Cancelled
    && issue.health !== IssueHealth.Paused
}

export function computeActionsState(input: ComputeActionsStateInput): ComputedActionsState {
  const {
    issue,
    agentStatus,
    workflowTimeline,
    errorMessages,
  } = input

  const activeAgents = agentStatus?.activeAgents ?? []
  const isAgentRunningOnThis = activeAgents.some((a) => a.issueNumber === issue.number)
  const capacity = agentStatus?.capacity
  const isCapacityFull = !!capacity && capacity.max > 0 && capacity.active >= capacity.max

  const recovery: RecoveryProjection | null | undefined = issue.recovery
  const recoveryAllowedActions = recovery?.allowedActions ?? []
  const recoveryAttemptState = recovery?.latestAttemptState
  const recoveryCanWait = recoveryAllowedActions.includes('wait')
  const recoveryCanStop = recoveryAllowedActions.includes('stop')

  const isBacklog = issue.status === IssueStatus.Backlog
  const isArchived = !!issue.archivedAt
  const workflowStage: WorkflowStage | null | undefined = issue.workflowStage ?? null
  const allowedActions = computeAllowedActions(recovery, workflowTimeline)
  const canRetryWorkflow = allowedActions.includes('retry')
  const canResumeWorkflow = allowedActions.includes('resume')
  const canRerunWorkflow = allowedActions.includes('rerun')
  const canStopWorkflow = computeCanStopWorkflow(issue)

  const thisAgent = activeAgents.find((a) => a.issueNumber === issue.number)
  const agentProgress = thisAgent?.progress
  const runnerUnavailable = agentStatus?.runnerAvailable === false

  const showArchivedNote = isArchived

  let startVariant: StartVariant | null = null
  if (isBacklog && !isArchived) {
    if (issue.isDraft) {
      startVariant = { kind: 'draft' }
    } else if (issue.blocker?.kind === 'waiting-for') {
      startVariant = { kind: 'waiting-for', issue: issue.blocker.issue }
    } else {
      startVariant = {
        kind: 'ready',
        runnerUnavailable,
        isAgentRunningOnThis,
        isCapacityFull,
        runnerMessage: agentStatus?.runnerMessage,
      }
    }
  }

  const showForceStopPanel = !isArchived && (isAgentRunningOnThis || recoveryCanWait || recoveryCanStop)
  const forceStopContext: ForceStopContext | null = showForceStopPanel
    ? {
        agentProgress,
        recoveryCanWait,
        recoveryCanStop,
        recoveryAttemptState,
        currentWorkItem: recovery?.currentWorkItem ?? null,
      }
    : null

  const isInterrupted = recoveryAttemptState === 'interrupted'
  const canInspect = allowedActions.includes('inspect')
  const showProjectedCheckRepairActions = SHOW_CHECK_REPAIR_ACTIONS && (canRetryWorkflow || canRerunWorkflow)

  const showBlockedActions = !isArchived
    && (issue.health === IssueHealth.Blocked || issue.health === IssueHealth.Interrupted)

  const blockedActions: BlockedActions = {
    showRetry: showBlockedActions && canRetryWorkflow,
    showResume: showBlockedActions && canResumeWorkflow,
    showRerun: showBlockedActions && canRerunWorkflow,
    showStop: showBlockedActions && canStopWorkflow,
    isInterrupted: showBlockedActions && isInterrupted,
    showProjectedCheckRepair: showProjectedCheckRepairActions,
    showInspectCurrent: showBlockedActions && canInspect,
    currentWorkItem: recovery?.currentWorkItem ?? null,
    showBlockedReason: showBlockedActions && !!issue.blockedReason,
    blockedReason: issue.blockedReason,
  }

  const showStandaloneRerun = !isBacklog
    && issue.status !== IssueStatus.Done
    && !!workflowStage
    && !isAgentRunningOnThis
    && canRerunWorkflow
    && issue.health !== IssueHealth.Blocked
    && issue.health !== IssueHealth.Interrupted
    && !SHOW_CHECK_REPAIR_ACTIONS

  const showClose = issue.health === IssueHealth.Active && !isAgentRunningOnThis

  const showError = !!(errorMessages.closeError
    || errorMessages.reopenError
    || errorMessages.startError
    || errorMessages.rerunError
    || errorMessages.retryError)

  const otherAgentsCount = activeAgents.length
  const showOtherAgents = !isAgentRunningOnThis && otherAgentsCount > 0 && !isBacklog

  return {
    showArchivedNote,
    startVariant,
    showForceStopPanel,
    forceStopContext,
    blockedActions,
    showStandaloneRerun,
    showClose,
    showError,
    errorMessages,
    showOtherAgents,
    otherAgentsCount,
  }
}
