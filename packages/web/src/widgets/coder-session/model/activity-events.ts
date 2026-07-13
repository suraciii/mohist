import { useMemo } from 'react'
import { useAgentActivity, type AgentActivitySession, type AgentActivityWaiting } from '../../../entities/agent'
import { useProjectEvents, type ProjectEventDto } from '../../../entities/project'
import { useRunners, type RunnerStatusRow } from '../../../entities/runner'

export type ActivityEventType = 'issue-state' | 'workflow-stage' | 'agent-session' | 'runner' | 'failure'
export type ActivityAttention = 'failure' | 'approval' | 'blocked' | 'routine'

export interface ActivityEventTarget {
  path: string
  label: string
}

export interface ActivityEventTargets {
  primary?: ActivityEventTarget
  issue?: { number: number; label: string; path?: string }
  workflow?: { issueNumber?: number; label: string; path?: string }
  session?: { sessionId: string; label: string; isGeneric: boolean; path?: string }
  agent?: { agentId: string; agentName: string | null; label: string; path?: string }
  runner?: { runnerId: string; label: string; path?: string }
}

export interface ActivityEvent {
  id: string
  type: ActivityEventType
  attention: ActivityAttention
  time: string
  title: string
  description: string
  targets: ActivityEventTargets
  outcome?: 'completed' | 'failed'
}

export interface ActivityEventsInput {
  recordedEvents: ProjectEventDto[]
  sessions: AgentActivitySession[]
  waiting: AgentActivityWaiting[]
  runners: RunnerStatusRow[]
}

interface EventTypeInfo {
  label: string
  attention: ActivityAttention
}

const ISSUE_EVENT_TYPES: Record<string, EventTypeInfo> = {
  'com.mohist.issue.created': { label: 'created', attention: 'routine' },
  'com.mohist.issue.work-started': { label: 'moved to in progress', attention: 'routine' },
  'com.mohist.issue.completed': { label: 'completed', attention: 'routine' },
  'com.mohist.issue.cancelled': { label: 'cancelled', attention: 'routine' },
  'com.mohist.issue.reopened': { label: 'reopened', attention: 'routine' },
  'com.mohist.issue.archived': { label: 'archived', attention: 'routine' },
  'com.mohist.issue.unarchived': { label: 'unarchived', attention: 'routine' },
  'com.mohist.issue.labels-changed': { label: 'labels changed', attention: 'routine' },
  'com.mohist.issue.priority-changed': { label: 'priority changed', attention: 'routine' },
  'com.mohist.issue.draft-changed': { label: 'draft status changed', attention: 'routine' },
  'com.mohist.issue.prerequisite-added': { label: 'prerequisite added', attention: 'routine' },
  'com.mohist.issue.prerequisite-removed': { label: 'prerequisite removed', attention: 'routine' },
  'com.mohist.issue.workflow-profile-changed': { label: 'workflow profile changed', attention: 'routine' },
}

