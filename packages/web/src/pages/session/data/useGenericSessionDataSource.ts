import { useCallback, useMemo } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { useQueryClient, useQuery } from '@tanstack/react-query'
import { useProject, useProjectPath } from '../../../entities/project'
import { useGenericSessionSummary, useGenericSessionTranscript, getGenericSessionTranscript, useGenericFollowup, useCancelGenericSession } from '../../../entities/agent'
import type { AgentSessionTranscriptResponse, SessionTurn, SessionMetadata } from '../../../entities/coder-session'
import { useSessionTranscript, projectTurn } from '../../../widgets/session-transcript'
import { buildGenericSessionMetadata } from './buildGenericSessionMetadata'
import { findHistoricalRuntimeWithVisibleContent } from './SessionDataSource'
import type { SessionCancelOptions, SessionDataSourceResult, StatusKind, EmptyStateKind } from './SessionDataSource'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

export interface GenericSessionDataSourceDependencies {
  useSessionTranscript: typeof useSessionTranscript
  projectTurn: typeof projectTurn
  useGenericSessionSummary: typeof useGenericSessionSummary
  useGenericSessionTranscript: typeof useGenericSessionTranscript
  getGenericSessionTranscript?: typeof getGenericSessionTranscript
  useGenericFollowup: typeof useGenericFollowup
  useCancelGenericSession: typeof useCancelGenericSession
}

const defaultDependencies: GenericSessionDataSourceDependencies = {
  useSessionTranscript,
  projectTurn,
  useGenericSessionSummary,
  useGenericSessionTranscript,
  getGenericSessionTranscript,
  useGenericFollowup,
  useCancelGenericSession,
}

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

