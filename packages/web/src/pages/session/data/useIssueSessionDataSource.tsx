import { useCallback, useEffect, useMemo, useState } from 'react'
import { useParams, useSearchParams, Link } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react'
import { useIssue } from '../../../entities/issue'
import { useCancelSessionMutation, useCoderSessions, getAgentSessionMetadata, getAgentSessionTranscript, useFollowupMutation } from '../../../entities/coder-session'
import type { AgentSessionMetadata, AgentSessionTranscriptResponse, CoderSessionDetail, SessionTurn, WorkflowRunSession } from '../../../entities/coder-session'
import { useProject, useProjectPath } from '../../../entities/project'
import { useSiblingSessions } from '../../../widgets/issue-workflow'
import { useSessionTranscript, projectTurn } from '../../../widgets/session-transcript'
import type { SessionDataSourceResult, StatusKind } from './SessionDataSource'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

export interface IssueSessionDataSourceDependencies {
  useSessionTranscript?: typeof useSessionTranscript
  projectTurn?: typeof projectTurn
  useIssue?: typeof useIssue
  useCoderSessions?: typeof useCoderSessions
  useSiblingSessions?: typeof useSiblingSessions
  getAgentSessionMetadata?: typeof getAgentSessionMetadata
  getAgentSessionTranscript?: typeof getAgentSessionTranscript
  useFollowupMutation?: typeof useFollowupMutation
  useCancelSessionMutation?: typeof useCancelSessionMutation
}

const defaultDependencies: Required<IssueSessionDataSourceDependencies> = {
  useSessionTranscript,
  projectTurn,
  useIssue,
  useCoderSessions,
  useSiblingSessions,
  getAgentSessionMetadata,
  getAgentSessionTranscript,
  useFollowupMutation,
  useCancelSessionMutation,
}

function buildSessionMetadata(
  meta: AgentSessionMetadata,
  lastEventAt: string | null,
  turnCount: number,
  runtimeSessionId: string,
) {
  const isRunning = meta.status === 'active' || meta.status === 'running' || meta.status === 'probing'
  return {
    sessionId: meta.id,
    sessionName: meta.sessionName,
    issueId: '',
    runtimeSessionId: meta.runtimeSessionId ?? runtimeSessionId,
    runtime: meta.runtime ?? null,
    executionId: null,
    title: meta.title,
    status: meta.status,
    statusKind: meta.statusKind
      ?? getSessionStatusKind(meta.status, lastEventAt ?? meta.lastActivityAt, isRunning, meta.completedAt),
    model: meta.model,
    stage: meta.stage,
    createdAt: meta.createdAt,
    completedAt: meta.completedAt,
    lastActivityAt: lastEventAt ?? meta.lastActivityAt ?? meta.lastDataAt ?? null,
    firstPromptSentAt: null,
    lastDataAt: lastEventAt ?? meta.lastDataAt ?? meta.lastActivityAt ?? null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: meta.failureReason ?? null,
    partCount: meta.metadata.partCount,
    toolCount: meta.metadata.toolCount,
    turnCount,
    changedFiles: meta.changedFiles,
    eventSummary: meta.eventSummary
      ? {
          resolvedModel: meta.eventSummary.resolvedModel ?? null,
          failureCategory: meta.eventSummary.failureCategory ?? null,
          toolCallCount: meta.eventSummary.toolCallCount ?? null,
          toolErrorCount: meta.eventSummary.toolErrorCount ?? null,
        }
      : undefined,
    usage: meta.usage
      ? {
          inputTokens: meta.usage.inputTokens ?? null,
          outputTokens: meta.usage.outputTokens ?? null,
          totalTokens: meta.usage.totalTokens ?? null,
          cachedReadTokens: meta.usage.cachedReadTokens ?? null,
          thoughtTokens: meta.usage.thoughtTokens ?? null,
          costAmount: meta.usage.costAmount ?? null,
          costCurrency: meta.usage.costCurrency ?? null,
          contextWindowUsed: meta.usage.contextWindowUsed ?? null,
          contextWindowSize: meta.usage.contextWindowSize ?? null,
          contextUsagePercent: meta.usage.contextUsagePercent ?? null,
          healthStatus: meta.usage.healthStatus ?? null,
        }
      : undefined,
  }
}