const WORKFLOW_EVENT_TYPES: Record<string, EventTypeInfo> = {
  'com.mohist.workflow.run.started': { label: 'started', attention: 'routine' },
  'com.mohist.workflow.run.completed': { label: 'completed', attention: 'routine' },
  'com.mohist.workflow.run.resumed': { label: 'resumed', attention: 'routine' },
  'com.mohist.workflow.run.retrying': { label: 'retrying', attention: 'routine' },
  'com.mohist.workflow.run.rerunning': { label: 'rerunning', attention: 'routine' },
  'com.mohist.workflow.run.failed': { label: 'failed', attention: 'failure' },
  'com.mohist.workflow.run.stopped': { label: 'stopped', attention: 'failure' },
  'com.mohist.workflow.run.paused': { label: 'paused', attention: 'blocked' },
  'com.mohist.workflow.stage.started': { label: 'started', attention: 'routine' },
  'com.mohist.workflow.stage.completed': { label: 'completed', attention: 'routine' },
  'com.mohist.workflow.stage.approval-resolved': { label: 'approval resolved', attention: 'routine' },
  'com.mohist.workflow.stage.failed': { label: 'failed', attention: 'failure' },
  'com.mohist.workflow.stage.approval-requested': { label: 'needs approval', attention: 'approval' },
  'com.mohist.workflow.feedback.requested': { label: 'feedback requested', attention: 'approval' },
  'com.mohist.workflow.task.started': { label: 'task started', attention: 'routine' },
  'com.mohist.workflow.task.completed': { label: 'task completed', attention: 'routine' },
  'com.mohist.workflow.task.failed': { label: 'task failed', attention: 'failure' },
  'com.mohist.workflow.check.started': { label: 'check started', attention: 'routine' },
  'com.mohist.workflow.check.passed': { label: 'check passed', attention: 'routine' },
  'com.mohist.workflow.check.failed': { label: 'check failed', attention: 'failure' },
  'com.mohist.workflow.check.pending': { label: 'check pending', attention: 'blocked' },
  'com.mohist.workflow.repair-scheduled': { label: 'repair scheduled', attention: 'routine' },
  'com.mohist.workflow.artifact.recorded': { label: 'artifact recorded', attention: 'routine' },
}

const AGENT_SESSION_EVENT_TYPES: Record<string, EventTypeInfo> = {
  coder_session_started: { label: 'started', attention: 'routine' },
  coder_session_completed: { label: 'completed', attention: 'routine' },
  coder_session_cancelled: { label: 'cancelled', attention: 'routine' },
  coder_session_failed: { label: 'failed', attention: 'failure' },
  coder_session_status_changed: { label: 'status changed', attention: 'routine' },
  'com.mohist.agent-session.runtime-bound': { label: 'runtime bound', attention: 'routine' },
  'com.mohist.agent-session.usage-recorded': { label: 'usage recorded', attention: 'routine' },
  'com.mohist.agent-session.model-changed': { label: 'model changed', attention: 'routine' },
  'com.mohist.agent-session.context-compacted': { label: 'context compacted', attention: 'routine' },
  'com.mohist.agent-session.context-health-updated': { label: 'context health updated', attention: 'routine' },
  'com.mohist.agent-session.context-exhausted': { label: 'context exhausted', attention: 'failure' },
  'session.closed': { label: 'closed', attention: 'routine' },
  'session.liveness': { label: 'liveness changed', attention: 'routine' },
}

const RUNNER_EVENT_TYPES: Record<string, EventTypeInfo> = {
  'com.mohist.runner.connected': { label: 'connected', attention: 'routine' },
  'com.mohist.runner.disconnected': { label: 'disconnected', attention: 'blocked' },
  'com.mohist.runner.heartbeat': { label: 'heartbeat received', attention: 'routine' },
}

const ATTENTION_ORDER: Record<ActivityAttention, number> = {
  failure: 0,
  approval: 1,
  blocked: 2,
  routine: 3,
}

const TYPE_ORDER: Record<ActivityEventType, number> = {
  failure: 0,
  'workflow-stage': 1,
  'issue-state': 2,
  'agent-session': 3,
  runner: 4,
}

const FALLBACK_EVENT_TIME = '1970-01-01T00:00:00.000Z'

function fromActivity(path: string): string {
  const separator = path.includes('?') ? '&' : '?'
  return `${path}${separator}from=activity`
}

function readString(data: Record<string, unknown> | null | undefined, keys: string[]): string | null {
  if (data == null) return null
  const normalizedKeys = new Set(keys.map((key) => key.toLowerCase()))
  for (const [key, value] of Object.entries(data)) {
    if (!normalizedKeys.has(key.toLowerCase())) continue
    if (typeof value === 'string' && value.length > 0) return value
    if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  }
  return null
}

