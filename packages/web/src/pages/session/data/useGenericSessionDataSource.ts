import { useCallback, useMemo } from 'react'
import { useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { useProject, useProjectPath } from '../../../entities/project'
import { useGenericSessionSummary, useGenericSessionTranscript, useGenericFollowup } from '../../../entities/agent'
import type { SessionTurn, SessionMetadata } from '../../../entities/coder-session'
import { useSessionTranscript, projectTurn } from '../../../widgets/session-transcript'
import { buildGenericSessionMetadata } from './buildGenericSessionMetadata'
import type { SessionDataSourceResult, StatusKind } from './SessionDataSource'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

function getSessionStatusKind(
  rawStatus: string | undefined,
  lastActivityAt: string | null | undefined,
  isRunning: boolean,
): StatusKind {
  if (rawStatus === 'failed' || rawStatus === 'timeout' || rawStatus === 'cancelled') return 'failed'
  if (rawStatus === 'completed') return 'completed'
  if (rawStatus === 'inactive') return 'stale'
  if (rawStatus === 'probing') return 'probing'
  if (rawStatus === 'active') return lastActivityAt ? 'live' : 'stale'
  if (!isRunning) return 'completed'
  if (!lastActivityAt) return 'live'
  const lastActivity = new Date(lastActivityAt).getTime()
  const now = Date.now()
  const twoMinutes = 2 * 60 * 1000
  if (now - lastActivity > twoMinutes) return 'stale'
  return 'live'
}

export function useGenericSessionDataSource(): SessionDataSourceResult {
  const { sessionId: rawSessionId } = useParams<{ sessionId: string }>()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const queryClient = useQueryClient()
  const sessionId = rawSessionId ? decodeURIComponent(rawSessionId) : ''

  useDocumentTitle(`Session — Mohist`)

  const { data: summary, isLoading: summaryLoading, isError: summaryError } = useGenericSessionSummary(sessionId)
  const { data: transcriptResponse } = useGenericSessionTranscript(sessionId)
  const genericFollowup = useGenericFollowup()

  const initialTurns = useMemo<SessionTurn[]>(() => transcriptResponse?.turns ?? [], [transcriptResponse])

  const meta: SessionMetadata | null = useMemo(() => {
    if (!summary) return null
    return buildGenericSessionMetadata(summary)
  }, [summary])

  const rawStatus = summary?.status ?? ''
  const apiStatusKind = meta?.statusKind
  const isRunning = (rawStatus === 'active' || rawStatus === 'running' || rawStatus === 'probing') && apiStatusKind !== 'completed' && apiStatusKind !== 'failed'

  const statusKind: StatusKind = meta
    ? (meta.statusKind ?? getSessionStatusKind(rawStatus, meta.lastActivityAt, isRunning))
    : getSessionStatusKind(rawStatus, undefined, isRunning)

  const metadataQueryKey = useMemo(
    () => ['agent-session', projectId, sessionId] as const,
    [projectId, sessionId],
  )
  const transcriptQueryKey = useMemo(
    () => ['agent-session', projectId, sessionId, 'transcript'] as const,
    [projectId, sessionId],
  )

  const handleRecoverySuccess = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: metadataQueryKey })
    queryClient.invalidateQueries({ queryKey: transcriptQueryKey })
  }, [queryClient, metadataQueryKey, transcriptQueryKey])

  const {
    turns,
    transcriptVersion,
    scrollToBottom,
    newContentAvailable,
    setIsNearBottom,
    isFinalizing,
    isThinking,
    isStreaming,
  } = useSessionTranscript({
    issueNumber: 0,
    sessionId,
    acpSessionId: sessionId,
    initialTurns: initialTurns.length > 0 ? initialTurns : undefined,
    sessionQueryKeys: [metadataQueryKey, transcriptQueryKey],
    isRunning,
    terminalInvalidationKey: ['agent-session', projectId, sessionId],
  })

  const displayStatusKind: StatusKind = isFinalizing && isRunning ? 'finalizing' : statusKind

  const displayTurns = useMemo(() => turns.map((turn) => projectTurn(turn)), [turns])

  // Determine back link: if context ref has issueNumber, link to issue; else link to agent profile
  const hasIssueContextRef = summary?.contextRefs?.issueNumber != null
  const backPath = hasIssueContextRef && summary?.contextRefs?.issueNumber
    ? toProjectPath(`/issues/${summary.contextRefs.issueNumber}`)
    : summary?.agentId
      ? toProjectPath(`/agents/${encodeURIComponent(summary.agentId)}`)
      : toProjectPath('/agents')
  const backLabel = hasIssueContextRef && summary?.contextRefs?.issueNumber
    ? `Issue #${summary.contextRefs.issueNumber}`
    : summary?.agentName ?? 'Agent'

  const hasData = meta?.usage?.contextWindowUsed != null || meta?.usage?.contextWindowSize != null

  const sendFollowup = useCallback((text: string) => {
    genericFollowup.mutate({ sessionId, text })
  }, [genericFollowup, sessionId])

  return {
    isLoading: summaryLoading,
    isError: summaryError,
    notFound: !sessionId || (!summary && !summaryLoading && !summaryError),
    sessionKey: sessionId,
    acpSessionId: sessionId,
    meta,
    transcriptResponse: transcriptResponse ?? null,
    initialTurns,
    statusKind: displayStatusKind,
    isRunning,
    followupIsPending: genericFollowup.isPending,
    sendFollowup,
    contextWindowUsed: meta?.usage?.contextWindowUsed ?? null,
    contextWindowSize: meta?.usage?.contextWindowSize ?? null,
    contextUsagePercent: meta?.usage?.contextUsagePercent ?? null,
    hasRecoveryActions: hasData,
    recoverySessionName: null,
    runtimeSessionLineage: null,
    viewedRuntimeSessionId: null,
    buildLineageTargetPath: null,
    metadataQueryKey,
    transcriptQueryKey,
    handleRecoverySuccess,
    backPath,
    backLabel,
    issueTitle: undefined,
    siblingNav: null,
    siblingSidebar: null,
    sessionTurns: turns,
    transcriptVersion,
    scrollToBottom,
    newContentAvailable,
    setIsNearBottom,
    isFinalizing,
    isThinking,
    isStreaming,
    displayTurns,
    issueNumber: 0,
  }
}
