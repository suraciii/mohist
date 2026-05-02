import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api'
import { onAgentEvent } from '../lib/agent-events'
import type { CoderSessionItem } from '../lib/types'

export function useCoderSessions(issueNumber: number) {
  const { data: sessions = [], isLoading } = useQuery({
    queryKey: ['issues', issueNumber, 'coder-sessions'],
    queryFn: () => api.getCoderSessions(issueNumber),
    enabled: issueNumber > 0,
  })

  const [liveSessions, setLiveSessions] = useState<CoderSessionItem[]>([])
  const initializedRef = useRef(false)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const mountedRef = useRef(true)

  useEffect(() => {
    initializedRef.current = false
  }, [issueNumber])

  useEffect(() => {
    if (isLoading || !sessions) return
    if (initializedRef.current) return
    initializedRef.current = true
    setLiveSessions([...sessions])
  }, [sessions, isLoading])

  const startTimer = useCallback(() => {
    if (timerRef.current !== null) return
    timerRef.current = setInterval(() => {
      if (!mountedRef.current) return
      setLiveSessions((prev) => {
        const hasRunning = prev.some((s) => s.status === 'running')
        if (!hasRunning) {
          if (timerRef.current !== null) {
            clearInterval(timerRef.current)
            timerRef.current = null
          }
          return prev
        }
        return prev.map((s) => s)
      })
    }, 1000)
  }, [])

  const stopTimer = useCallback(() => {
    if (timerRef.current !== null) {
      clearInterval(timerRef.current)
      timerRef.current = null
    }
  }, [])

  useEffect(() => {
    mountedRef.current = true
    const issueId = String(issueNumber)
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_session_started', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const newSession: CoderSessionItem = {
          id: detail.coderSessionId,
          acpSessionId: detail.acpSessionId,
          executionId: detail.executionId ?? null,
          taskDescription: detail.taskDescription ?? null,
          status: 'running',
          createdAt: new Date().toISOString(),
          completedAt: null,
          model: detail.model ?? null,
          coderType: detail.coderType ?? null,
          stage: detail.stage ?? null,
          title: detail.title ?? null,
          workflowLogs: [],
        }
        setLiveSessions((prev) => [...prev, newSession])
        startTimer()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setLiveSessions((prev) => {
          const next = prev.map((s) =>
            s.id === detail.coderSessionId
              ? { ...s, status: detail.status, completedAt: new Date().toISOString() }
              : s,
          )
          const hasRunning = next.some((s) => s.status === 'running')
          if (!hasRunning) stopTimer()
          return next
        })
      }),
    )

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
      stopTimer()
    }
  }, [issueNumber, startTimer, stopTimer])

  return {
    sessions: liveSessions,
    isLoading,
  }
}