function readIssueNumber(event: ProjectEventDto): number | null {
  if (event.issueNumber != null && Number.isFinite(event.issueNumber)) return event.issueNumber
  const raw = readString(event.data, ['issueNumber', 'issueNo', 'issue_number'])
    ?? readString(event.extensions, ['issueno', 'issueNumber', 'issueNo'])
    ?? event.subject
  if (!raw) return null
  const n = Number(raw)
  return Number.isFinite(n) ? n : null
}

function normalizeOrigin(origin: string): 'issue' | 'workflow-run' | 'agent-session' | 'epic' | null {
  const normalized = origin.toLowerCase().replaceAll('_', '-')
  if (normalized === 'issue') return 'issue'
  if (normalized === 'workflow-run' || normalized === 'workflowrun') return 'workflow-run'
  if (normalized === 'agent-session' || normalized === 'agentsession') return 'agent-session'
  if (normalized === 'epic') return 'epic'
  return null
}

function eventIdentity(event: ProjectEventDto): string {
  return `${event.origin}-${event.sourceAggregateKind}-${event.sourceAggregateId}-${event.id}-${event.type}`
}

function sessionPath(sessionId: string, issueNumber: number | null, isGeneric: boolean): string {
  return isGeneric
    ? fromActivity(`/agent-sessions/${encodeURIComponent(sessionId)}`)
    : fromActivity(`/issues/${issueNumber}/session/${encodeURIComponent(sessionId)}`)
}

function issueTarget(issueNumber: number): { number: number; label: string; path: string } {
  return {
    number: issueNumber,
    label: `Issue #${issueNumber}`,
    path: fromActivity(`/issues/${issueNumber}`),
  }
}

function workflowTarget(issueNumber: number): { issueNumber: number; label: string; path: string } {
  return {
    issueNumber,
    label: 'Workflow context',
    path: fromActivity(`/issues/${issueNumber}`),
  }
}

function agentTarget(agentId: string, agentName: string | null) {
  return {
    agentId,
    agentName,
    label: agentName ?? agentId,
    path: fromActivity(`/agents/${encodeURIComponent(agentId)}`),
  }
}

function runnerTarget(runnerId: string) {
  return {
    runnerId,
    label: `Runner ${runnerId}`,
    path: fromActivity(`/runners/${encodeURIComponent(runnerId)}`),
  }
}

function buildIssueEventEntry(event: ProjectEventDto): ActivityEvent | null {
  const info = ISSUE_EVENT_TYPES[event.type]
  if (!info) return null

  const issueNumber = readIssueNumber(event)
  const titleText = readString(event.data, ['title', 'Title']) ?? 'Issue'
  const title = issueNumber != null ? `Issue #${issueNumber} ${info.label}` : `${titleText} ${info.label}`
  const description = info.label

  const targets: ActivityEventTargets = {}
  if (issueNumber != null) {
    targets.issue = issueTarget(issueNumber)
    targets.primary = { path: targets.issue.path!, label: targets.issue.label }
  }

  return {
    id: `recorded-${eventIdentity(event)}`,
    type: 'issue-state',
    attention: info.attention,
    time: event.time,
    title,
    description,
    targets,
    outcome: event.type === 'com.mohist.issue.completed' ? 'completed' : undefined,
  }
}

