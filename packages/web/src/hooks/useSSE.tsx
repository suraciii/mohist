import { useEffect, useRef, useCallback, useState, createContext, useContext } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { EventName, EventMap, LiveTaskState, RebaseConflictState, Issue } from '../lib/types'
import { dispatchAgentEvent, AGENT_DETAIL_EVENTS } from '../lib/agent-events'
import type { AgentDetailEventMap } from '../lib/types'
import { useProject } from '../context/ProjectContext'

const SSE_URL = '/api/events'
const LIVE_TIMER_INTERVAL = 500

type AgentDetailEventName = keyof AgentDetailEventMap

function isAgentDetailEvent(name: string): name is AgentDetailEventName {
  return (AGENT_DETAIL_EVENTS as readonly string[]).includes(name)
}

export const LiveTaskContext = createContext<LiveTaskState>({
  activeTaskId: null,
  activeTaskElapsedMs: null,
  rebaseConflict: null,
})

export function useLiveTask(): LiveTaskState {
  return useContext(LiveTaskContext)
}

function getCurrentIssueNumber(): number | null {
  const match = window.location.pathname.match(/\/issue\/(\d+)/)
  return match ? parseInt(match[1], 10) : null
}

function useSSEInner(projectId: string | null): LiveTaskState {
  const queryClient = useQueryClient()
  const eventSourceRef = useRef<EventSource | null>(null)
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
    (eventName: string, data: string) => {
      try {
        const parsed = JSON.parse(data)

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
          eventName === 'coder_session_status_changed'
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
          case 'question_asked':
          case 'question_answered': {
            queryClient.invalidateQueries({ queryKey: ['agent-status'] })
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
          case 'rebase_completed': {
            setRebaseConflict(null)
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
                toast.warning(`Issue #${driftEvt.issueNumber} has stale evidence — rebase or rerun checks`)
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

  useEffect(() => {
    if (!projectId) {
      if (eventSourceRef.current) {
        eventSourceRef.current.close()
        eventSourceRef.current = null
      }
      return
    }

    const url = `${SSE_URL}?projectId=${encodeURIComponent(projectId)}`
    const es = new EventSource(url)
    eventSourceRef.current = es

    const eventTypes: EventName[] = [
      'stage_changed',
      'comment_added',
      'agent_started',
      'agent_completed',
      'agent_paused',
      'agent_error',
      'approval_requested',
      'question_asked',
      'question_answered',
      'agent_text_chunk',
      'main_tool_call',
      'coder_text_chunk',
      'coder_tool_call',
      'ralph_task_update',
      'ralph_loop_progress',
      'plan_round_start',
      'plan_session_update',
      'coder_session_started',
      'coder_session_completed',
      'coder_session_failed',
      'coder_session_cancelled',
      'coder_session_status_changed',
      'merge_queued',
      'merge_started',
      'merge_completed',
      'merge_failed',
      'coder_recovery_status',
      'rebase_started',
      'rebase_progress',
      'rebase_completed',
      'rebase_conflict',
      'agent_blocked',
      'agent_conflict_resolution_started',
      'agent_conflict_resolution_completed',
      'agent_conflict_resolution_failed',
      'check_started',
      'check_update',
      'check_suite_status_changed',
      'stage_task_update',
      'base_drift_detected',
      'rebase_opportunity',
      'user_attention_requested',
    ]

    for (const type of eventTypes) {
      es.addEventListener(type, (e) => {
        handleEvent(type, (e as MessageEvent).data)
      })
    }

    es.onerror = () => {
      es.close()
      eventSourceRef.current = null
      setTimeout(() => {
        if (eventSourceRef.current === null) {
          const reconnect = new EventSource(url)
          eventSourceRef.current = reconnect

          for (const type of eventTypes) {
            reconnect.addEventListener(type, (ev) => {
              handleEvent(type, (ev as MessageEvent).data)
            })
          }
        }
      }, 3000)
    }

    return () => {
      es.close()
      eventSourceRef.current = null
      clearLiveTimer()
      setActiveTaskId(null)
      setActiveTaskElapsedMs(null)
    }
  }, [projectId, handleEvent, clearLiveTimer])

  useEffect(() => {
    return () => {
      clearLiveTimer()
    }
  }, [clearLiveTimer])

  return { activeTaskId, activeTaskElapsedMs, rebaseConflict }
}

export function LiveTaskProvider({ children }: { children: React.ReactNode }) {
  const { projectId } = useProject()
  const state = useSSEInner(projectId)
  return (
    <LiveTaskContext.Provider value={state}>
      {children}
    </LiveTaskContext.Provider>
  )
}

export default function useSSE(projectId: string | null) {
  return useSSEInner(projectId)
}