export function useGenericSessionDataSource(
  dependencies: GenericSessionDataSourceDependencies = defaultDependencies,
): SessionDataSourceResult {
  const {
    useSessionTranscript: useTranscript,
    projectTurn: projectTranscriptTurn,
    useGenericSessionSummary: useSummary,
    useGenericSessionTranscript: useTranscriptResponse,
    getGenericSessionTranscript: fetchTranscript = getGenericSessionTranscript,
    useGenericFollowup: useFollowup,
    useCancelGenericSession: useCancel,
  } = dependencies
  const { sessionId: rawSessionId } = useParams<{ sessionId: string }>()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const queryClient = useQueryClient()
  const sessionId = rawSessionId ? decodeURIComponent(rawSessionId) : ''
  const [searchParams] = useSearchParams()
  const requestedRuntimeSessionId = searchParams.get('rt')

  useDocumentTitle(`Session — Mohist`)

  const { data: summary, isLoading: summaryLoading, isError: summaryError } = useSummary(sessionId)
  const { data: transcriptResponse } = useTranscriptResponse(sessionId, requestedRuntimeSessionId)
  const genericFollowup = useFollowup()
  const cancelGeneric = useCancel()

  const initialTurns = useMemo<SessionTurn[]>(() => transcriptResponse?.turns ?? [], [transcriptResponse])

  const meta: SessionMetadata | null = useMemo(() => {
    if (!summary) return null
    return buildGenericSessionMetadata(summary)
  }, [summary])

  const rawStatus = summary?.status ?? ''
  const apiStatusKind = meta?.statusKind
  const isRunning = summary == null
    ? true
    : (rawStatus === 'active' || rawStatus === 'running' || rawStatus === 'probing') && apiStatusKind !== 'completed' && apiStatusKind !== 'failed'
  const terminal = rawStatus === 'completed' || rawStatus === 'failed' || rawStatus === 'stopped' || rawStatus === 'cancelled'
  const runtimeLineage = summary?.runtimeSessionLineage ?? null
  const viewedRuntimeSessionId = requestedRuntimeSessionId ?? summary?.runtimeSessionId ?? null
  const isCurrentRuntimeView = viewedRuntimeSessionId === summary?.runtimeSessionId
  const canFollowup = !terminal && isCurrentRuntimeView && !!summary?.runtimeSessionId && !!summary.runtime
  const isHistoricalRuntimeView = !!requestedRuntimeSessionId

  const shouldFetchUnfilteredTranscript = isHistoricalRuntimeView && transcriptResponse != null && transcriptResponse.turns.length === 0

  const { data: unfilteredTranscriptResponse } = useQuery<AgentSessionTranscriptResponse>({
    queryKey: ['agent-session', projectId, sessionId, 'transcript', null],
    queryFn: () => fetchTranscript(projectId!, sessionId, null),
    enabled: !!projectId && !!sessionId && shouldFetchUnfilteredTranscript,
  })

  const statusKind: StatusKind = meta
    ? (meta.statusKind ?? getSessionStatusKind(rawStatus, meta.lastActivityAt, isRunning))
    : getSessionStatusKind(rawStatus, undefined, isRunning)

  const metadataQueryKey = useMemo(
    () => ['agent-session', projectId, sessionId] as const,
    [projectId, sessionId],
  )
  const transcriptQueryKey = useMemo(
    () => ['agent-session', projectId, sessionId, 'transcript', requestedRuntimeSessionId] as const,
    [projectId, sessionId, requestedRuntimeSessionId],
  )

  const handleRecoverySuccess = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: metadataQueryKey })
    queryClient.invalidateQueries({ queryKey: transcriptQueryKey })
    queryClient.invalidateQueries({ queryKey: ['agent-sessions'] })
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
  } = useTranscript({
    issueNumber: 0,
    sessionId,
    runtimeSessionId: requestedRuntimeSessionId ?? summary?.runtimeSessionId ?? '',
    runtime: summary?.runtime ?? null,
    isHistoricalRuntimeView,
    initialTurns: initialTurns.length > 0 ? initialTurns : undefined,
    sessionQueryKeys: [metadataQueryKey, transcriptQueryKey],
    isRunning,
    terminalInvalidationKey: ['agent-session', projectId, sessionId],
  })

  const displayStatusKind: StatusKind = isFinalizing && isRunning ? 'finalizing' : statusKind

  const displayTurns = useMemo(() => turns.map((turn) => projectTranscriptTurn(turn)), [turns, projectTranscriptTurn])

  // Determine back link: Activity-origin links (from=activity) take priority
  // and return to the project-scoped Activity page. Otherwise, if the session
  // carries an issue context ref, link to that issue; otherwise link to the
  // agent profile (and never fabricate issue/workflow links for sessions
  // without an issue binding).
  const fromActivity = searchParams.get('from') === 'activity'
  const buildLineageTargetPath = runtimeLineage && runtimeLineage.length >= 2
    ? (runtimeId: string) => {
        const base = toProjectPath(`/agent-sessions/${encodeURIComponent(sessionId)}`)
        const params = new URLSearchParams({ rt: runtimeId })
        if (fromActivity) params.set('from', 'activity')
        return `${base}?${params}`
      }
    : null

  const emptyStateEvidence = useMemo(() => {
    if (turns.length > 0) return { emptyStateKind: null as EmptyStateKind | null, historicalRuntimeTarget: null as string | null, historicalRuntimeId: null as string | null }

    if (isHistoricalRuntimeView && unfilteredTranscriptResponse?.turns && unfilteredTranscriptResponse.turns.length > 0) {
      const historicalRuntimeId = findHistoricalRuntimeWithVisibleContent(
        unfilteredTranscriptResponse.turns,
        requestedRuntimeSessionId,
        runtimeLineage,
      )
      if (historicalRuntimeId) {
        return {
          emptyStateKind: 'runtime-filtered' as const,
          historicalRuntimeTarget: buildLineageTargetPath?.(historicalRuntimeId) ?? null,
          historicalRuntimeId,
        }
      }
    }

    if (isRunning) {
      return { emptyStateKind: 'running-no-content' as const, historicalRuntimeTarget: null as string | null, historicalRuntimeId: null as string | null }
    }

    return { emptyStateKind: 'terminal-no-content' as const, historicalRuntimeTarget: null as string | null, historicalRuntimeId: null as string | null }
  }, [turns.length, isHistoricalRuntimeView, unfilteredTranscriptResponse, requestedRuntimeSessionId, runtimeLineage, buildLineageTargetPath, isRunning])
  const hasIssueContextRef = summary?.contextRefs?.issueNumber != null
  const backPath = fromActivity
    ? toProjectPath('/activity')
    : hasIssueContextRef && summary?.contextRefs?.issueNumber
      ? toProjectPath(`/issues/${summary.contextRefs.issueNumber}`)
      : summary?.agentId
        ? toProjectPath(`/agents/${encodeURIComponent(summary.agentId)}`)
        : toProjectPath('/agents')
  const backLabel = fromActivity
    ? 'Activity'
    : hasIssueContextRef && summary?.contextRefs?.issueNumber
      ? `Issue #${summary.contextRefs.issueNumber}`
      : summary?.agentName ?? 'Agent'
  // Generic sessions have no workflow context link; only expose it when
  // an issue binding exists. Do NOT fabricate workflow context for sessions
  // without an issue.
  const workflowContextPath = hasIssueContextRef && summary?.contextRefs?.issueNumber
    ? toProjectPath(`/issues/${summary.contextRefs.issueNumber}`)
    : undefined
  const workflowContextLabel = workflowContextPath ? 'Workflow context' : undefined

  const sendFollowup = useCallback(async (text: string) => {
    await genericFollowup.mutateAsync({ sessionId, text })
  }, [genericFollowup, sessionId])

  const cancelSession = useCallback((options?: SessionCancelOptions) => {
    cancelGeneric.mutate(
      { sessionId, agentRef: summary?.agentId },
      {
        onSuccess: (result) => options?.onSuccess?.({ state: result.state ?? 'not-cancellable' }),
        onSettled: options?.onSettled,
      },
    )
  }, [cancelGeneric, sessionId, summary?.agentId])

  const cancel = useMemo(
    () => isRunning && isCurrentRuntimeView && !!summary?.runtimeSessionId && !!summary.runtime
      ? { mutate: cancelSession, isPending: cancelGeneric.isPending }
      : null,
    [cancelSession, cancelGeneric.isPending, isCurrentRuntimeView, isRunning, summary?.runtime, summary?.runtimeSessionId],
  )

  return {
    isLoading: summaryLoading,
    isError: summaryError,
    notFound: !sessionId || (!summary && !summaryLoading && !summaryError),
    sessionKey: sessionId,
    runtimeSessionId: viewedRuntimeSessionId ?? sessionId,
    meta,
    transcriptResponse: transcriptResponse ?? null,
    initialTurns,
    statusKind: displayStatusKind,
    isRunning,
    canFollowup,
    followupIsPending: genericFollowup.isPending,
    sendFollowup,
    cancel,
    contextWindowUsed: meta?.usage?.contextWindowUsed ?? null,
    contextWindowSize: meta?.usage?.contextWindowSize ?? null,
    contextUsagePercent: meta?.usage?.contextUsagePercent ?? null,
    healthStatus: meta?.usage?.healthStatus ?? null,
    hasRecoveryActions: !!summary,
    recoveryAvailable: summary?.recoveryAvailable ?? false,
    recoverySessionName: null,
    recoverySessionId: sessionId || null,
    runtimeSessionLineage: runtimeLineage,
    viewedRuntimeSessionId,
    buildLineageTargetPath,
    metadataQueryKey,
    transcriptQueryKey,
    handleRecoverySuccess,
    backPath,
    backLabel,
    issueTitle: undefined,
    workflowContextPath,
    workflowContextLabel,
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
    emptyStateKind: emptyStateEvidence.emptyStateKind,
    historicalRuntimeTarget: emptyStateEvidence.historicalRuntimeTarget,
    historicalRuntimeId: emptyStateEvidence.historicalRuntimeId,
    issueNumber: 0,
  }
}