function buildWorkflowEventEntry(event: ProjectEventDto): ActivityEvent | null {
  const info = WORKFLOW_EVENT_TYPES[event.type]
  if (!info) return null

  const stage = readString(event.data, ['stage', 'Stage'])
  const checkName = readString(event.data, ['checkName', 'CheckName'])
  const taskId = readString(event.data, ['taskId', 'TaskId'])
  const reason = readString(event.data, ['reason', 'Reason', 'message', 'Message'])
  const issueNumber = readIssueNumber(event)

  const eventType: ActivityEventType = info.attention === 'failure' ? 'failure' : 'workflow-stage'

  let title: string
  if (stage) {
    title = `Workflow stage ${stage} ${info.label}`
  } else if (checkName) {
    title = `Check ${checkName} ${info.label}`
  } else if (taskId) {
    title = `Task ${taskId} ${info.label}`
  } else {
    title = `Workflow run ${info.label}`
  }

  const description = reason ?? info.label

  const targets: ActivityEventTargets = {}
  if (issueNumber != null) {
    targets.issue = issueTarget(issueNumber)
    targets.primary = { path: targets.issue.path!, label: targets.issue.label }
    targets.workflow = workflowTarget(issueNumber)
  }

  return {
    id: `recorded-${eventIdentity(event)}`,
    type: eventType,
    attention: info.attention,
    time: event.time,
    title,
    description,
    targets,
    outcome: info.attention === 'failure' ? 'failed' : info.label === 'completed' ? 'completed' : undefined,
  }
}

function buildAgentSessionEventEntry(
  event: ProjectEventDto,
  sessionById: Map<string, AgentActivitySession>,
): ActivityEvent | null {
  const info = agentSessionEventInfo(event)
  if (!info) return null

  const sessionId = event.sourceAggregateId || readString(event.data, ['sessionId', 'coderSessionId']) || String(event.id)
  const session = sessionById.get(sessionId)
  const agentId = event.agentId ?? session?.agentId ?? readString(event.data, ['agentId'])
  const agentName = event.agentName ?? session?.agentName ?? readString(event.data, ['agentName'])
  const issueNumber = session && session.issueNumber > 0 ? session.issueNumber : readIssueNumber(event)
  const sourceKind = event.sessionSourceKind ?? (session?.agentId ? 'agent-launch' : session ? 'workflow' : null)
  const isGeneric = sourceKind === 'agent-launch' || (sourceKind == null && (agentId != null || issueNumber == null))

  let title = `Session ${sessionId} ${info.label}`
  const targets: ActivityEventTargets = {}
  if (isGeneric) {
    const displayName = agentName ?? agentId ?? 'Agent'
    title = `Agent ${displayName} session ${info.label}`
    targets.primary = { path: sessionPath(sessionId, null, true), label: 'Session' }
    targets.session = { sessionId, label: 'Session', isGeneric: true, path: sessionPath(sessionId, null, true) }
  } else if (issueNumber != null && issueNumber > 0) {
    title = `Issue #${issueNumber} session ${info.label}`
    targets.primary = { path: sessionPath(sessionId, issueNumber, false), label: 'Session' }
    targets.session = { sessionId, label: 'Session', isGeneric: false, path: sessionPath(sessionId, issueNumber, false) }
  }
  if (issueNumber != null && issueNumber > 0) targets.issue = issueTarget(issueNumber)
  if (event.workflowRunId && issueNumber != null && issueNumber > 0) targets.workflow = workflowTarget(issueNumber)
  if (agentId) targets.agent = agentTarget(agentId, agentName)
  if (event.runnerId) targets.runner = runnerTarget(event.runnerId)

  const eventType: ActivityEventType = info.attention === 'failure' ? 'failure' : 'agent-session'
  const failureCategory = readString(event.data, ['failureCategory', 'FailureCategory'])
  const failureReason = readString(event.data, ['failureReason', 'FailureReason', 'reason', 'Reason', 'message', 'Message'])
  const status = readString(event.data, ['status', 'Status'])
  const description = failureReason ?? failureCategory ?? status ?? info.label

  return {
    id: `recorded-${eventIdentity(event)}`,
    type: eventType,
    attention: info.attention,
    time: event.time,
    title,
    description,
    targets,
    outcome: info.attention === 'failure' ? 'failed' : info.label === 'completed' ? 'completed' : undefined,
  }
}

