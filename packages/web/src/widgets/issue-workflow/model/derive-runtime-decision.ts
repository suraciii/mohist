import { IssueHealth, WorkflowStage, type Issue, type RecoveryProjection, type WorkflowTimeline } from '../../../entities/issue'
import type { AgentStatus } from '../../../entities/agent'

export type RuntimeSummary =
  | 'running'
  | 'queued'
  | 'approval-required'
  | 'blocked'
  | 'failed'
  | 'done'

export type RuntimeActionKind =
  | 'approve'
  | 'send-back'
  | 'retry'
  | 'resume'
  | 'rerun'
  | 'stop'
  | 'start'
  | 'inspect'

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
  actions: RuntimeAvailableAction[]
  waitReason: string | null
  driftNote: string | null
  blockedReason: string | null
}

export interface RuntimeDecisionInput {
  issue: Pick<Issue,
    | 'status'
    | 'workflowStage'
    | 'workflowStatus'
    | 'health'
    | 'approvalState'
    | 'blockedReason'
    | 'recovery'
    | 'convergence'
    | 'drift'
    | 'workflowStageProgress'
    | 'prerequisites'
    | 'isDraft'
    | 'canStart'
    | 'blocker'
  > | null | undefined
  timeline?: Pick<WorkflowTimeline, 'currentStage' | 'status' | 'stages' | 'pendingWork' | 'availableActions'> | null
  agentStatus?: Pick<AgentStatus, 'runnerAvailable' | 'runnerMessage' | 'capacity' | 'activeAgents'> | null
  issueNumber?: number
  hasActiveAgent?: boolean
  hasAnyActiveAgent?: boolean
}

const APPROVAL_FAILURE_OVERRIDE_SUMMARY: RuntimeSummary = 'failed'

function isScriptHealthCheck(check: { name?: string; status?: string; message?: string | null } | undefined | null): boolean {
  if (!check) return false
  if (check.name === 'health') return true
  return false
}

function isFailedCheck(check: { status?: string; message?: string | null } | undefined | null): boolean {
  if (!check || !check.status) return false
  const status = check.status.toLowerCase()
  return status === 'failed' || status === 'error'
}

function findFailedScriptHealthCheck(
  timeline: RuntimeDecisionInput['timeline'],
): boolean {
  if (!timeline?.stages) return false
  for (const stage of timeline.stages) {
    if (!stage.checks) continue
    for (const check of stage.checks) {
      if (isScriptHealthCheck(check) && isFailedCheck(check)) {
        return true
      }
    }
  }
  return false
}

function findRunningCheck(
  timeline: RuntimeDecisionInput['timeline'],
): { title: string; status: string } | null {
  if (!timeline?.stages) return null
  for (const stage of timeline.stages) {
    if (!stage.checks) continue
    for (const check of stage.checks) {
      const status = (check.status ?? '').toLowerCase()
      if (status === 'running') {
        return { title: check.title || check.name, status: check.status ?? 'running' }
      }
    }
  }
  return null
}

function findRunningTask(
  timeline: RuntimeDecisionInput['timeline'],
): { title: string; status: string } | null {
  if (!timeline?.stages) return null
  for (const stage of timeline.stages) {
    if (!stage.tasks) continue
    for (const task of stage.tasks) {
      const status = (task.status ?? '').toLowerCase()
      if (status === 'running') {
        return { title: task.title, status: task.status ?? 'running' }
      }
    }
  }
  return null
}

function findFailedCheck(
  timeline: RuntimeDecisionInput['timeline'],
): { title: string; status: string } | null {
  if (!timeline?.stages) return null
  for (const stage of timeline.stages) {
    if (!stage.checks) continue
    for (const check of stage.checks) {
      const status = (check.status ?? '').toLowerCase()
      if (status === 'failed' || status === 'error') {
        return { title: check.title || check.name, status: check.status ?? 'failed' }
      }
    }
  }
  return null
}

