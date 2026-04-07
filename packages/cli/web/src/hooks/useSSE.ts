import { useEffect, useRef, useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { EventName, EventMap } from '../lib/types'

const SSE_URL = '/api/events'

function useSSE(projectId: string | null) {
  const queryClient = useQueryClient()
  const eventSourceRef = useRef<EventSource | null>(null)

  const handleEvent = useCallback(
    (eventName: string, data: string) => {
      try {
        const parsed = JSON.parse(data)

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
        }
      } catch {
        // ignore malformed events
      }
    },
    [queryClient],
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
    }
  }, [projectId, handleEvent])
}

export default useSSE