function agentSessionEventInfo(event: ProjectEventDto): EventTypeInfo | null {
  const base = AGENT_SESSION_EVENT_TYPES[event.type]
  if (!base) return null

  const status = readString(event.data, ['status'])?.toLowerCase()
  if (status === 'failed') {
    return { label: 'failed', attention: 'failure' }
  }
  if (event.type === 'coder_session_status_changed' && status) {
    return { label: status, attention: 'routine' }
  }
  if (event.type === 'session.closed' && status) {
    return { label: status, attention: 'routine' }
  }
  return base
}

function buildRunnerEventEntry(event: ProjectEventDto): ActivityEvent | null {
  const info = RUNNER_EVENT_TYPES[event.type]
  if (!info) return null

  const runnerId = event.runnerId
    ?? readString(event.data, ['runnerId', 'runner_id', 'runner'])
    ?? readString(event.extensions, ['runnerid', 'runnerId', 'runner_id', 'runner'])
    ?? (event.sourceAggregateKind === 'runner' ? event.sourceAggregateId : null)
  const title = runnerId ? `Runner ${runnerId} ${info.label}` : `Runner ${info.label}`
  const targets: ActivityEventTargets = {}
  if (runnerId) {
    const target = runnerTarget(runnerId)
    targets.runner = target
    targets.primary = { path: target.path, label: `Runner ${runnerId}` }
  }

  return {
    id: `recorded-${eventIdentity(event)}`,
    type: 'runner',
    attention: info.attention,
    time: event.time,
    title,
    description: info.label,
    targets,
  }
}

function buildSessionSnapshotEntry(session: AgentActivitySession): ActivityEvent {
  const isGeneric = session.agentId != null && session.agentId.length > 0
  const issueNumber = session.issueNumber > 0 ? session.issueNumber : null
  const status = session.status || 'unknown'
  const title = isGeneric
    ? `Agent ${session.agentName ?? session.agentId ?? 'session'} session ${status}`
    : issueNumber != null ? `Issue #${issueNumber} session ${status}` : `Session ${session.sessionId} ${status}`

  const targets: ActivityEventTargets = {}
  if (isGeneric) {
    targets.agent = agentTarget(session.agentId!, session.agentName ?? null)
    targets.primary = { path: sessionPath(session.sessionId, null, true), label: 'Session' }
    targets.session = { sessionId: session.sessionId, label: 'Session', isGeneric: true, path: sessionPath(session.sessionId, null, true) }
  } else if (issueNumber != null) {
    targets.issue = issueTarget(issueNumber)
    targets.primary = {
      path: sessionPath(session.sessionId, issueNumber, false),
      label: 'Session',
    }
    targets.session = { sessionId: session.sessionId, label: 'Session', isGeneric: false, path: sessionPath(session.sessionId, issueNumber, false) }
    targets.workflow = workflowTarget(issueNumber)
  }

  return {
    id: `session-snapshot-${session.sessionId}`,
    type: 'agent-session',
    attention: 'routine',
    time: session.lastActivityAt ?? session.createdAt,
    title,
    description: `Status: ${status}`,
    targets,
  }
}

function buildWaitingEntry(waiting: AgentActivityWaiting): ActivityEvent {
  const targets: ActivityEventTargets = {
    issue: issueTarget(waiting.issueNumber),
    primary: { path: issueTarget(waiting.issueNumber).path, label: `Issue #${waiting.issueNumber}` },
    workflow: workflowTarget(waiting.issueNumber),
  }

  return {
    id: `waiting-${waiting.issueId}`,
    type: 'workflow-stage',
    attention: 'approval',
    time: waiting.requestedAt ?? FALLBACK_EVENT_TIME,
    title: `Issue #${waiting.issueNumber} needs approval`,
    description: waiting.preview ?? 'Waiting for approval',
    targets,
  }
}

