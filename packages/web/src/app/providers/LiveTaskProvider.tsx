import { useEffect, useRef, useCallback, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { EventName, Issue, LiveTaskState, RebaseConflictState } from '../../entities/issue'
import { dispatchAgentEvent, AGENT_DETAIL_EVENTS, useAgentStatus } from '../../entities/agent'
import type { AgentDetailEventMap } from '../../entities/agent'
import { dispatchRebaseEvent } from '../../entities/issue/model/rebase-events'
import { dispatchTimelineEvent, type TimelineLiveEvent } from '../../entities/issue/model/timeline-events'
import { useProject } from '../../entities/project'
import { LiveTaskContext } from '../../entities/issue'
import { useEventsConnection } from '../../shared/api/events-hub'
import { EVENT_TYPES, REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { useRuntimeToast } from '../../shared/ui/toast'

/**
 * Compile-time guard: every name the switch can route (i.e. every key of
 * `EventMap`) must be in the canonical subscription set. The reverse-DNS
 * names are added to `EventMap` in `entities/issue/@x/events.ts` and the
 * `EVENT_TYPES` constant is the union of agent-detail, transcript, and
 * reverse-DNS names. If a new switch arm is added without adding its event
 * type to `EVENT_TYPES`, the assignment below will fail to typecheck.
 *
 * Note: the check uses `[T] extends [never]` rather than `T extends never`
 * because TypeScript collapses `Exclude<..., string[]>` to `never` even
 * when the result is non-empty; wrapping in a tuple prevents the collapse
 * and gives a meaningful conditional check.
 */
type _AssertEventNameSubscribed = [Exclude<EventName, (typeof EVENT_TYPES)[number]>] extends [never]
  ? true
  : false
const _subscriptionCoversSwitch: _AssertEventNameSubscribed = true
void _subscriptionCoversSwitch

type AgentDetailEventName = keyof AgentDetailEventMap

function isAgentDetailEvent(name: string): name is AgentDetailEventName {
  return (AGENT_DETAIL_EVENTS as readonly string[]).includes(name)
}

/**
 * Map transcript event names to the legacy detail-event names that UI surfaces
 * (timeline, activity, session transcript) subscribe to. The producer emits the
 * canonical transcript names (`message.delta`, `reasoning.delta`,
 * `tool_call.*`); consumers still listen for the legacy `coder_*` detail
 * names, so we translate here. Names without a translation (e.g.
 * `session.input`, `session.closed`) pass through unchanged.
 */
function routeTranscriptEventName(name: string): string {
  switch (name) {
    case 'message.delta':
      return 'coder_text_chunk'
    case 'reasoning.delta':
      return 'coder_thought_chunk'
    case 'tool_call.started':
    case 'tool_call.updated':
    case 'tool_call.completed':
      return 'coder_tool_call'
    default:
      return name
  }
}

/**
 * Wire shape from the SignalR bus. The server now sends the full CloudEvents
 * 1.0.2 envelope; the Web reads {@link payload} for the original event body
 * and {@link extensions} for routing metadata (projectid, workflowrunid,
 * issueno). Falls back to the legacy raw-payload shape (where the event
 * body sits in a top-level `payload` field) for any unmigrated producers.
 *
 * Note on field casing: the server-side `CloudEventEnvelope` record uses
 * PascalCase property names (SpecVersion, DataContentType, ...) when
 * serialised by System.Text.Json, so the wire JSON has `specVersion`,
 * not the CloudEvents-spec lowercase `specversion`. The structural
 * check here matches what the server actually emits.
 */
function unwrapEnvelope(rawData: unknown): Record<string, unknown> {
  if (!rawData || typeof rawData !== 'object') {
    return {}
  }
  const candidate = rawData as Record<string, unknown>
  // CloudEvents envelope marker: id + source + type + specVersion all
  // present as strings. duck-typing on 'payload' alone would mis-parse
  // any future event whose data payload happens to contain a nested
  // 'payload' field.
  if (
    typeof candidate.specVersion === 'string'
    && typeof candidate.id === 'string'
    && typeof candidate.source === 'string'
    && typeof candidate.type === 'string'
  ) {
    const payload = candidate.payload ?? candidate.data
    if (payload && typeof payload === 'object') {
      return payload as Record<string, unknown>
    }
    return {}
  }
  // Legacy raw-payload shape (unmigrated producers).
  if (typeof candidate.type === 'string' && 'payload' in candidate) {
    const payload = candidate.payload
    if (payload && typeof payload === 'object') {
      return payload as Record<string, unknown>
    }
    return {}
  }
  return candidate
}

function readEnvelopeField(candidate: Record<string, unknown>, camelCase: string, pascalCase: string): unknown {
  return candidate[camelCase] ?? candidate[pascalCase]
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

function normalizeToolState(value: unknown, eventName: string): string | undefined {
  if (typeof value === 'string' && value) {
    switch (value) {
      case 'completed':
      case 'failed':
      case 'timeout':
      case 'cancelled':
      case 'started':
        return value
      case 'running':
      case 'in_progress':
      case 'pending':
        return 'started'
      default:
        return value
    }
  }
  if (eventName === 'tool_call.completed') return 'completed'
  if (eventName === 'tool_call.started') return 'started'
  return undefined
}

function normalizeTranscriptDetail(
  candidate: Record<string, unknown>,
  eventName: string,
  innerPayload?: Record<string, unknown>,
): Record<string, unknown> {
  const agentSessionId = readEnvelopeField(candidate, 'agentSessionId', 'AgentSessionId')
  const sessionId = readEnvelopeField(candidate, 'sessionId', 'SessionId')
  const workId = readEnvelopeField(candidate, 'workId', 'WorkId')
  const normalized: Record<string, unknown> = {
    ...candidate,
    ...(innerPayload ?? {}),
    type: eventName,
  }
  const toolCall = asRecord(normalized.toolCall)
  if (toolCall) {
    normalized.toolCallId ??= toolCall.toolCallId ?? toolCall.id
    normalized.toolName ??= toolCall.toolName ?? toolCall.name
    normalized.title ??= toolCall.title
    normalized.rawInput ??= toolCall.input ?? toolCall.rawInput
    normalized.rawOutput ??= toolCall.output ?? toolCall.rawOutput
    normalized.rawOutputMetadata ??= toolCall.outputMetadata ?? toolCall.rawOutputMetadata
    normalized.metadata ??= toolCall.metadata
    normalized.details ??= toolCall.details
    normalized.normalizedName ??= toolCall.normalizedName
    normalized.displayTitle ??= toolCall.displayTitle
    normalized.displaySubtitle ??= toolCall.displaySubtitle
    normalized.category ??= toolCall.category
  }
  if (eventName.startsWith('tool_call.')) {
    normalized.state = normalizeToolState(
      normalized.state ?? normalized.status ?? toolCall?.state ?? toolCall?.status,
      eventName,
    )
  }
  if (innerPayload) {
    normalized.payload = innerPayload
  }
  if (normalized.acpSessionId === undefined) {
    normalized.acpSessionId = agentSessionId ?? sessionId
  }
  if (normalized.coderSessionId === undefined) {
    normalized.coderSessionId = sessionId
  }
  if (normalized.executionId === undefined) {
    normalized.executionId = workId
  }
  return normalized
}

function unwrapTranscriptEnvelope(rawData: unknown): { eventName: string; payload: unknown; detail: unknown } | null {
  if (!rawData || typeof rawData !== 'object') {
    return null
  }
  const candidate = rawData as Record<string, unknown>
  const eventName = readEnvelopeField(candidate, 'type', 'Type')
    ?? readEnvelopeField(candidate, 'eventType', 'EventType')
    ?? readEnvelopeField(candidate, 'name', 'Name')
  if (typeof eventName !== 'string') {
    return null
  }
  const innerPayload = readEnvelopeField(candidate, 'payload', 'Payload') ?? readEnvelopeField(candidate, 'data', 'Data')
  const hasRuntimeRowMetadata = readEnvelopeField(candidate, 'sessionId', 'SessionId') !== undefined
    || readEnvelopeField(candidate, 'sequence', 'Sequence') !== undefined
    || readEnvelopeField(candidate, 'createdAt', 'CreatedAt') !== undefined
  if (hasRuntimeRowMetadata && innerPayload && typeof innerPayload === 'object') {
    const payload = innerPayload as Record<string, unknown>
    return {
      eventName,
      payload,
      detail: normalizeTranscriptDetail(candidate, eventName, payload),
    }
  }
  return {
    eventName,
    payload: candidate,
    detail: normalizeTranscriptDetail(candidate, eventName),
  }
}

export const __testing__ = { unwrapEnvelope, unwrapTranscriptEnvelope, routeTranscriptEventName, buildTimelineLiveEvent }


function getCurrentIssueNumber(): number | null {
  const match = window.location.pathname.match(/\/issue\/(\d+)/)
  return match ? parseInt(match[1], 10) : null
}

/**
 * Surface a runner-drop notice whenever `useAgentStatus()` transitions to
 * `runnerAvailable === false`. The notice is delivered through the runtime
 * toast host (and via the host's `onNotice` sink into Activity), never as
 * inline issue content.
 */
function useRunnerDropNotice(): void {
  const { data: agentStatus } = useAgentStatus()
  const toastCtx = useRuntimeToast()
  const lastSeen = useRef<boolean | null>(null)

  useEffect(() => {
    if (!agentStatus) return
    const next = agentStatus.runnerAvailable === false
    if (lastSeen.current === null) {
      lastSeen.current = next
      return
    }
    if (next === lastSeen.current) return
    lastSeen.current = next
    if (next) {
      toastCtx.push({
        tone: 'transport',
        title: 'Runner dropped',
        body: agentStatus.runnerMessage ?? 'The workflow runner is no longer reachable. Workflows will resume when it reconnects.',
        testId: 'runtime-toast-runner-dropped',
        ttlMs: 8_000,
      })
    } else {
      toastCtx.push({
        tone: 'transport',
        title: 'Runner reconnected',
        body: 'The workflow runner is back online.',
        testId: 'runtime-toast-runner-reconnected',
        ttlMs: 5_000,
      })
    }
  }, [agentStatus, toastCtx])
}

function findIssueNumber(
  queryClient: ReturnType<typeof useQueryClient>,
  issueId: string,
): number | null {
  const matches = queryClient.getQueriesData<Issue[]>({ queryKey: ['issues'] })
  for (const [, data] of matches) {
    if (Array.isArray(data)) {
      const found = data.find((i) => i.id === issueId)
      if (found) {
        return found.number
      }
    }
  }
  return null
}

function notifyRunLifecycleToast(
  queryClient: ReturnType<typeof useQueryClient>,
  viewedIssue: number | null,
  issueId: string,
  kind: 'pause' | 'error',
): void {
  const issueNumber = findIssueNumber(queryClient, issueId)
  if (issueNumber === null || issueNumber === viewedIssue) return
  if (kind === 'pause') {
    toast.info(`Issue #${issueNumber} needs approval`)
  } else {
    toast.error(`Issue #${issueNumber} encountered an error`)
  }
}

function notifyApprovalRequestedToast(
  queryClient: ReturnType<typeof useQueryClient>,
  viewedIssue: number | null,
  evt: { issueId?: string; issueNumber?: number },
): void {
  const issueNumber = evt.issueNumber ?? (evt.issueId ? findIssueNumber(queryClient, evt.issueId) : null)
  if (issueNumber === null || issueNumber === undefined || issueNumber === viewedIssue) return
  toast.info(`Issue #${issueNumber} needs approval`)
}

function readIssueNumber(parsed: Record<string, unknown>): number | null {
  const issueNumber = parsed.issueNumber ?? parsed.issueNo ?? parsed.number
  return typeof issueNumber === 'number' ? issueNumber : null
}

function readTimelineEventId(rawData: unknown): string | null {
  if (!rawData || typeof rawData !== 'object') return null
  const candidate = rawData as Record<string, unknown>
  const id = candidate.id ?? candidate.eventId
  return typeof id === 'string' && id ? id : null
}

function readTimelineTime(rawData: unknown, parsed: Record<string, unknown>): string | null {
  if (rawData && typeof rawData === 'object') {
    const candidate = rawData as Record<string, unknown>
    const t = candidate.time ?? candidate.Time
    if (typeof t === 'string' && t) return t
  }
  const fallback = parsed.time ?? parsed.createdAt ?? parsed.createdAtUtc ?? parsed.timestamp
  return typeof fallback === 'string' && fallback ? fallback : null
}

function buildTimelineLiveEvent(
  eventName: string,
  rawData: unknown,
  parsed: Record<string, unknown>,
): TimelineLiveEvent {
  return {
    issueNumber: readIssueNumber(parsed),
    issueId: typeof parsed.issueId === 'string' ? parsed.issueId : null,
    type: eventName,
    time: readTimelineTime(rawData, parsed),
    eventId: readTimelineEventId(rawData),
    payload: parsed,
  }
}

function readOutcome(parsed: Record<string, unknown>): string | null {
  const outcome = parsed.outcome ?? parsed.result ?? parsed.kind ?? parsed.operation ?? parsed.reason
  return typeof outcome === 'string' ? outcome : null
}

function isRebasePayload(parsed: Record<string, unknown>): boolean {
  const outcome = readOutcome(parsed)
  return outcome?.includes('rebase') === true || 'rebased' in parsed || 'conflicts' in parsed
}

function isMergePayload(parsed: Record<string, unknown>): boolean {
  const outcome = readOutcome(parsed)
  return outcome?.includes('merge') === true
}

function handleReverseDnsIntegrationOutcome(
  eventName: string,
  parsed: Record<string, unknown>,
  queryClient: ReturnType<typeof useQueryClient>,
  setRebaseConflict: React.Dispatch<React.SetStateAction<RebaseConflictState | null>>,
): boolean {
  const issueNumber = readIssueNumber(parsed)
  if (issueNumber === null) return false

  if (eventName === REVERSE_DNS_EVENT_TYPES.IssueWorkCompleted) {
    if (isRebasePayload(parsed)) {
      const rebased = typeof parsed.rebased === 'boolean' ? parsed.rebased : true
      setRebaseConflict(null)
      dispatchRebaseEvent({ type: 'rebase_completed', issueNumber, rebased })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      return true
    }
    if (isMergePayload(parsed)) {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success(`Issue #${issueNumber} merged successfully`)
      return true
    }
  }

  const isFailureEvent = eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed
    || eventName === REVERSE_DNS_EVENT_TYPES.StageFailed
  if (!isFailureEvent) return false

  if (isRebasePayload(parsed)) {
    const conflicts = Array.isArray(parsed.conflicts) ? parsed.conflicts.filter((x): x is string => typeof x === 'string') : []
    const error = typeof parsed.error === 'string' ? parsed.error : undefined
    setRebaseConflict({ issueNumber, conflicts, status: 'failed', error })
    dispatchRebaseEvent({ type: 'rebase_conflict', issueNumber, conflicts, status: 'failed', error })
    toast.error(`Rebase conflict on Issue #${issueNumber}`)
    queryClient.invalidateQueries({ queryKey: ['issues'] })
    return true
  }
  if (isMergePayload(parsed)) {
    queryClient.invalidateQueries({ queryKey: ['issues'] })
    toast.error(`Merge failed for Issue #${issueNumber}`)
    return true
  }
  return false
}

function useLiveEvents(projectId: string | null): LiveTaskState {
  const queryClient = useQueryClient()
  const [activeTaskId] = useState<string | null>(null)
  const [activeTaskElapsedMs] = useState<number | null>(null)
  const [rebaseConflict, setRebaseConflict] = useState<RebaseConflictState | null>(null)
  const viewedIssueRef = useRef<number | null>(getCurrentIssueNumber())
  // Subscribe to SignalR connection transitions so transport notices are
  // routed to the toast host / Activity surface instead of any inline issue
  // content. The hook itself owns publishing.
  useRunnerDropNotice()

  useEffect(() => {
    const update = () => {
      viewedIssueRef.current = getCurrentIssueNumber()
    }
    window.addEventListener('popstate', update)
    const origPush = history.pushState
    const origReplace = history.replaceState
    history.pushState = function (...args) {
      origPush.apply(this, args)
      update()
    }
    history.replaceState = function (...args) {
      origReplace.apply(this, args)
      update()
    }
    return () => {
      window.removeEventListener('popstate', update)
      history.pushState = origPush
      history.replaceState = origReplace
    }
  }, [])

  const handleEvent = useCallback(
    (eventName: string, rawData: unknown, options?: { dispatchAgentDetail?: boolean }) => {
      try {
        const parsed = unwrapEnvelope(rawData)

        if (options?.dispatchAgentDetail !== false && isAgentDetailEvent(eventName)) {
          dispatchAgentEvent(eventName, parsed as AgentDetailEventMap[typeof eventName])
        }

        if (
          eventName === 'message.delta' ||
          eventName === 'reasoning.delta' ||
          eventName === 'tool_call.started' ||
          eventName === 'tool_call.updated' ||
          eventName === 'tool_call.completed' ||
          eventName === 'coder_text_chunk' ||
          eventName === 'coder_thought_chunk' ||
          eventName === 'coder_tool_call' ||
          eventName === 'coder_session_started' ||
          eventName === 'coder_session_completed' ||
          eventName === 'coder_session_failed' ||
          eventName === 'coder_session_cancelled' ||
          eventName === 'coder_session_status_changed' ||
          eventName === 'session.liveness' ||
          eventName === 'usage.updated'
        ) {
          queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
        }

        switch (eventName as EventName) {
          case REVERSE_DNS_EVENT_TYPES.StageStarted:
          case REVERSE_DNS_EVENT_TYPES.StageCompleted:
          case REVERSE_DNS_EVENT_TYPES.StageFailed: {
            if (handleReverseDnsIntegrationOutcome(eventName, parsed, queryClient, setRebaseConflict)) {
              break
            }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case REVERSE_DNS_EVENT_TYPES.IssueCreated:
          case REVERSE_DNS_EVENT_TYPES.IssueClosed:
          case REVERSE_DNS_EVENT_TYPES.IssueArchived:
          case REVERSE_DNS_EVENT_TYPES.IssueUnarchived:
          case REVERSE_DNS_EVENT_TYPES.IssueReopened:
          case REVERSE_DNS_EVENT_TYPES.IssueWorkStarted:
          case REVERSE_DNS_EVENT_TYPES.IssueWorkCompleted:
          case REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged:
          case REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged:
          case REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded:
          case REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved: {
            if (handleReverseDnsIntegrationOutcome(eventName, parsed, queryClient, setRebaseConflict)) {
              break
            }
            const { issueId } = parsed as { issueId: string; projectId: string }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (issueId) {
              queryClient.invalidateQueries({ queryKey: ['issues', 'detail', issueId] })
            }
            break
          }
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged: {
            if (handleReverseDnsIntegrationOutcome(eventName, parsed, queryClient, setRebaseConflict)) {
              break
            }
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused) {
              const evt = parsed as { issueId: string }
              notifyRunLifecycleToast(queryClient, viewedIssueRef.current, evt.issueId, 'pause')
            } else if (eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed) {
              const evt = parsed as { issueId: string }
              notifyRunLifecycleToast(queryClient, viewedIssueRef.current, evt.issueId, 'error')
            }
            break
          }
          case REVERSE_DNS_EVENT_TYPES.StageApprovalRequested: {
            const evt = parsed as { issueId: string; projectId: string; issueNumber?: number }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            notifyApprovalRequestedToast(queryClient, viewedIssueRef.current, evt)
            break
          }
          case REVERSE_DNS_EVENT_TYPES.StageApprovalResolved: {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            break
          }
        }

        const timelineEvent = buildTimelineLiveEvent(eventName, rawData, parsed)
        dispatchTimelineEvent(timelineEvent)
      } catch {
        // ignore malformed events
      }
    },
    [queryClient],
  )

  const handleTranscriptEvent = useCallback(
    (rawData: unknown) => {
      const transcript = unwrapTranscriptEnvelope(rawData)
      if (!transcript) return
      const routedName = routeTranscriptEventName(transcript.eventName)
      if (isAgentDetailEvent(routedName)) {
        dispatchAgentEvent(
          routedName,
          transcript.detail as AgentDetailEventMap[typeof routedName],
        )
      }
      handleEvent(routedName, transcript.payload, { dispatchAgentDetail: false })
    },
    [handleEvent],
  )

  useEventsConnection(projectId, handleEvent, handleTranscriptEvent)

  return { activeTaskId, activeTaskElapsedMs, rebaseConflict }
}

export function LiveTaskProvider({ children }: { children: React.ReactNode }) {
  const { projectId } = useProject()
  const state = useLiveEvents(projectId)
  return (
    <LiveTaskContext.Provider value={state}>
      {children}
    </LiveTaskContext.Provider>
  )
}
