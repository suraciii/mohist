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
        const hasActive = prev.some((s) => s.status === 'running' || s.status === 'probing')
        if (!hasActive) {
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
          lastDataAt: null,
          probeSentAt: null,
          probeDeadlineAt: null,
          failureReason: null,
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
          const hasActive = next.some((s) => s.status === 'running' || s.status === 'probing')
          if (!hasActive) stopTimer()
          return next
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_status_changed', (detail) => {
        if (!mountedRef.current) return
        setLiveSessions((prev) => {
          const idx = prev.findIndex((s) => s.id === detail.coderSessionId)
          if (idx === -1) return prev
          const existing = prev[idx]
          const updated: CoderSessionItem = {
            ...existing,
            status: detail.status,
            ...(detail.lastDataAt !== undefined && { lastDataAt: detail.lastDataAt }),
            ...(detail.probeSentAt !== undefined && { probeSentAt: detail.probeSentAt }),
            ...(detail.probeDeadlineAt !== undefined && { probeDeadlineAt: detail.probeDeadlineAt }),
            ...(detail.failureReason !== undefined && { failureReason: detail.failureReason }),
          }
          const next = [...prev]
          next[idx] = updated
          const hasActive = next.some((s) => s.status === 'running' || s.status === 'probing')
          if (!hasActive) stopTimer()
          else startTimer()
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
