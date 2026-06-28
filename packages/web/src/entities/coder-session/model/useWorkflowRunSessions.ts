import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../../agent/@x/events'
import { getWorkflowRunSessions } from '../api/client'
import type { WorkflowRunSession } from './types'

const EMPTY_SESSIONS: WorkflowRunSession[] = []

export function useWorkflowRunSessions(workflowRunId: string | null | undefined) {
  const queryClient = useQueryClient()
  const queryKey = useMemo(() => ['workflow-runs', workflowRunId, 'sessions'] as const, [workflowRunId])
  const { data: sessions = EMPTY_SESSIONS, isLoading } = useQuery({
    queryKey,
    queryFn: () => getWorkflowRunSessions(workflowRunId!),
    enabled: !!workflowRunId,
    staleTime: 30 * 1000,
  })

  const [liveSessions, setLiveSessions] = useState<WorkflowRunSession[]>([])
  const mountedRef = useRef(true)

  useEffect(() => {
    if (isLoading) return
    setLiveSessions(sessions)
  }, [sessions, isLoading])

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
      onAgentEvent('coder_session_completed', (detail) => {
        if (!mountedRef.current) return
        setLiveSessions((prev) => prev.map((session) =>
          session.id === detail.coderSessionId
            ? { ...session, status: detail.status, completedAt: new Date().toISOString() }
            : session,
        ))
      }),
      onAgentEvent('coder_session_status_changed', (detail) => {
        if (!mountedRef.current) return
        setLiveSessions((prev) => prev.map((session) =>
          session.id === detail.coderSessionId
            ? {
                ...session,
                status: detail.status,
                acpSessionId: detail.acpSessionId ?? session.acpSessionId,
                ...(detail.lastDataAt !== undefined && { lastDataAt: detail.lastDataAt }),
                ...(detail.failureReason !== undefined && { failureReason: detail.failureReason }),
              }
            : session,
        ))
      }),
      onAgentEvent('usage.updated', (detail) => {
        if (!mountedRef.current) return
        setLiveSessions((prev) => prev.map((session) =>
          session.id === detail.coderSessionId || session.acpSessionId === detail.acpSessionId
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
                },
              }
            : session,
        ))
      }),
    ]

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
    }
  }, [workflowRunId, queryClient, queryKey])

  return {
    sessions: liveSessions,
    isLoading,
  }
}
