import { IssueHealth, WorkflowStage, type RecoveryProjection } from '../../../entities/issue'
import {
  findFailedCheck,
  findFailedScriptHealthCheck,
  findRunningCheck,
  findRunningTask,
} from './runtime-query-helpers'
import {
  buildPresentationContext,
  resolveSummaryPresentation,
} from './runtime-presentations'
import type {
  RuntimeAvailableAction,
  RuntimeCurrentTask,
  RuntimeDecision,
  RuntimeDecisionInput,
  RuntimeSummary,
} from './runtime-types'

export type {
  RuntimeActionKind,
  RuntimeAvailableAction,
  RuntimeCurrentTask,
  RuntimeDecision,
  RuntimeDecisionInput,
  RuntimeSummary,
} from './runtime-types'

const APPROVAL_FAILURE_OVERRIDE_SUMMARY: RuntimeSummary = 'failed'

export function buildWaitReason(input: RuntimeDecisionInput): string | null {
  const blocker = input.issue?.blocker
  if (blocker?.kind === 'draft') {
    return 'Issue is still a draft. Mark it ready before starting.'
  }
  if (blocker?.kind === 'waiting-for') {
    return `Waiting for #${blocker.issue.number} ${blocker.issue.title}`.trim()
  }

  if (input.agentStatus?.runnerAvailable === false) {
    return input.agentStatus.runnerMessage
      ?? 'No runner is connected. Start a runner before this issue can run.'
  }

  const capacity = input.agentStatus?.capacity
  if (capacity && capacity.max > 0 && capacity.active >= capacity.max) {
    return `Runner capacity is full (${capacity.active}/${capacity.max}).`
  }

  return null
}

function determineSummary(input: RuntimeDecisionInput): RuntimeSummary {
  const issue = input.issue

  if (!issue) {
    return 'running'
  }

  const stage = (issue.workflowStage ?? null) as WorkflowStage | null
  const status = (issue.workflowStatus ?? '').toLowerCase()
  const health = issue.health

  const isDone =
    stage === WorkflowStage.Done
    || status === 'done'
    || health === IssueHealth.Done
  if (isDone) {
    return 'done'
  }

  const recovery = issue.recovery
  const failedScriptHealthCheck = findFailedScriptHealthCheck(input.timeline)

  if (stage === WorkflowStage.Check && failedScriptHealthCheck && recovery?.latestAttemptState !== 'running') {
    return APPROVAL_FAILURE_OVERRIDE_SUMMARY
  }

  if (
    recovery?.latestAttemptState === 'failed'
    || (failedScriptHealthCheck && recovery?.latestAttemptState !== 'running')
  ) {
    return 'failed'
  }

  if (health === IssueHealth.Blocked) {
    return 'blocked'
  }

  if (
    recovery?.latestAttemptState === 'interrupted'
    || status === 'interrupted'
  ) {
    return 'blocked'
  }

  const approval = issue.approvalState
  if (approval?.status === 'awaiting') {
    return 'approval-required'
  }

  const hasUnresolvedConvergence = !!issue.convergence
    && (issue.convergence.unresolvedItemIds?.length ?? 0) > 0
  if (hasUnresolvedConvergence) {
    return 'blocked'
  }

  const waitReason = buildWaitReason(input)
  const hasExplicitQueueSignal =
    !!input.issue?.blocker
    || input.agentStatus?.runnerAvailable === false
    || (!!input.agentStatus?.capacity?.max && (input.agentStatus.capacity.active >= input.agentStatus.capacity.max))
    || input.hasActiveAgent === false
  const isBacklog = issue.status === 'backlog'

  if (waitReason && (hasExplicitQueueSignal || isBacklog)) {
    return 'queued'
  }

  if (isBacklog) {
    return 'queued'
  }

  if (recovery?.latestAttemptState === 'running' || input.hasActiveAgent) {
    return 'running'
  }

  if (recovery?.workflowSummaryState === 'awaiting-approval') {
    return 'approval-required'
  }

  if (stage === WorkflowStage.Check && failedScriptHealthCheck) {
    return 'failed'
  }

  return 'running'
}

function pickCurrentTask(
  input: RuntimeDecisionInput,
  summary: RuntimeSummary,
): RuntimeCurrentTask | null {
  const recovery = input.issue?.recovery
  if (recovery?.currentWorkItem?.title) {
    return {
      kind: recovery.currentWorkItem.type,
      title: recovery.currentWorkItem.title,
      status: recovery.latestAttemptState ?? null,
    }
  }

  const stageProgress = input.issue?.workflowStageProgress
  if (stageProgress?.currentTaskTitle) {
    return {
      kind: 'task',
      title: stageProgress.currentTaskTitle,
      status: stageProgress.running > 0 ? 'running' : null,
    }
  }

  if (summary === 'failed') {
    const failedCheck = findFailedCheck(input.timeline)
    if (failedCheck) {
      return { kind: 'check', title: failedCheck.title, status: failedCheck.status }
    }
  }

  const runningCheck = findRunningCheck(input.timeline)
  if (runningCheck) {
    return { kind: 'check', title: runningCheck.title, status: runningCheck.status }
  }

  const runningTask = findRunningTask(input.timeline)
  if (runningTask) {
    return { kind: 'task', title: runningTask.title, status: runningTask.status }
  }

  return null
}

function hasRecoverableStop(input: RuntimeDecisionInput): boolean {
  const recoveryActions = input.issue?.recovery?.allowedActions ?? []
  return recoveryActions.includes('stop')
    || recoveryActions.includes('force-stop')
    || recoveryActions.includes('force_stop')
}

function pickPrimaryAction(actions: RuntimeAvailableAction[]): RuntimeAvailableAction | null {
  const executable = actions.find((action) => action.enabled)
  if (executable) return executable
  return actions[0] ?? null
}

function buildDriftNote(input: RuntimeDecisionInput): string | null {
  const drift = input.issue?.drift
  if (!drift?.drifted) return null
  if (drift.nextAction) return drift.nextAction
  if (drift.decision === 'needs-attention') {
    return 'Base drift requires attention.'
  }
  if (drift.decision === 'defer') {
    return 'Base drift is currently deferred.'
  }
  return 'Base drift detected.'
}

export function deriveRuntimeDecision(input: RuntimeDecisionInput): RuntimeDecision {
  const issue = input.issue
  const summary = determineSummary(input)
  const currentTask = pickCurrentTask(input, summary)
  const computedWaitReason = buildWaitReason(input)
  const contextInput = buildPresentationContext(input)
  const presentation = resolveSummaryPresentation(
    contextInput,
    currentTask,
    summary,
    computedWaitReason,
  )
  const waitReason = summary === 'queued' ? computedWaitReason : null
  const primary = pickPrimaryAction(presentation.actions)
  const hasStop = presentation.actions.some((action) => action.kind === 'stop')
  const stopRecoverable = hasStop ? hasRecoverableStop(input) : null
  const driftNote = buildDriftNote(input)
  const blockedReason = summary === 'failed' || summary === 'blocked'
    ? (issue?.blockedReason ?? issue?.convergence?.blockedReason ?? null)
    : null
  const approvalStage = summary === 'approval-required'
    ? (issue?.approvalState?.stage ?? issue?.workflowStage ?? null)
    : null

  return {
    summary,
    headline: presentation.headline,
    rationale: presentation.rationale,
    currentTask,
    nextAction: presentation.nextAction,
    primary,
    actions: presentation.actions,
    stopRecoverable,
    waitReason,
    driftNote,
    blockedReason,
    approvalStage,
  }
}

export type { RecoveryProjection }
