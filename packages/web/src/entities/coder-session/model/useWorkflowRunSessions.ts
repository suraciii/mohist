import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../../agent/@x/events'
import { getWorkflowRunSessions } from '../api/client'
import type { WorkflowRunSession } from './types'

const EMPTY_SESSIONS: WorkflowRunSession[] = []

interface LiveSessionState {
  workflowRunId: string | null | undefined
  sessions: WorkflowRunSession[]
}

function matchesCurrentBinding(
  session: WorkflowRunSession,
  event: { runtimeSessionId?: string | null },
) {
  return event.runtimeSessionId != null && event.runtimeSessionId === session.runtimeSessionId
}

export type WorkflowRunSessionsFetcher = typeof getWorkflowRunSessions

export function useWorkflowRunSessions(
  workflowRunId: string | null | undefined,
  fetcher: WorkflowRunSessionsFetcher = getWorkflowRunSessions,
) {
  const queryClient = useQueryClient()
  const queryKey = useMemo(() => ['workflow-runs', workflowRunId, 'sessions'] as const, [workflowRunId])
  const { data: sessions = EMPTY_SESSIONS, isLoading } = useQuery({
    queryKey,
    queryFn: () => fetcher(workflowRunId!),
    enabled: !!workflowRunId,
    staleTime: 30 * 1000,
  })

  const [liveState, setLiveState] = useState<LiveSessionState>({
    workflowRunId: null,
    sessions: EMPTY_SESSIONS,
  })
  const mountedRef = useRef(true)

  useEffect(() => {
    if (!workflowRunId || isLoading) {
      setLiveState((prev) => prev.workflowRunId === workflowRunId
        ? prev
        : { workflowRunId, sessions: EMPTY_SESSIONS })
      return
    }
    setLiveState({ workflowRunId, sessions })
  }, [workflowRunId, sessions, isLoading])

  useEffect(() => {
    mountedRef.current = true
    if (!workflowRunId) return

    const invalidate = () => {
      queryClient.invalidateQueries({ queryKey })
    }

    const unsubs = [
      onAgentEvent('coder_session_started', (detail) => {
        if (!mountedRef.current) return
        const detailWorkflowRunId = 'workflowRunId' in detail ? detail.workflowRunId : undefined
        if (detailWorkflowRunId && detailWorkflowRunId !== workflowRunId) return
        invalidate()
      }),
      onAgentEvent('com.mohist.agent-session.runtime-bound', () => {
        if (!mountedRef.current) return
        invalidate()
      }),
      onAgentEvent('session.closed', (detail) => {
        if (!mountedRef.current) return
        setLiveState((prev) => prev.workflowRunId === workflowRunId
          ? {
              ...prev,
              sessions: prev.sessions.map((session) =>
                matchesCurrentBinding(session, detail)
                  ? { ...session, status: detail.status, completedAt: new Date().toISOString() }
                  : session,
              ),
            }
          : prev)
      }),
      onAgentEvent('coder_session_status_changed', (detail) => {
        if (!mountedRef.current) return
        setLiveState((prev) => prev.workflowRunId === workflowRunId
          ? {
              ...prev,
              sessions: prev.sessions.map((session) =>
                matchesCurrentBinding(session, detail)
                  ? {
                      ...session,
                      status: detail.status,
                      runtimeSessionId: detail.runtimeSessionId ?? session.runtimeSessionId,
                      ...(detail.lastDataAt !== undefined && { lastDataAt: detail.lastDataAt }),
                      ...(detail.failureReason !== undefined && { failureReason: detail.failureReason }),
                    }
                  : session,
              ),
            }
          : prev)
      }),
      onAgentEvent('usage.updated', (detail) => {
        if (!mountedRef.current) return
        setLiveState((prev) => prev.workflowRunId === workflowRunId
          ? {
              ...prev,
              sessions: prev.sessions.map((session) =>
                matchesCurrentBinding(session, detail)
                  ? {
                      ...session,
                      usage: {
                        ...(session.usage ?? {}),
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
                  : session,
              ),
            }
          : prev)
      }),
      onAgentEvent('context_health_update', (detail) => {
        if (!mountedRef.current) return
        setLiveState((prev) => prev.workflowRunId === workflowRunId
          ? {
              ...prev,
              sessions: prev.sessions.map((session) =>
                matchesCurrentBinding(session, detail)
                  ? {
                      ...session,
                      usage: {
                        ...(session.usage ?? {}),
                        ...(detail.healthStatus !== undefined && { healthStatus: detail.healthStatus }),
                        ...(detail.contextUsagePercent !== undefined && { contextUsagePercent: detail.contextUsagePercent }),
                        ...(detail.contextWindowUsed !== undefined && { contextWindowUsed: detail.contextWindowUsed }),
                        ...(detail.contextWindowSize !== undefined && { contextWindowSize: detail.contextWindowSize }),
                      },
                    }
                  : session,
              ),
            }
          : prev)
      }),
    ]

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
    }
  }, [workflowRunId, queryClient, queryKey])

  return {
    sessions: liveState.workflowRunId === workflowRunId ? liveState.sessions : EMPTY_SESSIONS,
    isLoading,
  }
}
