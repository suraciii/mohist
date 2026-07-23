import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { onAgentEvent } from '../../agent/@x/events'
import type { CoderSessionSummary } from './types'
import { useProject } from '../../project/@x/project-context'
import { issueWorkflowKeys } from '../../issue/@x/query-keys'
import { getCoderSessions } from '../api/client'

export type CoderSessionsFetcher = typeof getCoderSessions

const EMPTY_SESSIONS: CoderSessionSummary[] = []

export function useCoderSessions(
  issueNumber: number,
  fetcher: CoderSessionsFetcher = getCoderSessions,
) {
  const { projectId } = useProject()
  const { data: sessions = EMPTY_SESSIONS, isLoading, isFetching, refetch } = useQuery({
    queryKey: issueWorkflowKeys.session(projectId, issueNumber, 'coder-sessions'),
    queryFn: ({ signal }) => fetcher(issueNumber, projectId, signal),
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

      const fetchedIds = new Set(sessions.map((session) => session.id))
      const liveOnly = previous.filter(
        (session) =>
          session.activity === 'active' &&
          !fetchedIds.has(session.id),
      )
      return [...sessions, ...liveOnly]
    })
  }, [sessions, isLoading])

  const startTimer = useCallback(() => {
    if (timerRef.current !== null) return
    timerRef.current = setInterval(() => {
      if (!mountedRef.current) return
      setLiveSessions((prev) => {
        const hasActive = prev.some((s) => s.activity === 'active')
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
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_session_started', (detail) => {
        if (detail.projectId !== projectId || detail.issueNumber !== issueNumber || !mountedRef.current) return
        const newSession: CoderSessionSummary = {
          id: detail.sessionId,
          runtimeSessionId: detail.runtimeSessionId,
          executionId: detail.executionId ?? null,
          taskDescription: detail.taskDescription ?? null,
           activity: 'active',
          createdAt: new Date().toISOString(),
          completedAt: null,
          model: detail.model ?? null,
          runtime: detail.runtime ?? null,
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
        if (detail.projectId !== projectId || detail.issueNumber !== issueNumber || !mountedRef.current) return
        setLiveSessions((prev) => {
          const next = prev.map((s) =>
            s.id === detail.sessionId
                ? { ...s, activity: 'idle' as const }
              : s,
          )
           const hasActive = next.some((s) => s.activity === 'active')
          if (!hasActive) stopTimer()
          return next
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_status_changed', (detail) => {
        if (detail.projectId !== projectId || detail.issueNumber !== issueNumber || !mountedRef.current) return
        setLiveSessions((prev) => {
          const idx = prev.findIndex((s) => s.id === detail.sessionId)
          if (idx === -1) return prev
          const existing = prev[idx]
          const updated: CoderSessionSummary = {
            ...existing,
             activity: (detail.status === 'active' ? 'active' : detail.status === 'unknown' ? 'unknown' : 'idle') as CoderSessionSummary['activity'],
            ...(detail.lastDataAt !== undefined && { lastDataAt: detail.lastDataAt }),
            ...(detail.probeSentAt !== undefined && { probeSentAt: detail.probeSentAt }),
            ...(detail.probeDeadlineAt !== undefined && { probeDeadlineAt: detail.probeDeadlineAt }),
            ...(detail.failureReason !== undefined && { failureReason: detail.failureReason }),
          }
          const next = [...prev]
          next[idx] = updated
           const hasActive = next.some((s) => s.activity === 'active')
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
          const idx = prev.findIndex((s) => s.id === detail.sessionId)
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
              ...(detail.cachedWriteTokens !== undefined && { cachedWriteTokens: detail.cachedWriteTokens }),
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
  }, [issueNumber, projectId, startTimer, stopTimer])

  return {
    sessions: liveSessions,
    isLoading,
    isFetching,
    refetch,
  }
}
