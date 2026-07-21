import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import type { AgentStatus } from '../../../entities/agent'
import type { WorkflowRunSession } from '../../../entities/coder-session'
import type { RuntimeActionKind, RuntimeDecision, RuntimeAvailableAction } from '../../../widgets/issue-workflow'

export type IssueDecisionActionKind =
  | 'approve'
  | 'send-back'
  | 'retry'
  | 'resume'
  | 'rerun'
  | 'stop'
  | 'start'
  | 'mark-ready'
  | 'close'
  | 'mark-as-done'
  | 'ask-agent'
  | 'view-transcript'

export type IssueDecisionInteractionMode =
  | 'immediate'
  | 'confirmation'
  | 'feedback'
  | 'navigation'

export interface IssueDecisionAction {
  kind: IssueDecisionActionKind
  label: string
  pendingLabel: string
  enabled: boolean
  reason: string | null
  primary: boolean
  destructive: boolean
  mode: IssueDecisionInteractionMode
  to: string | null
  order: number
}

export interface IssueDecisionSessionSelection {
  sessionName: string
  transcriptPath: string
}

export interface IssueDecisionContextInput {
  decision: RuntimeDecision | null
  issue: Pick<
    Issue,
    | 'number'
    | 'status'
    | 'workflowStatus'
    | 'health'
    | 'isDraft'
    | 'canStart'
    | 'workflowStage'
    | 'workflowRunId'
    | 'archivedAt'
    | 'children'
    | 'childIssuesSummary'
    | 'blocker'
  >
  agentStatus: Pick<AgentStatus, 'runnerAvailable' | 'runnerMessage' | 'capacity' | 'activeAgents'> | null
  workflowSessions: ReadonlyArray<Pick<WorkflowRunSession, 'sessionName' | 'status' | 'startedAt' | 'createdAt'>>
  projectPath: (path: string) => string
}

const TRANSCRIPT_DEFAULT_LABEL = 'View transcript'

const PENDING_LABELS: Partial<Record<IssueDecisionActionKind, string>> = {
  approve: 'Approving...',
  'send-back': 'Sending back...',
  retry: 'Retrying...',
  resume: 'Resuming...',
  rerun: 'Rerunning stage...',
  stop: 'Stopping...',
  start: 'Starting...',
  'mark-ready': 'Marking ready...',
  close: 'Closing...',
  'mark-as-done': 'Marking done...',
  'ask-agent': 'Opening agent composer...',
  'view-transcript': 'Opening transcript...',
}

function pendingLabelFor(kind: IssueDecisionActionKind, fallback: string): string {
  return PENDING_LABELS[kind] ?? fallback
}

function sessionTimestamp(session: Pick<WorkflowRunSession, 'startedAt' | 'createdAt'>): number {
  if (session.startedAt) {
    const ts = Date.parse(session.startedAt)
    if (!Number.isNaN(ts)) return ts
  }
  const created = Date.parse(session.createdAt)
  return Number.isNaN(created) ? 0 : created
}

const ACTIVE_STATUSES = new Set(['active', 'running', 'probing'])
const ACTIVE_PRIORITY: Record<string, number> = {
  active: 0,
  running: 1,
  probing: 2,
}

function activePriority(status: string): number {
  if (ACTIVE_STATUSES.has(status)) {
    return ACTIVE_PRIORITY[status] ?? ACTIVE_STATUSES.size
  }
  return Number.MAX_SAFE_INTEGER
}

export function selectTranscriptSession(
  sessions: ReadonlyArray<Pick<WorkflowRunSession, 'sessionName' | 'status' | 'startedAt' | 'createdAt'>>,
): Pick<WorkflowRunSession, 'sessionName'> | null {
  if (sessions.length === 0) return null
  let best: { session: Pick<WorkflowRunSession, 'sessionName' | 'status' | 'startedAt' | 'createdAt'>; activeRank: number; ts: number } | null = null
  for (const session of sessions) {
    const activeRank = activePriority(session.status)
    const ts = sessionTimestamp(session)
    if (!best) {
      best = { session, activeRank, ts }
      continue
    }
    if (activeRank < best.activeRank) {
      best = { session, activeRank, ts }
      continue
    }
    if (activeRank === best.activeRank) {
      if (ts > best.ts) {
        best = { session, activeRank, ts }
        continue
      }
      if (ts === best.ts && session.sessionName.localeCompare(best.session.sessionName) < 0) {
        best = { session, activeRank, ts }
      }
    }
  }
  return best ? { sessionName: best.session.sessionName } : null
}

