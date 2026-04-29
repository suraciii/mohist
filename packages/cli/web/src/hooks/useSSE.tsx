import { useEffect, useRef, useCallback, useState, createContext, useContext } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { EventName, EventMap, LiveTaskState } from '../lib/types'
import { dispatchAgentEvent, AGENT_DETAIL_EVENTS } from '../lib/agent-events'
import { dispatchRebaseEvent } from '../lib/rebase-events'
import type { AgentDetailEventMap } from '../lib/types'

const SSE_URL = '/api/events'
const LIVE_TIMER_INTERVAL = 500

type AgentDetailEventName = keyof AgentDetailEventMap

function isAgentDetailEvent(name: string): name is AgentDetailEventName {
  return (AGENT_DETAIL_EVENTS as readonly string[]).includes(name)
}

export const LiveTaskContext = createContext<LiveTaskState>({
  activeTaskId: null,
  activeTaskElapsedMs: null,
})

export function useLiveTask(): LiveTaskState {
  return useContext(LiveTaskContext)
}

function useSSEInner(projectId: string | null): LiveTaskState {
  const queryClient = useQueryClient()
  const eventSourceRef = useRef<EventSource | null>(null)
  const [activeTaskId, setActiveTaskId] = useState<string | null>(null)
  const [activeTaskElapsedMs, setActiveTaskElapsedMs] = useState<number | null>(null)
  const taskStartRef = useRef<number | null>(null)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)

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
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'approval_requested': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'question_asked':
          case 'question_answered': {
            const { issueId } = parsed as EventMap['question_asked']
            if (issueId) {
              queryClient.invalidateQueries({ queryKey: ['questions', issueId] })
            }
            break
          }
          case 'merge_queued':
          case 'merge_started':
          case 'merge_completed':
          case 'merge_failed': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            break
          }
          case 'rebase_started': {
            const { issueNumber: rsNum } = parsed as EventMap['rebase_started']
            dispatchRebaseEvent({ type: 'rebase_started', issueNumber: rsNum })
            break
          }
          case 'rebase_progress': {
            const { issueNumber: rpNum, step } = parsed as EventMap['rebase_progress']
            dispatchRebaseEvent({ type: 'rebase_progress', issueNumber: rpNum, step })
            break
          }
          case 'rebase_completed': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            const { issueNumber: rcNum, rebased } = parsed as EventMap['rebase_completed']
            if (rcNum) {
              queryClient.invalidateQueries({ queryKey: ['worktree-status', rcNum] })
            }
            dispatchRebaseEvent({ type: 'rebase_completed', issueNumber: rcNum, rebased })
            break
          }
          case 'rebase_conflict': {
            queryClient.invalidateQueries({ queryKey: ['issues'] })
            const { issueNumber: rconfNum, conflicts } = parsed as EventMap['rebase_conflict']
            if (rconfNum) {
              queryClient.invalidateQueries({ queryKey: ['worktree-status', rconfNum] })
            }
            dispatchRebaseEvent({ type: 'rebase_conflict', issueNumber: rconfNum, conflicts })
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
      'plan_round_complete',
      'coder_session_started',
      'coder_session_completed',
      'merge_queued',
      'merge_started',
      'merge_completed',
      'merge_failed',
      'coder_recovery_status',
      'rebase_started',
      'rebase_progress',
      'rebase_completed',
      'rebase_conflict',
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

  return { activeTaskId, activeTaskElapsedMs }
}

export default function useSSE(projectId: string | null) {
  return useSSEInner(projectId)
}