function getSessionStatusKind(
  rawStatus: string | undefined,
  lastActivityAt: string | null | undefined,
  isRunning: boolean,
  completedAt?: string | null,
): StatusKind {
  if (rawStatus === 'failed' || rawStatus === 'timeout' || rawStatus === 'cancelled') return 'failed'
  if (rawStatus === 'completed') return 'completed'
  if (rawStatus === 'inactive') return 'stale'
  if (rawStatus === 'probing') return 'probing'
  if (rawStatus === 'active') return lastActivityAt ? 'live' : 'stale'
  if (isRunning && completedAt) return 'finalizing'
  if (!isRunning) return 'completed'
  if (!lastActivityAt) return 'live'
  const lastActivity = new Date(lastActivityAt).getTime()
  const now = Date.now()
  const twoMinutes = 2 * 60 * 1000
  if (now - lastActivity > twoMinutes) return 'stale'
  return 'live'
}

export function useIssueSessionDataSource(
  dependencies: IssueSessionDataSourceDependencies = {},
): SessionDataSourceResult {
  const {
    useSessionTranscript: useTranscript,
    projectTurn: projectTranscriptTurn,
    useIssue: useIssueHook,
    useCoderSessions: useCoderSessionsHook,
    useSiblingSessions: useSiblingSessionsHook,
    getAgentSessionMetadata: fetchAgentSessionMetadata,
    getAgentSessionTranscript: fetchAgentSessionTranscript,
    useFollowupMutation: useFollowup,
    useCancelSessionMutation: useCancel,
  } = { ...defaultDependencies, ...dependencies }
  const { number: numberStr, sessionId, sessionName } = useParams<{ number: string; sessionId?: string; sessionName?: string }>()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const queryClient = useQueryClient()
  const issueNumber = Number(numberStr)
  const decodedSessionId = sessionId ? decodeURIComponent(sessionId) : undefined
  const decodedSessionName = sessionName ? decodeURIComponent(sessionName) : undefined
  useDocumentTitle(`Session — Issue #${issueNumber} — Mohist`)

  const { data: issue } = useIssueHook(issueNumber)
  const { sessions, isLoading: sessionsLoading, isFetching: sessionsFetching, refetch: refetchSessions } = useCoderSessionsHook(issueNumber)
  const session = sessions.find((s) => decodedSessionName
    ? (s.sessionName ?? s.executionId ?? s.id) === decodedSessionName
    : s.id === decodedSessionId)

  // Resolve the route's sessionId segment to the canonical sessionName
  // when the legacy `/issues/:number/session/:sessionId` route is used.
  // Sessions are keyed by sessionName in the workflow-run API; using the
  // raw sessionId as a key would cause a metadata miss for any session
  // whose id and name differ (e.g. compacted/reset sessions). Falls back
  // to the sessionName segment when already on the workflow-sessions route.
  const resolvedSessionName = decodedSessionName
    ?? (decodedSessionId
      ? sessions.find((s) => s.id === decodedSessionId)?.sessionName ?? undefined
      : undefined)

  const isLegacyIdRoute = decodedSessionName == null && decodedSessionId != null
  const [refreshedLegacyId, setRefreshedLegacyId] = useState<string | null>(null)
  useEffect(() => {
    if (!isLegacyIdRoute || !decodedSessionId || resolvedSessionName || sessionsLoading || sessionsFetching || refreshedLegacyId === decodedSessionId) return
    void refetchSessions().then(() => setRefreshedLegacyId(decodedSessionId))
  }, [decodedSessionId, isLegacyIdRoute, refetchSessions, refreshedLegacyId, resolvedSessionName, sessionsFetching, sessionsLoading])

  const siblingNavHook = useSiblingSessionsHook(issue?.workflowRunId ?? null, {
    currentKey: resolvedSessionName ?? decodedSessionId ?? null,
  })

  const lookupKey = resolvedSessionName ?? decodedSessionId

  // The legacy route carries a stable ID, while the detail endpoints are name-keyed.
  // A cached list can omit a newly-created session, so force one list refresh and
  // never issue a detail request with the stable ID as though it were a name.
  const hasRoute = !!lookupKey && !!projectId && issueNumber > 0 && (!isLegacyIdRoute || resolvedSessionName != null)
  const metadataQueryKey = useMemo(
    () => ['issues', issueNumber, projectId, 'agent-session-metadata', lookupKey] as const,
    [issueNumber, projectId, lookupKey],
  )
  const transcriptQueryKey = useMemo(
    () => ['issues', issueNumber, projectId, 'agent-session-transcript', lookupKey] as const,
    [issueNumber, projectId, lookupKey],
  )

  const handleRecoverySuccess = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: metadataQueryKey })
    queryClient.invalidateQueries({ queryKey: transcriptQueryKey })
    queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions'] })
  }, [queryClient, metadataQueryKey, transcriptQueryKey, issueNumber])

  const {
    data: metadata,
    isLoading: metadataLoading,
    isError: metadataError,
  } = useQuery<AgentSessionMetadata | null, Error>({
    queryKey: metadataQueryKey,
    queryFn: async () => {
      if (!lookupKey) return null
      return fetchAgentSessionMetadata(issueNumber, lookupKey, projectId)
    },
    enabled: hasRoute,
  })

  const { data: transcriptResponse } = useQuery<AgentSessionTranscriptResponse | null, Error>({
    queryKey: transcriptQueryKey,
    queryFn: async () => {
      if (!lookupKey) return null
      return fetchAgentSessionTranscript(issueNumber, lookupKey, projectId)
    },
    enabled: hasRoute && !!metadata,
  })

  const initialTurns = useMemo<SessionTurn[]>(() => transcriptResponse?.turns ?? [], [transcriptResponse])

  const lastEventAt = useMemo(() => {
    return transcriptResponse?.lastActivityAt ?? metadata?.lastActivityAt ?? metadata?.lastDataAt ?? null
  }, [transcriptResponse, metadata?.lastActivityAt, metadata?.lastDataAt])

  const detail: CoderSessionDetail | null = useMemo(() => {
    if (!metadata) return null
    const turnCount = transcriptResponse?.turns.length ?? 0
    return {
      id: metadata.id,
      runtimeSessionId: metadata.runtimeSessionId ?? session?.runtimeSessionId ?? '',
      executionId: null,
      taskDescription: metadata.title,
      status: metadata.status,
      createdAt: metadata.createdAt,
      completedAt: metadata.completedAt,
      model: metadata.model,
      runtime: metadata.runtime ?? null,
      stage: metadata.stage,
      title: metadata.title,
      metadata: buildSessionMetadata(metadata, lastEventAt, turnCount, metadata.runtimeSessionId ?? session?.runtimeSessionId ?? ''),
      turns: initialTurns,
      incomplete: false,
    }
  }, [metadata, transcriptResponse, initialTurns, lastEventAt, session?.runtimeSessionId])

  const rawStatus = detail?.metadata?.status ?? detail?.status ?? session?.status
  const apiStatusKind = detail?.metadata?.statusKind
  const isRunning = (rawStatus === 'active' || rawStatus === 'running' || rawStatus === 'probing') && apiStatusKind !== 'completed' && apiStatusKind !== 'failed'
  const runtimeSessionId = detail?.runtimeSessionId ?? session?.runtimeSessionId ?? ''

  const statusKind: StatusKind = detail
    ? (detail.metadata.statusKind ?? getSessionStatusKind(rawStatus, detail.metadata.lastActivityAt, isRunning, detail.metadata.completedAt ?? detail.completedAt))
    : getSessionStatusKind(rawStatus, undefined, isRunning, session?.completedAt)

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
    issueNumber,
    sessionId: detail?.id ?? decodedSessionId ?? decodedSessionName ?? '',
    runtimeSessionId,
    initialTurns: initialTurns.length > 0 ? initialTurns : undefined,
    sessionQueryKeys: [metadataQueryKey, transcriptQueryKey],
    isRunning,
  })

  const displayStatusKind: StatusKind = isFinalizing && isRunning ? 'finalizing' : statusKind

  const displayTurns = useMemo(() => turns.map((turn) => projectTranscriptTurn(turn)), [turns, projectTranscriptTurn])

  const recoverySessionName = detail?.metadata?.sessionName ?? session?.sessionName ?? session?.executionId ?? lookupKey ?? ''
  const followup = useFollowup()
  const cancelMutation = useCancel()
  const isTerminal = rawStatus === 'completed' || rawStatus === 'failed' || rawStatus === 'cancelled' || rawStatus === 'stopped'
  const canFollowup = !isTerminal && !!runtimeSessionId && !!recoverySessionName
  const sendFollowup = useCallback((text: string) => {
    followup.mutate({ issueNumber, sessionName: recoverySessionName, text })
  }, [followup, issueNumber, recoverySessionName])
  const cancelSession = useCallback((options?: { onSettled?: () => void }) => {
    cancelMutation.mutate(
      { issueNumber, sessionName: recoverySessionName },
      { onSettled: options?.onSettled },
    )
  }, [cancelMutation, issueNumber, recoverySessionName])
  const cancel = useMemo(
    () => isRunning && runtimeSessionId && recoverySessionName
      ? { mutate: cancelSession, isPending: cancelMutation.isPending }
      : null,
    [cancelMutation.isPending, cancelSession, isRunning, recoverySessionName, runtimeSessionId],
  )

  const [searchParams] = useSearchParams()
  const fromActivity = searchParams.get('from') === 'activity'
  const runtimeLineage = metadata?.runtimeSessionLineage ?? null
  const viewedRuntimeSessionId = searchParams.get('rt') ?? metadata?.runtimeSessionId ?? null

  const buildLineageTargetPath = runtimeLineage && runtimeLineage.length >= 2
    ? (runtimeId: string) => {
        const base = toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(recoverySessionName)}`)
        const params = new URLSearchParams({ rt: runtimeId })
        if (fromActivity) params.set('from', 'activity')
        return `${base}?${params}`
      }
    : null

  const hasRecoveryActions = !!recoverySessionName
  const recoverySessionNameStr = recoverySessionName

  const currentSiblingKey = resolvedSessionName ?? decodedSessionId ?? null

  const siblingNav = (
    <SiblingNavigation
      issueNumber={issueNumber}
      previous={siblingNavHook.previous}
      next={siblingNavHook.next}
      fromActivity={fromActivity}
    />
  )

  const siblingSidebar = (
    <SiblingSessionsSidebar
      issueNumber={issueNumber}
      siblings={siblingNavHook.sessions}
      currentKey={currentSiblingKey}
      fromActivity={fromActivity}
    />
  )

  const isDetailError = metadataError || (!metadata && !session)
  const backPath = fromActivity
    ? toProjectPath('/activity')
    : toProjectPath(`/issues/${issueNumber}`)
  const backLabel = fromActivity ? 'Activity' : `Issue #${issueNumber}`
  const workflowContextPath = toProjectPath(`/issues/${issueNumber}`)
  const workflowContextLabel = 'Workflow context'
  return {
    isLoading: sessionsLoading || (isLegacyIdRoute && !resolvedSessionName && sessionsFetching) || metadataLoading,
    isError: isDetailError,
    notFound: !lookupKey || isNaN(issueNumber) || issueNumber <= 0 || (!detail && !sessionsLoading && !metadataLoading && !isDetailError),
    sessionKey: lookupKey ?? '',
    runtimeSessionId,
    meta: detail?.metadata ?? null,
    transcriptResponse: transcriptResponse ?? null,
    initialTurns,
    statusKind: displayStatusKind,
    isRunning,
    canFollowup,
    followupIsPending: followup.isPending,
    sendFollowup,
    cancel,
    contextWindowUsed: detail?.metadata?.usage?.contextWindowUsed ?? null,
    contextWindowSize: detail?.metadata?.usage?.contextWindowSize ?? null,
    contextUsagePercent: detail?.metadata?.usage?.contextUsagePercent ?? null,
    healthStatus: detail?.metadata?.usage?.healthStatus ?? null,
    hasRecoveryActions,
    recoverySessionName: recoverySessionNameStr,
    recoverySessionId: null,
    runtimeSessionLineage: runtimeLineage,
    viewedRuntimeSessionId,
    buildLineageTargetPath,
    metadataQueryKey,
    transcriptQueryKey,
    handleRecoverySuccess,
    backPath,
    backLabel,
    issueTitle: issue?.title,
    workflowContextPath,
    workflowContextLabel,
    siblingNav,
    siblingSidebar,
    sessionTurns: turns,
    transcriptVersion,
    scrollToBottom,
    newContentAvailable,
    setIsNearBottom,
    isFinalizing,
    isThinking,
    isStreaming,
    displayTurns,
    issueNumber,
  }
}