function isAgentRunningOnThis(issue: IssueDecisionContextInput['issue'], agentStatus: IssueDecisionContextInput['agentStatus']): boolean {
  const activeAgents = agentStatus?.activeAgents ?? []
  return activeAgents.some((agent: { issueNumber: number }) => agent.issueNumber === issue.number)
}

function buildWaitReason(input: IssueDecisionContextInput): string | null {
  const blocker = input.issue.blocker
  if (blocker?.kind === 'draft') {
    return 'Mark the issue ready before starting.'
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

function isArchivedIssue(issue: IssueDecisionContextInput['issue']): boolean {
  return !!issue.archivedAt
}

function isTerminalIssueStatus(issue: IssueDecisionContextInput['issue']): boolean {
  return issue.status === IssueStatus.Done || issue.status === IssueStatus.Cancelled
}

function hasChildIssues(issue: IssueDecisionContextInput['issue']): boolean {
  if (issue.children && issue.children.length > 0) return true
  const summary = issue.childIssuesSummary
  return !!summary && summary.count > 0
}

function isWorkflowClosed(issue: IssueDecisionContextInput['issue']): boolean {
  return issue.status === IssueStatus.Done || issue.status === IssueStatus.Cancelled
}

function copyWorkflowAction(
  action: RuntimeAvailableAction,
  primary: boolean,
  order: number,
): IssueDecisionAction {
  const kind = action.kind as IssueDecisionActionKind
  return {
    kind,
    label: action.label,
    pendingLabel: pendingLabelFor(kind, action.label),
    enabled: action.enabled,
    reason: action.reason ?? null,
    primary,
    destructive: kind === 'stop' || kind === 'send-back',
    mode: kind === 'send-back' ? 'feedback' : kind === 'stop' ? 'confirmation' : 'immediate',
    to: null,
    order,
  }
}

function lifecycleAction(
  kind: IssueDecisionActionKind,
  label: string,
  enabled: boolean,
  reason: string | null,
  order: number,
): IssueDecisionAction {
  return {
    kind,
    label,
    pendingLabel: pendingLabelFor(kind, label),
    enabled,
    reason,
    primary: false,
    destructive: kind === 'close' || kind === 'stop',
    mode: kind === 'close' || kind === 'stop' ? 'confirmation' : 'immediate',
    to: null,
    order,
  }
}

export function deriveIssueDecisionActions(input: IssueDecisionContextInput): {
  actions: IssueDecisionAction[]
  primary: IssueDecisionAction | null
  transcript: IssueDecisionSessionSelection | null
} {
  const actions: IssueDecisionAction[] = []
  const decision = input.decision
  const issue = input.issue
  const issueArchived = isArchivedIssue(issue)
  const issueTerminal = isTerminalIssueStatus(issue)
  const workflowClosed = isWorkflowClosed(issue)
  const compositeParent = hasChildIssues(issue)
  const agentOnThis = isAgentRunningOnThis(issue, input.agentStatus)

  if (decision && !issueArchived) {
    const workflowActionKinds: ReadonlySet<IssueDecisionActionKind> = new Set([
      'approve',
      'send-back',
      'retry',
      'resume',
      'rerun',
      'stop',
      'start',
    ])
    const primaryKind = (decision.primary?.kind ?? null) as IssueDecisionActionKind | null
    const orderedRuntimeKinds: IssueDecisionActionKind[] = []
    if (primaryKind && workflowActionKinds.has(primaryKind)) {
      orderedRuntimeKinds.push(primaryKind)
    }
    for (const action of decision.actions) {
      const kind = action.kind as RuntimeActionKind
      const issueKind = kind as IssueDecisionActionKind
      if (workflowActionKinds.has(issueKind) && !orderedRuntimeKinds.includes(issueKind)) {
        orderedRuntimeKinds.push(issueKind)
      }
    }

    orderedRuntimeKinds.forEach((kind, idx) => {
      const runtimeAction = kind === primaryKind
        ? decision.primary
        : decision.actions.find((a) => a.kind === kind)
      if (!runtimeAction) return
      actions.push(copyWorkflowAction(runtimeAction, kind === primaryKind, idx))
    })
  }

  let order = actions.length > 0 ? actions.length : 0
  const markReadyEnabled = !!issue.isDraft && !issueArchived && !issueTerminal
  if (markReadyEnabled) {
    actions.push(lifecycleAction(
      'mark-ready',
      'Mark ready',
      true,
      null,
      order++,
    ))
  }

  const closeEnabled = !issueArchived
    && !issueTerminal
    && !agentOnThis
    && issue.health === IssueHealth.Active
  if (closeEnabled) {
    actions.push(lifecycleAction('close', 'Close', true, null, order++))
  }

  const markDoneEnabled = !issueArchived
    && !issueTerminal
    && !compositeParent
    && !agentOnThis
    && !workflowClosed
    && issue.status === IssueStatus.InProgress
    && (issue.workflowStatus === 'stopped' || issue.workflowStatus === 'completed')
  if (markDoneEnabled) {
    actions.push(lifecycleAction('mark-as-done', 'Mark as done', true, null, order++))
  }

  const askAgentEnabled = !issueArchived && !issueTerminal && issue.status !== IssueStatus.Backlog
  if (askAgentEnabled) {
    actions.push({
      kind: 'ask-agent',
      label: 'Ask Agent',
      pendingLabel: pendingLabelFor('ask-agent', 'Opening agent composer...'),
      enabled: true,
      reason: null,
      primary: false,
      destructive: false,
      mode: 'navigation',
      to: input.projectPath(`/agent-sessions/new?issue=${encodeURIComponent(issue.number)}`),
      order: order++,
    })
  }

  const transcriptSession = !issueArchived
    && !issueTerminal
    && (issue.workflowRunId ?? null) !== null
    && input.workflowSessions.length > 0
      ? selectTranscriptSession(input.workflowSessions)
      : null

  if (transcriptSession) {
    actions.push({
      kind: 'view-transcript',
      label: `${TRANSCRIPT_DEFAULT_LABEL} · ${transcriptSession.sessionName}`,
      pendingLabel: pendingLabelFor('view-transcript', 'Opening transcript...'),
      enabled: true,
      reason: null,
      primary: false,
      destructive: false,
      mode: 'navigation',
      to: input.projectPath(`/issues/${issue.number}/workflow/sessions/${encodeURIComponent(transcriptSession.sessionName)}`),
      order: order++,
    })
  }

  const queuedStartEnabled = decision === null
    && issue.status === IssueStatus.Backlog
    && !!issue.canStart
    && !issue.isDraft
    && !issueArchived
    && !issueTerminal
  if (queuedStartEnabled) {
    const waitReason = buildWaitReason(input)
    const startAction: IssueDecisionAction = {
      kind: 'start',
      label: 'Start',
      pendingLabel: pendingLabelFor('start', 'Starting...'),
      enabled: !waitReason,
      reason: waitReason,
      primary: true,
      destructive: false,
      mode: 'immediate',
      to: null,
      order: order++,
    }
    actions.unshift(startAction)
    for (let i = 1; i < actions.length; i += 1) {
      actions[i] = { ...actions[i], order: actions[i].order + 1 }
    }
  }

  const sortedActions = [...actions].sort((a, b) => a.order - b.order)
  const primary = sortedActions.find((action) => action.primary && action.enabled)
    ?? sortedActions.find((action) => action.primary)
    ?? sortedActions.find((action) => action.kind === 'ask-agent' || action.kind === 'view-transcript' || action.kind === 'mark-ready')
    ?? null

  return {
    actions: sortedActions,
    primary,
    transcript: transcriptSession
      ? {
          sessionName: transcriptSession.sessionName,
          transcriptPath: input.projectPath(`/issues/${issue.number}/workflow/sessions/${encodeURIComponent(transcriptSession.sessionName)}`),
        }
      : null,
  }
}

export const TRANSCRIPT_ACTION_LABEL_PREFIX = TRANSCRIPT_DEFAULT_LABEL