function buildRunnerSnapshotEntry(runner: RunnerStatusRow): ActivityEvent | null {
  if (runner.status === 'idle') return null

  const attention: ActivityAttention = runner.status === 'busy' ? 'routine' : 'blocked'
  const label = runner.status === 'busy' ? 'busy' : 'stale/offline'
  const targets: ActivityEventTargets = {
    runner: runnerTarget(runner.id),
    primary: { path: runnerTarget(runner.id).path, label: `Runner ${runner.id}` },
  }

  return {
    id: `runner-snapshot-${runner.id}`,
    type: 'runner',
    attention,
    time: runner.lastHeartbeatAt ?? runner.registeredAt ?? FALLBACK_EVENT_TIME,
    title: `Runner ${runner.id} ${label}`,
    description: `Runner is ${label}`,
    targets,
  }
}

export function buildActivityEvents(input: ActivityEventsInput): ActivityEvent[] {
  const sessionById = new Map<string, AgentActivitySession>()
  for (const session of input.sessions) {
    sessionById.set(session.sessionId, session)
  }

  const recorded: ActivityEvent[] = []
  for (const event of input.recordedEvents) {
    const runnerEntry = buildRunnerEventEntry(event)
    if (runnerEntry) {
      recorded.push(runnerEntry)
      continue
    }

    switch (normalizeOrigin(event.origin)) {
      case 'issue': {
        const entry = buildIssueEventEntry(event)
        if (entry) recorded.push(entry)
        break
      }
      case 'workflow-run': {
        const entry = buildWorkflowEventEntry(event)
        if (entry) recorded.push(entry)
        break
      }
      case 'agent-session': {
        const entry = buildAgentSessionEventEntry(event, sessionById)
        if (entry) recorded.push(entry)
        break
      }
      case 'epic':
        break
      default:
        break
    }
  }

  const snapshotEntries: ActivityEvent[] = []
  for (const session of input.sessions) {
    snapshotEntries.push(buildSessionSnapshotEntry(session))
  }
  for (const waiting of input.waiting) {
    snapshotEntries.push(buildWaitingEntry(waiting))
  }
  for (const runner of input.runners) {
    const entry = buildRunnerSnapshotEntry(runner)
    if (entry) snapshotEntries.push(entry)
  }

  const seen = new Set<string>()
  const deduplicated: ActivityEvent[] = []
  for (const entry of recorded.concat(snapshotEntries)) {
    if (seen.has(entry.id)) continue
    seen.add(entry.id)
    deduplicated.push(entry)
  }

  return sortActivityEvents(deduplicated)
}

export function sortActivityEvents(events: ActivityEvent[]): ActivityEvent[] {
  const sorted = [...events]
  sorted.sort((a, b) => {
    const attentionDiff = ATTENTION_ORDER[a.attention] - ATTENTION_ORDER[b.attention]
    if (attentionDiff !== 0) return attentionDiff
    const typeDiff = TYPE_ORDER[a.type] - TYPE_ORDER[b.type]
    if (typeDiff !== 0) return typeDiff
    const timeDiff = eventTime(b.time) - eventTime(a.time)
    if (timeDiff !== 0) return timeDiff
    return a.id.localeCompare(b.id)
  })

  return sorted
}

function eventTime(value: string): number {
  const time = Date.parse(value)
  return Number.isFinite(time) ? time : Number.NEGATIVE_INFINITY
}

export function useActivityEvents() {
  const { data: recordedEvents = [], isLoading: eventsLoading, isError: eventsError } = useProjectEvents()
  const { data: activity, isLoading: activityLoading, isError: activityError } = useAgentActivity()
  const { data: runners = [], isLoading: runnersLoading, isError: runnersError } = useRunners()

  const events = useMemo(() => {
    return buildActivityEvents({
      recordedEvents,
      sessions: activity?.sessions ?? [],
      waiting: activity?.waiting ?? [],
      runners,
    })
  }, [recordedEvents, activity, runners])

  return {
    events,
    isLoading: eventsLoading || activityLoading || runnersLoading,
    isError: eventsError || activityError || runnersError,
  }
}
