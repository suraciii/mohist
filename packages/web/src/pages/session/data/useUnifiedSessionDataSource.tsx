import { useCallback, useMemo, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useProject, useProjectPath } from '../../../entities/project'
import { launchObservationQueryOptions, useGenericFollowup, useGenericTurnControl } from '../../../entities/agent'
import type { AgentLaunchObservationDto } from '../../../entities/agent'
import {
  canFollowupSession,
  deriveSessionStatusKind,
  useUnifiedSessionSummary,
  useUnifiedSessionTranscript,
} from '../../../entities/coder-session'
import type { SessionFollowupResult, SessionMetadata, SessionTurn, UnifiedSessionSummaryDto } from '../../../entities/coder-session'
import { projectTurn, useSessionTranscript } from '../../../widgets/session-transcript'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { createIdempotencyKey } from '../../../shared/lib/idempotency-key'
import { resolveFollowupStatus } from './followupStatus'
import type { EmptyStateKind, SessionCancelOptions, SessionDataSourceResult, SessionTurnControlHandle } from './SessionDataSource'

export interface UnifiedSessionDataSourceDependencies {
  useSessionTranscript: typeof useSessionTranscript
  projectTurn: typeof projectTurn
  useUnifiedSessionSummary: typeof useUnifiedSessionSummary
  useUnifiedSessionTranscript: typeof useUnifiedSessionTranscript
  useGenericFollowup: typeof useGenericFollowup
  useGenericTurnControl: typeof useGenericTurnControl
}

const defaultDependencies: UnifiedSessionDataSourceDependencies = {
  useSessionTranscript,
  projectTurn,
  useUnifiedSessionSummary,
  useUnifiedSessionTranscript,
  useGenericFollowup,
  useGenericTurnControl,
}

function buildMetadata(summary: UnifiedSessionSummaryDto, turnCount: number): SessionMetadata {
  return {
    sessionId: summary.id,
    sessionName: summary.sessionName ?? summary.agentName ?? null,
    source: summary.source,
    agentId: summary.agentId ?? null,
    agentName: summary.agentName ?? null,
    workflowRunId: summary.workflowRunId ?? null,
    runtimeSessionId: summary.runtimeSessionId ?? '',
    runtime: summary.runtime,
    executionId: null,
    title: summary.sessionName ?? summary.agentName ?? 'Session',
    activity: deriveSessionStatusKind(summary.activity),
    model: summary.model,
    stage: null,
    createdAt: summary.createdAt,
    completedAt: null,
    lastActivityAt: summary.lastActivityAt,
    firstPromptSentAt: null,
    lastDataAt: summary.lastActivityAt,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: summary.failureReason,
    partCount: 0,
    toolCount: summary.toolCallCount ?? 0,
    turnCount,
    changedFiles: undefined,
    eventSummary: {
      resolvedModel: summary.resolvedModel,
      failureCategory: summary.failureCategory,
      toolCallCount: summary.toolCallCount,
      toolErrorCount: summary.toolErrorCount,
    },
    usage: summary.usage,
    inputs: summary.inputs,
    turns: summary.turns,
  }
}