function formatStageLabel(stage: string | null | undefined): string {
  if (!stage) return 'workflow'
  return stage
    .split(/[_-]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ')
}

function buildAllowedActions(input: RuntimeDecisionInput): Set<string> {
  const recoveryActions = input.issue?.recovery?.allowedActions ?? []
  const timelineActions = (input.timeline?.availableActions ?? []).map((action) => action.name)
  return new Set([...recoveryActions, ...timelineActions])
}

function actionEnabled(
  allowed: Set<string>,
  kind: RuntimeActionKind,
): boolean {
  if (kind === 'start') {
    return allowed.has('start')
  }
  if (kind === 'approve') {
    return allowed.has('approve')
  }
  if (kind === 'send-back') {
    return allowed.has('reject') || allowed.has('send-back') || allowed.has('send_back')
  }
  if (kind === 'retry') {
    return allowed.has('retry')
  }
  if (kind === 'resume') {
    return allowed.has('resume')
  }
  if (kind === 'rerun') {
    return allowed.has('rerun')
  }
  if (kind === 'stop') {
    return allowed.has('stop') || allowed.has('force-stop') || allowed.has('force_stop')
  }
  if (kind === 'inspect') {
    return allowed.has('inspect')
  }
  return false
}

function buildActions(
  summary: RuntimeSummary,
  input: RuntimeDecisionInput,
  isBacklog: boolean,
  isClosed: boolean,
  isDone: boolean,
): RuntimeAvailableAction[] {
  const allowed = buildAllowedActions(input)
  const actions: RuntimeAvailableAction[] = []

  if (summary === 'approval-required') {
    actions.push({
      kind: 'approve',
      label: 'Approve',
      enabled: !isClosed && actionEnabled(allowed, 'approve'),
      reason: actionEnabled(allowed, 'approve')
        ? undefined
        : 'Approval is not currently offered by the backend projection.',
    })
    actions.push({
      kind: 'send-back',
      label: 'Send back',
      enabled: !isClosed && actionEnabled(allowed, 'send-back'),
      reason: actionEnabled(allowed, 'send-back')
        ? undefined
        : 'Send-back is not currently offered by the backend projection.',
    })
    return actions
  }

  if (summary === 'failed' || summary === 'blocked') {
    actions.push({
      kind: 'retry',
      label: 'Retry',
      enabled: !isClosed && !isDone && actionEnabled(allowed, 'retry'),
      reason: actionEnabled(allowed, 'retry')
        ? undefined
        : 'Retry is not currently offered by the backend projection.',
    })
    actions.push({
      kind: 'resume',
      label: 'Resume',
      enabled: !isClosed && !isDone && actionEnabled(allowed, 'resume'),
      reason: actionEnabled(allowed, 'resume')
        ? undefined
        : 'Resume is not currently offered by the backend projection.',
    })
    actions.push({
      kind: 'rerun',
      label: 'Rerun stage',
      enabled: !isClosed && !isDone && actionEnabled(allowed, 'rerun'),
      reason: actionEnabled(allowed, 'rerun')
        ? undefined
        : 'Rerun is not currently offered by the backend projection.',
    })
    if (summary === 'failed') {
      actions.push({
        kind: 'start',
        label: 'Start new workflow',
        enabled: !isClosed && !isDone && actionEnabled(allowed, 'start'),
        reason: actionEnabled(allowed, 'start')
          ? undefined
          : 'Start is not currently offered by the backend projection.',
      })
    } else {
      actions.push({
        kind: 'stop',
        label: 'Stop workflow',
        enabled: !isClosed && !isDone && actionEnabled(allowed, 'stop'),
        reason: actionEnabled(allowed, 'stop')
          ? undefined
          : 'Stop is not currently offered by the backend projection.',
      })
    }
    actions.push({
      kind: 'inspect',
      label: 'View transcript',
      enabled: actionEnabled(allowed, 'inspect'),
    })
    return actions
  }

  if (summary === 'queued') {
    return actions
  }

  if (summary === 'running') {
    actions.push({
      kind: 'stop',
      label: 'Stop workflow',
      enabled: !isClosed && !isDone && actionEnabled(allowed, 'stop'),
      reason: actionEnabled(allowed, 'stop')
        ? undefined
        : 'Stop is not currently offered by the backend projection.',
    })
    actions.push({
      kind: 'inspect',
      label: 'View transcript',
      enabled: actionEnabled(allowed, 'inspect'),
    })
    return actions
  }

  if (summary === 'done') {
    actions.push({
      kind: 'inspect',
      label: 'View transcript',
      enabled: actionEnabled(allowed, 'inspect'),
    })
    return actions
  }

  if (isBacklog) {
    actions.push({
      kind: 'start',
      label: 'Start',
      enabled: !isClosed && actionEnabled(allowed, 'start'),
      reason: actionEnabled(allowed, 'start')
        ? undefined
        : 'Start is not currently offered by the backend projection.',
    })
  }

  return actions
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

function buildWaitReason(input: RuntimeDecisionInput): string | null {
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

  const approval = issue.approvalState
  if (approval?.status === 'awaiting') {
    return 'approval-required'
  }

  const hasUnresolvedConvergence = !!issue.convergence
    && (issue.convergence.unresolvedItemIds?.length ?? 0) > 0
  if (
    health === IssueHealth.Blocked
    || recovery?.latestAttemptState === 'interrupted'
    || hasUnresolvedConvergence
  ) {
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

function buildHeadline(
  summary: RuntimeSummary,
  currentTask: RuntimeCurrentTask | null,
  issue: RuntimeDecisionInput['issue'],
): string {
  const stage = formatStageLabel(issue?.workflowStage ?? null)

  if (summary === 'running') {
    if (currentTask) {
      const label = currentTask.kind === 'check' ? 'Check' : 'Task'
      return `${label} running: ${currentTask.title}`
    }
    return `Workflow running (${stage})`
  }

  if (summary === 'queued') {
    return `Waiting to start (${stage})`
  }

  if (summary === 'approval-required') {
    if (currentTask) {
      return `Approval required on ${currentTask.title}`
    }
    return `Approval required at ${stage}`
  }

  if (summary === 'blocked') {
    if (currentTask) {
      return `Blocked on ${currentTask.title}`
    }
    return `Workflow blocked (${stage})`
  }

  if (summary === 'failed') {
    if (currentTask) {
      return `${currentTask.kind === 'check' ? 'Check' : 'Task'} failed: ${currentTask.title}`
    }
    return `Workflow failed (${stage})`
  }

  return `Workflow done`
}

function buildRationale(
  summary: RuntimeSummary,
  input: RuntimeDecisionInput,
  waitReason: string | null,
): string {
  if (summary === 'done') {
    return 'The workflow has completed.'
  }

  if (summary === 'approval-required') {
    return 'The workflow is paused and waiting for your review.'
  }

  if (summary === 'failed') {
    const blocked = input.issue?.blockedReason
    if (blocked) return blocked
    return 'The latest attempt failed and is not recoverable without intervention.'
  }

  if (summary === 'blocked') {
    const blocked = input.issue?.blockedReason
    if (blocked) return blocked
    if (input.issue?.convergence?.blockedReason) return input.issue.convergence.blockedReason
    if (input.issue?.recovery?.latestAttemptState === 'interrupted') {
      return 'The workflow was interrupted. Resume or rerun to continue.'
    }
    return 'The workflow is blocked and needs an action to continue.'
  }

  if (summary === 'queued') {
    return waitReason ?? 'The workflow is waiting to start.'
  }

  return 'The workflow is currently executing.'
}

function buildNextAction(
  summary: RuntimeSummary,
  actions: RuntimeAvailableAction[],
  currentTask: RuntimeCurrentTask | null,
  waitReason: string | null,
): string {
  if (summary === 'done') {
    return 'No further action required.'
  }

  if (summary === 'queued') {
    return waitReason ?? 'Wait for prerequisites or runner capacity.'
  }

  if (summary === 'approval-required') {
    const approve = actions.find((a) => a.kind === 'approve' && a.enabled)
    if (approve) return `Review and approve to continue${currentTask ? ` (${currentTask.title})` : ''}.`
    return 'Approval actions are unavailable right now.'
  }

  if (summary === 'failed' || summary === 'blocked') {
    const retry = actions.find((a) => a.kind === 'retry' && a.enabled)
    if (retry) return 'Retry the failed attempt.'
    const resume = actions.find((a) => a.kind === 'resume' && a.enabled)
    if (resume) return 'Resume from where the workflow stopped.'
    const rerun = actions.find((a) => a.kind === 'rerun' && a.enabled)
    if (rerun) return 'Rerun the current stage.'
    const start = actions.find((a) => a.kind === 'start' && a.enabled)
    if (start) return 'Start a new workflow run, discarding the failed one.'
    if (currentTask) return `Investigate ${currentTask.title}.`
    return 'Inspect the failure and take action.'
  }

  return 'No user action required right now.'
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
  const waitReason = summary === 'queued' ? buildWaitReason(input) : null
  const isBacklog = issue?.status === 'backlog'
  const isClosed = issue?.status === 'cancelled'
  const isDone = summary === 'done'
  const actions = buildActions(summary, input, isBacklog, isClosed, isDone)
  const headline = buildHeadline(summary, currentTask, issue)
  const rationale = buildRationale(summary, input, waitReason)
  const nextAction = buildNextAction(summary, actions, currentTask, waitReason)
  const driftNote = buildDriftNote(input)
  const blockedReason = summary === 'failed' || summary === 'blocked'
    ? (issue?.blockedReason ?? null)
    : null

  return {
    summary,
    headline,
    rationale,
    currentTask,
    nextAction,
    actions,
    waitReason,
    driftNote,
    blockedReason,
  }
}

export type { RecoveryProjection }
