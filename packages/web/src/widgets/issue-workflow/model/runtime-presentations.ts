import { formatStageLabel } from './runtime-query-helpers'
import type {
  RuntimeActionKind,
  RuntimeAvailableAction,
  RuntimeCurrentTask,
  RuntimeDecisionInput,
  RuntimeSummary,
} from './runtime-types'

interface SummaryPresentationContext {
  input: RuntimeDecisionInput
  issue: RuntimeDecisionInput['issue']
  currentTask: RuntimeCurrentTask | null
  isBacklog: boolean
  isClosed: boolean
  isDone: boolean
  allowed: Set<string>
  waitReason: string | null
}

interface SummaryPresentation {
  headline: (ctx: SummaryPresentationContext) => string
  rationale: (ctx: SummaryPresentationContext) => string
  nextAction: (ctx: SummaryPresentationContext) => string
  actions: (ctx: SummaryPresentationContext) => RuntimeAvailableAction[]
}

function buildAllowedActions(input: RuntimeDecisionInput): Set<string> {
  const recoveryActions = input.issue?.recovery?.allowedActions ?? []
  const timelineActions = (input.timeline?.availableActions ?? []).map((action) => action.name)
  return new Set([...recoveryActions, ...timelineActions])
}

function actionEnabled(allowed: Set<string>, kind: RuntimeActionKind): boolean {
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
  return false
}

function buildRetryAction(ctx: SummaryPresentationContext): RuntimeAvailableAction {
  const offered = actionEnabled(ctx.allowed, 'retry')
  return {
    kind: 'retry',
    label: 'Retry',
    enabled: !ctx.isClosed && !ctx.isDone && offered,
    reason: offered ? undefined : 'Retry is not available right now.',
  }
}

function buildResumeAction(ctx: SummaryPresentationContext): RuntimeAvailableAction {
  const offered = actionEnabled(ctx.allowed, 'resume')
  return {
    kind: 'resume',
    label: 'Resume',
    enabled: !ctx.isClosed && !ctx.isDone && offered,
    reason: offered ? undefined : 'Resume is not available right now.',
  }
}

function buildRerunAction(ctx: SummaryPresentationContext): RuntimeAvailableAction {
  const offered = actionEnabled(ctx.allowed, 'rerun')
  return {
    kind: 'rerun',
    label: 'Rerun stage',
    enabled: !ctx.isClosed && !ctx.isDone && offered,
    reason: offered ? undefined : 'Rerun is not available right now.',
  }
}

function buildStopAction(ctx: SummaryPresentationContext): RuntimeAvailableAction {
  const offered = actionEnabled(ctx.allowed, 'stop')
  return {
    kind: 'stop',
    label: 'Stop',
    enabled: !ctx.isClosed && !ctx.isDone && offered,
    reason: offered ? undefined : 'Stop becomes available between tasks.',
  }
}

function buildStartNewWorkflowAction(ctx: SummaryPresentationContext): RuntimeAvailableAction {
  const offered = actionEnabled(ctx.allowed, 'start')
  return {
    kind: 'start',
    label: 'Start new workflow',
    enabled: !ctx.isClosed && !ctx.isDone && offered,
    reason: offered ? undefined : 'Start is not available right now.',
  }
}

function terminalActions(ctx: SummaryPresentationContext, terminalKind: 'start' | 'stop'): RuntimeAvailableAction[] {
  return [
    buildRetryAction(ctx),
    buildResumeAction(ctx),
    buildRerunAction(ctx),
    terminalKind === 'start' ? buildStartNewWorkflowAction(ctx) : buildStopAction(ctx),
  ]
}

function buildApprovalActions(ctx: SummaryPresentationContext): RuntimeAvailableAction[] {
  const approveOffered = actionEnabled(ctx.allowed, 'approve')
  const sendBackOffered = actionEnabled(ctx.allowed, 'send-back')
  return [
    {
      kind: 'approve',
      label: 'Approve',
      enabled: !ctx.isClosed && approveOffered,
      reason: approveOffered ? undefined : 'Approval is not available right now.',
    },
    {
      kind: 'send-back',
      label: 'Send back',
      enabled: !ctx.isClosed && sendBackOffered,
      reason: sendBackOffered ? undefined : 'Send-back is not available right now.',
    },
  ]
}

