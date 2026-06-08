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

const LIVE_TIMER_INTERVAL = 500

type AgentDetailEventName = keyof AgentDetailEventMap

function isAgentDetailEvent(name: string): name is AgentDetailEventName {
  return (AGENT_DETAIL_EVENTS as readonly string[]).includes(name)
}

/**
 * Wire shape from the SignalR bus. The server now sends the full CloudEvents
 * 1.0.2 envelope; the Web reads {@link data} for the original event body
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
    if (candidate.data && typeof candidate.data === 'object') {
      return candidate.data as Record<string, unknown>
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

export const __testing__ = { unwrapEnvelope }


function getCurrentIssueNumber(): number | null {
  const match = window.location.pathname.match(/\/issue\/(\d+)/)
  return match ? parseInt(match[1], 10) : null
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
    (eventName: string, rawData: unknown) => {
      try {
        const parsed = unwrapEnvelope(rawData)

        if (isAgentDetailEvent(eventName)) {
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
          case 'stage_changed': {
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
          case 'agent_started':
          case 'agent_completed':
          case 'agent_paused':
          case 'agent_error': {
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            if (eventName === 'agent_paused' || eventName === 'agent_error') {
              const viewed = viewedIssueRef.current
              const evt = parsed as EventMap['agent_paused'] | EventMap['agent_error']
              const matches = queryClient.getQueriesData<Issue[]>({ queryKey: ['issues'] })
              let issueNumber: number | null = null
              for (const [, data] of matches) {
                if (Array.isArray(data)) {
                  const found = data.find((i) => i.id === evt.issueId)
                  if (found) {
                    issueNumber = found.number
                    break
                  }
                }
              }
              if (issueNumber !== null && issueNumber !== viewed) {
                if (eventName === 'agent_paused') {
                  toast.info(`Issue #${issueNumber} needs approval`)
                } else {
                  toast.error(`Issue #${issueNumber} encountered an error`)
                }
              }
            }
            break
          }
          case 'agent_blocked': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
            queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
            break
          }
          case 'approval_requested': {
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

  useEventsConnection(projectId, handleEvent)

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