export function useUnifiedSessionDataSource(
  dependencies: Partial<UnifiedSessionDataSourceDependencies> = {},
): SessionDataSourceResult {
  const {
    useSessionTranscript: useTranscript,
    projectTurn: projectTranscriptTurn,
    useUnifiedSessionSummary: useSummary,
    useUnifiedSessionTranscript: useTranscriptResponse,
    useGenericFollowup: useFollowup,
    useGenericTurnControl: useTurnControl,
  } = { ...defaultDependencies, ...dependencies }
  const { sessionId: rawSessionId } = useParams<{ sessionId: string }>()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const queryClient = useQueryClient()
  const [searchParams] = useSearchParams()
  const sessionId = rawSessionId ? decodeURIComponent(rawSessionId) : ''
  const jobId = searchParams.get('jobId')

  useDocumentTitle('Session — Mohist')

  const { data: summary, isLoading: summaryLoading, isError: summaryError } = useSummary(sessionId)
  const { data: transcriptResponse } = useTranscriptResponse(sessionId, summary?.runtimeSessionId)
  const { data: launchObservation } = useQuery<AgentLaunchObservationDto>(launchObservationQueryOptions(projectId, jobId))
  const followup = useFollowup()
  const turnControl = useTurnControl()
  const followupKeys = useRef(new Map<string, string>())
  const [followupResult, setFollowupResult] = useState<SessionFollowupResult | null>(null)

  const initialTurns = useMemo<SessionTurn[]>(() => transcriptResponse?.turns ?? [], [transcriptResponse])
  const meta = useMemo<SessionMetadata | null>(
    () => summary ? buildMetadata(summary, initialTurns.length) : null,
    [initialTurns.length, summary],
  )
  const activity = summary?.activity
  const statusKind = deriveSessionStatusKind(activity)
  const isRunning = activity === 'active'
  const runtimeSessionId = summary?.runtimeSessionId ?? ''
  const canFollowup = canFollowupSession(activity) && !!runtimeSessionId && !!summary?.runtime
  const metadataQueryKey = useMemo(
    () => ['unified-session', projectId, sessionId] as const,
    [projectId, sessionId],
  )
  const transcriptQueryKey = useMemo(
    () => ['unified-session', projectId, sessionId, 'transcript', runtimeSessionId || null] as const,
    [projectId, runtimeSessionId, sessionId],
  )

  const reconcileUnifiedQueries = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: metadataQueryKey })
    queryClient.invalidateQueries({ queryKey: transcriptQueryKey })
    queryClient.invalidateQueries({ queryKey: ['unified-session', projectId, sessionId, 'transcript'] })
    queryClient.invalidateQueries({ queryKey: ['agent-sessions'] })
    queryClient.invalidateQueries({ queryKey: ['workflow-runs'] })
    queryClient.invalidateQueries({ queryKey: ['agents', projectId] })
    queryClient.invalidateQueries({ queryKey: ['workflow-run-sessions'] })
  }, [metadataQueryKey, projectId, queryClient, sessionId, transcriptQueryKey])

  const handleRecoverySuccess = useCallback(() => {
    reconcileUnifiedQueries()
  }, [reconcileUnifiedQueries])

  const transcript = useTranscript({
    issueNumber: 0,
    projectId,
    sessionId,
    runtimeSessionId,
    runtime: summary?.runtime ?? null,
    initialTurns: initialTurns.length > 0 ? initialTurns : undefined,
    sessionQueryKeys: [metadataQueryKey, transcriptQueryKey],
    isRunning,
    terminalInvalidationKey: metadataQueryKey,
  })
  const displayTurns = useMemo(
    () => transcript.turns.map((turn) => projectTranscriptTurn(turn)),
    [projectTranscriptTurn, transcript.turns],
  )

  const sendFollowup = useCallback(async (text: string, attachmentIds: string[] = []) => {
    const retryKey = `${sessionId}:${text}:${attachmentIds.join(',')}`
    const idempotencyKey = followupKeys.current.get(retryKey) ?? createIdempotencyKey()
    followupKeys.current.set(retryKey, idempotencyKey)
    let result: SessionFollowupResult
    try {
      result = (await followup.mutateAsync({ sessionId, text, attachments: attachmentIds, idempotencyKey })) as SessionFollowupResult
    } catch (error) {
      followupKeys.current.set(retryKey, idempotencyKey)
      throw error
    }
    const normalized = result
    setFollowupResult(normalized)
    reconcileUnifiedQueries()
    if (normalized.status === 'unknown') {
      throw new Error('Follow-up outcome is unknown. Retry with the same key.')
    }
    followupKeys.current.delete(retryKey)
    return normalized
  }, [followup, reconcileUnifiedQueries, sessionId])

  const followupStatus = useMemo(() => {
    if (!followupResult) return null
    const input = summary?.inputs?.find((candidate) => candidate.id === followupResult.inputId)
    const turn = summary?.turns?.find((candidate) => candidate.id === followupResult.turnId)
    return resolveFollowupStatus(followupResult, input, turn)
  }, [followupResult, summary?.inputs, summary?.turns])

  const cancelSession = useCallback((operation: 'cancel' | 'stop', options?: SessionCancelOptions) => {
    if (!summary?.currentTurnId) {
      options?.onSettled?.()
      return
    }
    turnControl.mutate(
      { sessionId, turnId: summary.currentTurnId, operation },
      {
        onSuccess: (result) => {
          reconcileUnifiedQueries()
          options?.onSuccess?.({ state: result.state ?? 'unknown' })
        },
        onSettled: () => {
          reconcileUnifiedQueries()
          options?.onSettled?.()
        },
      },
    )
  }, [reconcileUnifiedQueries, sessionId, summary?.currentTurnId, turnControl])

  const currentTurn = useMemo(() => {
    if (!summary?.currentTurnId || !summary.turns) return null
    return summary.turns.find((turn) => turn.id === summary.currentTurnId) ?? null
  }, [summary?.currentTurnId, summary?.turns])

  const cancel = useMemo<SessionTurnControlHandle | null>(() => {
    if (!summary?.currentTurnId || !isRunning || !runtimeSessionId || !summary.runtime) return null
    if (currentTurn?.status !== 'queued') return null
    return {
      turnId: summary.currentTurnId,
      state: 'queued',
      mutate: (options) => cancelSession('cancel', options),
      isPending: turnControl.isPending,
    }
  }, [cancelSession, currentTurn?.status, isRunning, runtimeSessionId, summary?.currentTurnId, summary?.runtime, turnControl.isPending])

  const stop = useMemo<SessionTurnControlHandle | null>(() => {
    if (!summary?.currentTurnId || !isRunning || !runtimeSessionId || !summary.runtime) return null
    if (currentTurn?.status !== 'executing') return null
    return {
      turnId: summary.currentTurnId,
      state: 'executing',
      mutate: (options) => cancelSession('stop', options),
      isPending: turnControl.isPending,
    }
  }, [cancelSession, currentTurn?.status, isRunning, runtimeSessionId, summary?.currentTurnId, summary?.runtime, turnControl.isPending])

  const fromActivity = searchParams.get('from') === 'activity'
  const issueNumber = summary?.contextRefs?.issueNumber ?? 0
  const workflowContextPath = issueNumber > 0 ? toProjectPath(`/issues/${issueNumber}`) : undefined
  const workflowContextLabel = workflowContextPath ? 'Workflow context' : undefined
  const backPath = fromActivity
    ? toProjectPath('/activity')
    : summary?.source === 'workflow' && workflowContextPath
      ? workflowContextPath
      : summary?.agentId
        ? toProjectPath(`/agents/${encodeURIComponent(summary.agentId)}`)
        : toProjectPath('/agents')
  const backLabel = fromActivity
    ? 'Activity'
    : summary?.source === 'workflow' && workflowContextPath
      ? `Issue #${issueNumber}`
      : summary?.agentName ?? 'Agents'
  const emptyStateKind: EmptyStateKind | null = transcript.turns.length > 0
    ? null
    : `${statusKind}-no-content`

  return {
    isLoading: summaryLoading,
    isError: summaryError,
    notFound: !sessionId || (!summary && !summaryLoading && !summaryError),
    sessionKey: sessionId,
    runtimeSessionId,
    meta,
    transcriptResponse: transcriptResponse ?? null,
    launchObservation: launchObservation ?? null,
    initialTurns,
    statusKind,
    isRunning,
    canFollowup,
    supportsInputAttachments: true,
    projectId,
    followupIsPending: followup.isPending,
    followupStatus,
    sendFollowup,
    cancel,
    stop,
    contextWindowUsed: meta?.usage?.contextWindowUsed ?? null,
    contextWindowSize: meta?.usage?.contextWindowSize ?? null,
    contextUsagePercent: meta?.usage?.contextUsagePercent ?? null,
    healthStatus: meta?.usage?.healthStatus ?? null,
    hasRecoveryActions: !!summary,
    recoveryAvailable: summary?.recoveryAvailable ?? false,
    recoverySessionName: summary?.sessionName ?? summary?.agentName ?? sessionId,
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
    sessionTurns: transcript.turns,
    transcriptVersion: transcript.transcriptVersion,
    scrollToBottom: transcript.scrollToBottom,
    newContentAvailable: transcript.newContentAvailable,
    setIsNearBottom: transcript.setIsNearBottom,
    isFinalizing: transcript.isFinalizing,
    isThinking: transcript.isThinking,
    isStreaming: transcript.isStreaming,
    displayTurns,
    emptyStateKind,
    issueNumber,
  }
}