function buildRunningActions(ctx: SummaryPresentationContext): RuntimeAvailableAction[] {
  return [buildStopAction(ctx)]
}

function buildDoneActions(): RuntimeAvailableAction[] {
  return []
}

function buildQueuedActions(ctx: SummaryPresentationContext): RuntimeAvailableAction[] {
  if (!ctx.isBacklog) return []
  const startOffered = actionEnabled(ctx.allowed, 'start') || (ctx.isBacklog && ctx.input.issue?.canStart === true)
  const startEnabled = !ctx.isClosed && startOffered && !ctx.waitReason
  const startReason = ctx.waitReason ?? (!startOffered ? 'Start is not available right now.' : undefined)
  return [
    {
      kind: 'start',
      label: 'Start',
      enabled: startEnabled,
      reason: startReason,
    },
  ]
}

const PRESENTATIONS: Record<RuntimeSummary, SummaryPresentation> = {
  running: {
    headline: (ctx) => {
      const stage = formatStageLabelForCtx(ctx)
      if (ctx.currentTask) {
        const label = ctx.currentTask.kind === 'check' ? 'Check' : 'Task'
        return `${label} running: ${ctx.currentTask.title}`
      }
      return `Workflow running (${stage})`
    },
    rationale: () => 'The workflow is currently executing.',
    nextAction: () => 'No user action required right now.',
    actions: buildRunningActions,
  },
  'recoverable-interrupted': {
    headline: (ctx) => {
      if (ctx.currentTask) return `Recoverable interruption: ${ctx.currentTask.title}`
      return `Workflow recovering (${formatStageLabelForCtx(ctx)})`
    },
    rationale: (ctx) =>
      ctx.issue?.attention?.message ??
      ctx.issue?.attention?.reasonCode ??
      'The runner was interrupted while active work was in progress.',
    nextAction: (ctx) => {
      const deadline = ctx.issue?.attention?.recoveryDeadlineAt
      return deadline
        ? `Wait for the original runner to recover before ${deadline}.`
        : 'Wait for the original runner to recover.'
    },
    actions: () => [],
  },
  queued: {
    headline: (ctx) => `Waiting to start (${formatStageLabelForCtx(ctx)})`,
    rationale: (ctx) => ctx.waitReason ?? 'The workflow is waiting to start.',
    nextAction: (ctx) => {
      const start = buildQueuedActions(ctx).find((a) => a.kind === 'start' && a.enabled)
      if (start) return 'Start the workflow.'
      return ctx.waitReason ?? 'Wait for prerequisites or runner capacity.'
    },
    actions: buildQueuedActions,
  },
  'approval-required': {
    headline: (ctx) => {
      if (ctx.currentTask) return `Approval pending on ${ctx.currentTask.title}`
      return `Approval pending at ${formatStageLabelForCtx(ctx)}`
    },
    rationale: () => 'The workflow is paused while an approval decision is pending.',
    nextAction: (ctx) => {
      const approve = buildApprovalActions(ctx).find((a) => a.kind === 'approve' && a.enabled)
      if (approve)
        return `An approval decision is needed to continue${ctx.currentTask ? ` (${ctx.currentTask.title})` : ''}.`
      return 'Approval actions are unavailable right now.'
    },
    actions: buildApprovalActions,
  },
  blocked: {
    headline: (ctx) => {
      if (ctx.currentTask) return `Blocked on ${ctx.currentTask.title}`
      return `Workflow blocked (${formatStageLabelForCtx(ctx)})`
    },
    rationale: (ctx) => {
      const issue = ctx.issue
      if (issue?.blockedReason) return issue.blockedReason
      if (issue?.convergence?.blockedReason) return issue.convergence.blockedReason
      const recovery = issue?.recovery
      if (
        recovery?.latestAttemptState === 'interrupted' ||
        issue?.workflowStatus?.toLowerCase() === 'interrupted' ||
        issue?.workflowStatus?.toLowerCase() === 'stopped'
      ) {
        return 'Execution stopped manually. Resume or rerun to continue.'
      }
      return 'The workflow is blocked and needs an action to continue.'
    },
    nextAction: (ctx) => {
      const actions = terminalActions(ctx, 'stop')
      return nextActionForTerminal(actions, ctx)
    },
    actions: (ctx) => terminalActions(ctx, 'stop'),
  },
  failed: {
    headline: (ctx) => {
      const stage = formatStageLabelForCtx(ctx)
      if (ctx.currentTask) {
        const label = ctx.currentTask.kind === 'check' ? 'Check' : 'Task'
        return `${label} failed: ${ctx.currentTask.title}`
      }
      return `Workflow failed (${stage})`
    },
    rationale: (ctx) => {
      const blocked = ctx.issue?.blockedReason
      if (blocked) return blocked
      return 'The latest attempt failed and is not recoverable without intervention.'
    },
    nextAction: (ctx) => {
      const actions = terminalActions(ctx, 'start')
      return nextActionForTerminal(actions, ctx)
    },
    actions: (ctx) => terminalActions(ctx, 'start'),
  },
  done: {
    headline: () => 'Workflow done',
    rationale: () => 'The workflow has completed.',
    nextAction: () => 'No further action required.',
    actions: buildDoneActions,
  },
  cancelled: {
    headline: () => 'Issue cancelled',
    rationale: () => 'This issue was cancelled and will not be delivered. Reopen it to resume work.',
    nextAction: () => 'Reopen the issue if it should be worked on again.',
    actions: buildDoneActions,
  },
}