function SiblingNavigation({
  issueNumber,
  previous,
  next,
  fromActivity,
}: {
  issueNumber: number
  previous: WorkflowRunSession | null
  next: WorkflowRunSession | null
  fromActivity: boolean
}) {
  const toProjectPath = useProjectPath()
  const sessionPath = (sessionName: string) => {
    const path = toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(sessionName)}`)
    return fromActivity ? `${path}?from=activity` : path
  }
  return (
    <div className="flex max-w-full min-w-0 flex-wrap items-center gap-1" data-testid="session-sibling-navigation">
      {previous ? (
        <Link
          to={sessionPath(previous.sessionName)}
          className="inline-flex max-w-full min-w-0 items-center gap-1 rounded border border-border bg-background px-2 py-1 text-xs font-medium text-muted-foreground transition-colors hover:border-info-border hover:bg-info-subtle hover:text-info"
          data-testid="session-sibling-prev"
          title={`Previous session: ${previous.sessionName}`}
          aria-label={`Previous session: ${previous.sessionName}`}
        >
          <ChevronLeftIcon className="h-3.5 w-3.5" aria-hidden="true" />
          <span className="min-w-0 truncate font-mono">prev: {previous.sessionName}</span>
        </Link>
      ) : (
        <span
          className="inline-flex items-center gap-1 rounded border border-border bg-muted px-2 py-1 text-xs font-medium text-muted-foreground/60 cursor-not-allowed"
          data-testid="session-sibling-prev-disabled"
          aria-disabled="true"
          title="No previous session"
        >
          <ChevronLeftIcon className="h-3.5 w-3.5" aria-hidden="true" />
          <span className="font-mono">prev</span>
        </span>
      )}
      {next ? (
        <Link
          to={sessionPath(next.sessionName)}
          className="inline-flex max-w-full min-w-0 items-center gap-1 rounded border border-border bg-background px-2 py-1 text-xs font-medium text-muted-foreground transition-colors hover:border-info-border hover:bg-info-subtle hover:text-info"
          data-testid="session-sibling-next"
          title={`Next session: ${next.sessionName}`}
          aria-label={`Next session: ${next.sessionName}`}
        >
          <span className="min-w-0 truncate font-mono">next: {next.sessionName}</span>
          <ChevronRightIcon className="h-3.5 w-3.5" aria-hidden="true" />
        </Link>
      ) : (
        <span
          className="inline-flex items-center gap-1 rounded border border-border bg-muted px-2 py-1 text-xs font-medium text-muted-foreground/60 cursor-not-allowed"
          data-testid="session-sibling-next-disabled"
          aria-disabled="true"
          title="No next session"
        >
          <span className="font-mono">next</span>
          <ChevronRightIcon className="h-3.5 w-3.5" aria-hidden="true" />
        </span>
      )}
    </div>
  )
}

export function isCurrentSiblingSession(sibling: Pick<WorkflowRunSession, 'id' | 'sessionName'>, currentKey: string | null): boolean {
  return sibling.sessionName === currentKey || sibling.id === currentKey
}

function SiblingSessionsSidebar({
  issueNumber,
  siblings,
  currentKey,
  fromActivity,
}: {
  issueNumber: number
  siblings: WorkflowRunSession[]
  currentKey: string | null
  fromActivity: boolean
}) {
  const toProjectPath = useProjectPath()
  if (siblings.length === 0) return null

  return (
    <aside
      className="hidden xl:flex w-64 shrink-0 flex-col border-l border-border bg-background"
      data-testid="session-sibling-sidebar"
      aria-label="Sibling sessions"
    >
      <div className="px-3 py-2 border-b border-border text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        Sibling sessions
      </div>
      <nav className="flex-1 overflow-y-auto p-1">
        {siblings.map((sibling) => {
          const isCurrent = isCurrentSiblingSession(sibling, currentKey)
          const basePath = toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(sibling.sessionName)}`)
          const path = fromActivity ? `${basePath}?from=activity` : basePath
          return (
            <Link
              key={sibling.id}
              to={path}
              data-testid="session-sibling-sidebar-entry"
              data-current={isCurrent ? 'true' : 'false'}
              data-tone={isCurrent ? 'info' : 'neutral'}
              title={`Open ${sibling.sessionName} transcript`}
              aria-current={isCurrent ? 'page' : undefined}
              className={`flex items-center gap-2 rounded px-2 py-1.5 text-xs transition-colors min-w-0 ${
                isCurrent ? 'bg-info-subtle text-info font-medium border border-info-border' : 'text-muted-foreground hover:bg-muted border border-transparent'
              }`}
            >
              <span
                data-testid="session-sibling-status-dot"
                data-tone={
                  sibling.status === 'completed'
                    ? 'success'
                    : sibling.status === 'failed' || sibling.status === 'cancelled'
                      ? 'danger'
                      : sibling.status === 'running' || sibling.status === 'active' || sibling.status === 'probing'
                        ? 'info'
                        : 'neutral'
                }
                className={`inline-block h-1.5 w-1.5 shrink-0 rounded-full ${
                  sibling.status === 'completed'
                    ? 'bg-success'
                    : sibling.status === 'failed' || sibling.status === 'cancelled'
                      ? 'bg-danger'
                      : sibling.status === 'running' || sibling.status === 'active' || sibling.status === 'probing'
                        ? 'bg-info'
                        : 'bg-muted-foreground/60'
                }`}
                aria-hidden="true"
              />
              <span className="min-w-0 flex-1 truncate font-mono">{sibling.sessionName}</span>
              {isCurrent && (
                <span
                  data-testid="session-sibling-current-label"
                  className="shrink-0 text-[10px] uppercase tracking-wide text-info"
                >
                  current
                </span>
              )}
            </Link>
          )
        })}
      </nav>
    </aside>
  )
}
