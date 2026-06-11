import { useEffect, useRef, useCallback, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { EventName, EventMap, Issue, LiveTaskState, RebaseConflictState } from '../../entities/issue'
import { dispatchAgentEvent, AGENT_DETAIL_EVENTS } from '../../entities/agent'
import type { AgentDetailEventMap } from '../../entities/agent'
import { dispatchRebaseEvent } from '../../entities/issue/model/rebase-events'
import { useProject } from '../../entities/project'
import { LiveTaskContext } from '../../entities/issue'
import { useEventsConnection } from '../../shared/api/events-hub'
import { EVENT_TYPES, REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'

/**
 * Compile-time guard: every name the switch can route (i.e. every key of
 * `EventMap`) must be in the canonical subscription set. The reverse-DNS
 * names are added to `EventMap` in `entities/issue/@x/events.ts` and the
 * `EVENT_TYPES` constant is the union of legacy + reverse-DNS names. If a
 * new switch arm is added without adding its event type to `EVENT_TYPES`,
 * the assignment below will fail to typecheck.
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

const LIVE_TIMER_INTERVAL = 500

type AgentDetailEventName = keyof AgentDetailEventMap

function isAgentDetailEvent(name: string): name is AgentDetailEventName {
  return (AGENT_DETAIL_EVENTS as readonly string[]).includes(name)
}

function routeTranscriptEventName(name: string): string {
  switch (name) {
    case 'agent_message':
    case 'agent_message_chunk':
      return 'coder_text_chunk'
    case 'agent_thought':
    case 'agent_thought_chunk':
      return 'coder_thought_chunk'
    case 'tool_call':
    case 'tool_call_update':
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

function normalizeTranscriptDetail(
  candidate: Record<string, unknown>,
  eventName: string,
  innerPayload?: Record<string, unknown>,
): Record<string, unknown> {
  const issueNumber = readEnvelopeField(candidate, 'issueNumber', 'IssueNumber')
    ?? readEnvelopeField(candidate, 'issueNo', 'IssueNo')
  const agentSessionId = readEnvelopeField(candidate, 'agentSessionId', 'AgentSessionId')
  const sessionId = readEnvelopeField(candidate, 'sessionId', 'SessionId')
  const workId = readEnvelopeField(candidate, 'workId', 'WorkId')
  const normalized: Record<string, unknown> = {
    ...candidate,
    ...(innerPayload ?? {}),
    type: eventName,
  }
  if (innerPayload) {
    normalized.payload = innerPayload
  }
  if (normalized.issueId === undefined && issueNumber !== undefined) {
    normalized.issueId = String(issueNumber)
  }
  if (normalized.issueNumber === undefined && issueNumber !== undefined) {
    normalized.issueNumber = issueNumber
  }
  if (normalized.acpSessionId === undefined) {
    normalized.acpSessionId = agentSessionId ?? sessionId
  }
  if (normalized.executionId === undefined) {
    normalized.executionId = workId
  }
  if (normalized.coderSessionId === undefined) {
    normalized.coderSessionId = sessionId
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

export const __testing__ = { unwrapEnvelope, unwrapTranscriptEnvelope, routeTranscriptEventName }


function getCurrentIssueNumber(): number | null {
  const match = window.location.pathname.match(/\/issue\/(\d+)/)
  return match ? parseInt(match[1], 10) : null
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
  const [activeTaskId, setActiveTaskId] = useState<string | null>(null)
  const [activeTaskElapsedMs, setActiveTaskElapsedMs] = useState<number | null>(null)
  const [rebaseConflict, setRebaseConflict] = useState<RebaseConflictState | null>(null)
  const taskStartRef = useRef<number | null>(null)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const viewedIssueRef = useRef<number | null>(getCurrentIssueNumber())

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

  const clearLiveTimer = useCallback(() => {
    if (timerRef.current !== null) {
      clearInterval(timerRef.current)
      timerRef.current = null
    }
    taskStartRef.current = null
  }, [])

  const handleEvent = useCallback(
    (eventName: string, rawData: unknown, options?: { dispatchAgentDetail?: boolean }) => {
      try {
        const parsed = unwrapEnvelope(rawData)

        if (options?.dispatchAgentDetail !== false && isAgentDetailEvent(eventName)) {
          dispatchAgentEvent(eventName, parsed as AgentDetailEventMap[typeof eventName])
        }

        if (eventName === 'ralph_task_update') {
          const taskEvt = parsed as AgentDetailEventMap['ralph_task_update']
          if (taskEvt.status === 'started') {
            clearLiveTimer()
            taskStartRef.current = Date.now()
            setActiveTaskId(taskEvt.taskId)
            setActiveTaskElapsedMs(0)
            timerRef.current = setInterval(() => {
              if (taskStartRef.current !== null) {
                setActiveTaskElapsedMs(Date.now() - taskStartRef.current)
              }
            }, LIVE_TIMER_INTERVAL)
          } else if (taskEvt.status === 'completed' || taskEvt.status === 'failed') {
            clearLiveTimer()
            setActiveTaskId(taskEvt.taskId)
            setActiveTaskElapsedMs(null)
          }
        }

        if (
          eventName === 'coder_text_chunk' ||
          eventName === 'coder_tool_call' ||
          eventName === 'ralph_task_update' ||
          eventName === 'ralph_loop_progress' ||
          eventName === 'coder_session_started' ||
          eventName === 'coder_session_completed' ||
          eventName === 'coder_session_failed' ||
          eventName === 'coder_session_cancelled' ||
          eventName === 'coder_session_status_changed' ||
          eventName === 'agent_liveness_status' ||
          eventName === 'agent_usage_update'
        ) {
          queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
        }

        switch (eventName as EventName) {
          case 'stage_changed':
          case REVERSE_DNS_EVENT_TYPES.StageStarted:
          case REVERSE_DNS_EVENT_TYPES.StageCompleted:
          case REVERSE_DNS_EVENT_TYPES.StageFailed: {
            if (handleReverseDnsIntegrationOutcome(eventName, parsed, queryClient, setRebaseConflict)) {
              break
            }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'comment_added': {
            const { issueId } = parsed as EventMap['comment_added']
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (issueId) {
              queryClient.invalidateQueries({ queryKey: ['issues', 'detail', issueId] })
            }
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
          case 'agent_started':
          case 'agent_completed':
          case 'agent_paused':
          case 'agent_error':
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying:
          case REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionStarted:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionActivated:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionCompleted:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionStatusChanged: {
            if (handleReverseDnsIntegrationOutcome(eventName, parsed, queryClient, setRebaseConflict)) {
              break
            }
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (
              eventName === 'agent_paused' ||
              eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused
            ) {
              const evt = parsed as EventMap['agent_paused']
              notifyRunLifecycleToast(queryClient, viewedIssueRef.current, evt.issueId, 'pause')
            } else if (
              eventName === 'agent_error' ||
              eventName === REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed
            ) {
              const evt = parsed as EventMap['agent_error']
              notifyRunLifecycleToast(queryClient, viewedIssueRef.current, evt.issueId, 'error')
            }
            break
          }
          case REVERSE_DNS_EVENT_TYPES.AgentSessionFailed:
          case REVERSE_DNS_EVENT_TYPES.AgentSessionCancelled: {
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            const evt = parsed as {
              issueId: string
              projectId: string
              reason?: string
            }
            notifyRunLifecycleToast(
              queryClient,
              viewedIssueRef.current,
              evt.issueId,
              'error',
            )
            break
          }
          case 'agent_blocked': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            break
          }
          case 'approval_requested':
          case REVERSE_DNS_EVENT_TYPES.StageApprovalRequested: {
            const evt = parsed as EventMap['approval_requested'] & { issueNumber?: number }
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
          case 'merge_queued':
          case 'merge_started':
          case 'merge_completed':
          case 'merge_failed': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (eventName === 'merge_completed') {
              const d = parsed as EventMap['merge_completed']
              toast.success(`Issue #${d.issueNumber} merged successfully`)
            } else if (eventName === 'merge_failed') {
              const d = parsed as EventMap['merge_failed']
              toast.error(`Merge failed for Issue #${d.issueNumber}`)
            }
            break
          }
          case 'rebase_started': {
            const d = parsed as EventMap['rebase_started']
            dispatchRebaseEvent({ type: 'rebase_started', issueNumber: d.issueNumber })
            break
          }
          case 'rebase_progress': {
            const d = parsed as EventMap['rebase_progress']
            dispatchRebaseEvent({
              type: 'rebase_progress',
              issueNumber: d.issueNumber,
              step: d.step,
            })
            break
          }
          case 'rebase_completed': {
            const d = parsed as EventMap['rebase_completed']
            setRebaseConflict(null)
            dispatchRebaseEvent({
              type: 'rebase_completed',
              issueNumber: d.issueNumber,
              rebased: d.rebased,
            })
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'rebase_conflict': {
            const d = parsed as EventMap['rebase_conflict']
            if (d.status === 'resolving' || d.status === 'failed') {
              setRebaseConflict({ issueNumber: d.issueNumber, conflicts: d.conflicts, status: d.status, error: d.error })
            } else {
              setRebaseConflict(null)
            }
            dispatchRebaseEvent({
              type: 'rebase_conflict',
              issueNumber: d.issueNumber,
              conflicts: d.conflicts,
              status: d.status,
              error: d.error,
            })
            if (d.status === 'failed') {
              toast.error(`Rebase conflict on Issue #${d.issueNumber}`)
            }
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'agent_conflict_resolution_started': {
            const d = parsed as EventMap['agent_conflict_resolution_started']
            setRebaseConflict((prev) =>
              prev && prev.issueNumber === d.issueNumber
                ? { ...prev, status: 'resolving' }
                : prev,
            )
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'agent_conflict_resolution_completed': {
            const d = parsed as EventMap['agent_conflict_resolution_completed']
            setRebaseConflict((prev) =>
              prev && prev.issueNumber === d.issueNumber
                ? { ...prev, status: 'resolving' }
                : prev,
            )
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'agent_conflict_resolution_failed': {
            const d = parsed as EventMap['agent_conflict_resolution_failed']
            setRebaseConflict((prev) =>
              prev && prev.issueNumber === d.issueNumber
                ? { ...prev, status: 'failed', error: d.error }
                : prev,
            )
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'check_started':
          case 'check_update':
          case 'check_suite_status_changed': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'stage_task_update': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            break
          }
          case 'base_drift_detected':
          case 'rebase_opportunity': {
            const d = parsed as EventMap['base_drift_detected'] | EventMap['rebase_opportunity']
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if ('issueNumber' in d && d.issueNumber) {
              queryClient.invalidateQueries({ queryKey: ['issues', d.issueNumber] })
            }
            if (eventName === 'base_drift_detected') {
              const driftEvt = d as EventMap['base_drift_detected']
              if (driftEvt.decision === 'needs-attention') {
                toast.warning(`Issue #${driftEvt.issueNumber} needs attention before continuing`)
              }
            }
            break
          }
          case 'user_attention_requested': {
            const d = parsed as EventMap['user_attention_requested']
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (d.issueNumber) {
              queryClient.invalidateQueries({ queryKey: ['issues', d.issueNumber] })
              toast.info(`Issue #${d.issueNumber}: ${d.reason}`)
            }
            break
          }
        }
      } catch {
        // ignore malformed events
      }
    },
    [queryClient, clearLiveTimer],
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

  useEffect(() => {
    return () => {
      clearLiveTimer()
    }
  }, [clearLiveTimer])

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
