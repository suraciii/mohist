import { useCallback, useMemo } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useProject, useProjectPath } from '../../../entities/project'
import { useGenericSessionSummary, useGenericSessionTranscript, useGenericFollowup, useGenericTurnControl, useCancelGenericSession, launchObservationQueryOptions } from '../../../entities/agent'
import type { AgentLaunchObservationDto } from '../../../entities/agent'
import { canFollowupSession, deriveSessionStatusKind } from '../../../entities/coder-session'
import type { SessionTurn, SessionMetadata } from '../../../entities/coder-session'
import { useSessionTranscript, projectTurn } from '../../../widgets/session-transcript'
import { buildGenericSessionMetadata } from './buildGenericSessionMetadata'
import type { SessionCancelOptions, SessionDataSourceResult, EmptyStateKind } from './SessionDataSource'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

export interface GenericSessionDataSourceDependencies {
  useSessionTranscript: typeof useSessionTranscript
  projectTurn: typeof projectTurn
  useGenericSessionSummary: typeof useGenericSessionSummary
  useGenericSessionTranscript: typeof useGenericSessionTranscript
  getGenericSessionTranscript?: (...args: [string, string, string?]) => Promise<unknown>
  useGenericFollowup: typeof useGenericFollowup
  useGenericTurnControl?: typeof useGenericTurnControl
  useCancelGenericSession?: typeof useCancelGenericSession
}

const defaultDependencies: GenericSessionDataSourceDependencies = {
  useSessionTranscript,
  projectTurn,
  useGenericSessionSummary,
  useGenericSessionTranscript,
  useGenericFollowup,
  useGenericTurnControl,
}

export function useGenericSessionDataSource(
  dependencies: GenericSessionDataSourceDependencies = defaultDependencies,
): SessionDataSourceResult {
  const {
    useSessionTranscript: useTranscript,
    projectTurn: projectTranscriptTurn,
    useGenericSessionSummary: useSummary,
    useGenericSessionTranscript: useTranscriptResponse,
    useGenericFollowup: useFollowup,
  } = dependencies
  const useCancel = dependencies.useGenericTurnControl
    ?? dependencies.useCancelGenericSession
    ?? defaultDependencies.useGenericTurnControl!
  const { sessionId: rawSessionId } = useParams<{ sessionId: string }>()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const queryClient = useQueryClient()
  const sessionId = rawSessionId ? decodeURIComponent(rawSessionId) : ''
  const [searchParams] = useSearchParams()
  const jobId = searchParams.get('jobId')

  useDocumentTitle(`Session — Mohist`)

  const { data: summary, isLoading: summaryLoading, isError: summaryError } = useSummary(sessionId)
  const { data: transcriptResponse } = useTranscriptResponse(sessionId)
  const { data: launchObservation } = useQuery<AgentLaunchObservationDto>(launchObservationQueryOptions(projectId, jobId))
  const genericFollowup = useFollowup()
  const cancelGeneric = useCancel()

  const initialTurns = useMemo<SessionTurn[]>(() => transcriptResponse?.turns ?? [], [transcriptResponse])

  const meta: SessionMetadata | null = useMemo(() => {
    if (!summary) return null
    return buildGenericSessionMetadata(summary)
  }, [summary])

  const activity = summary?.activity
  const statusKind = deriveSessionStatusKind(activity)
  const isRunning = activity === 'active'
  const canFollowup = canFollowupSession(activity) && !!summary?.runtimeSessionId && !!summary.runtime

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
    runtimeSessionId: summary?.runtimeSessionId ?? '',
    runtime: summary?.runtime ?? null,
    initialTurns: initialTurns.length > 0 ? initialTurns : undefined,
    sessionQueryKeys: [metadataQueryKey, transcriptQueryKey],
    isRunning,
    terminalInvalidationKey: ['agent-session', projectId, sessionId],
  })

  const displayTurns = useMemo(() => turns.map((turn) => projectTranscriptTurn(turn)), [turns, projectTranscriptTurn])

  // Determine back link: Activity-origin links (from=activity) take priority
  // and return to the project-scoped Activity page. Otherwise, if the session
  // carries an issue context ref, link to that issue; otherwise link to the
  // agent profile (and never fabricate issue/workflow links for sessions
  // without an issue binding).
  const fromActivity = searchParams.get('from') === 'activity'

  const emptyStateKind: EmptyStateKind | null = turns.length > 0
    ? null
    : `${deriveSessionStatusKind(activity)}-no-content`

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

  const currentTurnId = summary?.currentTurnId
  const cancelSession = useCallback((operation: 'cancel' | 'stop' = 'stop', options?: SessionCancelOptions) => {
    cancelGeneric.mutate(
      { sessionId, turnId: currentTurnId ?? '', operation, agentRef: summary?.agentId },
      {
        onSuccess: (result) => options?.onSuccess?.({ state: result.state ?? 'not-cancellable' }),
        onSettled: options?.onSettled,
      },
    )
  }, [cancelGeneric, currentTurnId, sessionId, summary?.agentId])

  const cancel = useMemo(
    () => currentTurnId && isRunning && !!summary?.runtimeSessionId && !!summary.runtime
      ? { turnId: currentTurnId, mutate: cancelSession, isPending: cancelGeneric.isPending }
      : null,
    [cancelSession, cancelGeneric.isPending, currentTurnId, isRunning, summary?.runtime, summary?.runtimeSessionId],
  )

  return {
    isLoading: summaryLoading,
    isError: summaryError,
    notFound: !sessionId || (!summary && !summaryLoading && !summaryError),
    sessionKey: sessionId,
    runtimeSessionId: summary?.runtimeSessionId ?? sessionId,
    meta,
    transcriptResponse: transcriptResponse ?? null,
    launchObservation: launchObservation ?? null,
    initialTurns,
    statusKind,
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
    recoverySessionName: null,
    recoverySessionId: sessionId || null,
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
    emptyStateKind,
    issueNumber: 0,
  }
}
