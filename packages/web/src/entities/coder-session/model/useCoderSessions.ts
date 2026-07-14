import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { onAgentEvent } from '../../agent/@x/events'
import type { CoderSessionSummary } from './types'
import { useProject } from '../../project/@x/project-context'
import { getCoderSessions } from '../api/client'

export type CoderSessionsFetcher = typeof getCoderSessions

export function useCoderSessions(
  issueNumber: number,
  fetcher: CoderSessionsFetcher = getCoderSessions,
) {
  const { projectId } = useProject()
  const { data: sessions = [], isLoading, isFetching, refetch } = useQuery({
    queryKey: ['issues', issueNumber, projectId, 'coder-sessions'],
    queryFn: () => fetcher(issueNumber, projectId),
    enabled: issueNumber > 0 && !!projectId,
    staleTime: 30 * 1000,
  })

  const [liveSessions, setLiveSessions] = useState<CoderSessionSummary[]>([])

  const initializedRef = useRef(false)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const mountedRef = useRef(true)

  useEffect(() => {
    initializedRef.current = false
  }, [issueNumber, projectId])

  useEffect(() => {
    if (isLoading) return
    setLiveSessions((previous) => {
      if (!initializedRef.current) {
        initializedRef.current = true
        return [...sessions]
      }

      const refreshedIds = new Set(sessions.map((session) => session.id))
      return [
        ...sessions,
        ...previous.filter((session) => !refreshedIds.has(session.id)),
      ]
    })
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
        const newSession: CoderSessionSummary = {
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
          const updated: CoderSessionSummary = {
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

    unsubs.push(
      onAgentEvent('usage.updated', (detail) => {
        if (!mountedRef.current) return
        setLiveSessions((prev) => {
          const idx = prev.findIndex((s) => s.id === detail.coderSessionId)
          if (idx === -1) return prev
          const existing = prev[idx]
          const updated: CoderSessionSummary = {
            ...existing,
            usage: {
              ...(existing.usage ?? {}),
              ...(detail.inputTokens !== undefined && { inputTokens: detail.inputTokens }),
              ...(detail.outputTokens !== undefined && { outputTokens: detail.outputTokens }),
              ...(detail.totalTokens !== undefined && { totalTokens: detail.totalTokens }),
              ...(detail.cachedReadTokens !== undefined && { cachedReadTokens: detail.cachedReadTokens }),
              ...(detail.thoughtTokens !== undefined && { thoughtTokens: detail.thoughtTokens }),
              ...(detail.costAmount !== undefined && { costAmount: detail.costAmount }),
              ...(detail.costCurrency !== undefined && { costCurrency: detail.costCurrency }),
              ...(detail.contextWindowUsed !== undefined && { contextWindowUsed: detail.contextWindowUsed }),
              ...(detail.contextWindowSize !== undefined && { contextWindowSize: detail.contextWindowSize }),
              ...(detail.contextUsagePercent !== undefined && { contextUsagePercent: detail.contextUsagePercent }),
              ...(detail.healthStatus !== undefined && { healthStatus: detail.healthStatus }),
            },
          }
          const next = [...prev]
          next[idx] = updated
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
    isFetching,
    refetch,
  }
}