function nextActionForTerminal(actions: RuntimeAvailableAction[], ctx: SummaryPresentationContext): string {
  const retry = actions.find((a) => a.kind === 'retry' && a.enabled)
  if (retry) return 'Retry the failed attempt.'
  const resume = actions.find((a) => a.kind === 'resume' && a.enabled)
  if (resume) return 'Resume from where the workflow stopped.'
  const rerun = actions.find((a) => a.kind === 'rerun' && a.enabled)
  if (rerun) return 'Rerun the current stage.'
  const start = actions.find((a) => a.kind === 'start' && a.enabled)
  if (start) return 'Start a new workflow run, discarding the failed one.'
  if (ctx.currentTask) return `Investigate ${ctx.currentTask.title}.`
  return 'Inspect the failure and take action.'
}

function formatStageLabelForCtx(ctx: SummaryPresentationContext): string {
  return formatStageLabel(ctx.issue?.workflowStage ?? null)
}

export interface PresentationContextInput {
  input: RuntimeDecisionInput
  isBacklog: boolean
  isClosed: boolean
  allowed: Set<string>
}

export function buildPresentationContext(input: RuntimeDecisionInput): PresentationContextInput {
  const issue = input.issue
  return {
    input,
    isBacklog: issue?.status === 'backlog',
    isClosed: issue?.status === 'cancelled',
    allowed: buildAllowedActions(input),
  }
}

export function resolveSummaryPresentation(
  ctx: PresentationContextInput,
  currentTask: RuntimeCurrentTask | null,
  summary: RuntimeSummary,
  waitReason: string | null,
): {
  headline: string
  rationale: string
  nextAction: string
  actions: RuntimeAvailableAction[]
} {
  const resolved: SummaryPresentationContext = {
    input: ctx.input,
    issue: ctx.input.issue,
    currentTask,
    isBacklog: ctx.isBacklog,
    isClosed: ctx.isClosed,
    isDone: summary === 'done',
    allowed: ctx.allowed,
    waitReason,
  }
  const presentation = PRESENTATIONS[summary]
  return {
    headline: presentation.headline(resolved),
    rationale: presentation.rationale(resolved),
    nextAction: presentation.nextAction(resolved),
    actions: presentation.actions(resolved),
  }
}
