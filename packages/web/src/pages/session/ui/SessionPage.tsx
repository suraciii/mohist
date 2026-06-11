import { useEffect, useRef, useCallback, useMemo } from 'react'
import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useIssue } from '../../../entities/issue'
import { useCoderSessions } from '../../../entities/coder-session'
import { getAgentSessionMetadata, getAgentSessionEvents } from '../../../entities/coder-session'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'
import { useProject, useProjectPath } from '../../../entities/project'
import { useSessionTranscript, projectTurn } from '../../../widgets/session-transcript'
import type { AgentSessionMetadata, AgentSessionEvent, SessionMetadata, SessionStatusKind, CoderSessionDetail } from '../../../entities/coder-session'
import { SessionTranscriptLayout } from '../../../widgets/session-transcript'
import { Button } from '@/shared/ui/components/button'

type StatusKind = SessionStatusKind

const EMPTY_EVENTS: AgentSessionEvent[] = []

function countTurnsFromEvents(events: AgentSessionEvent[]): number {
  let count = 0
  for (const event of events) {
    if (event.type === 'mohist_prompt') count += 1
  }
  return count
}

function buildSessionMetadata(
  meta: AgentSessionMetadata,
  lastEventAt: string | null,
  turnCount: number,
  acpSessionId: string,
): SessionMetadata {
  const isRunning = meta.status === 'running' || meta.status === 'probing'
  return {
    sessionId: meta.id,
    sessionName: meta.sessionName,
    coderSessionId: meta.id,
    issueId: '',
    acpSessionId: meta.acpSessionId ?? acpSessionId,
    executionId: null,
    title: meta.title,
    status: meta.status,
    statusKind: meta.statusKind
      ?? getSessionStatusKind(meta.status, lastEventAt ?? meta.lastActivityAt, isRunning, meta.completedAt),
    model: meta.model,
    stage: meta.stage,
    createdAt: meta.createdAt,
    completedAt: meta.completedAt,
    lastActivityAt: lastEventAt,
    firstPromptSentAt: null,
    lastDataAt: lastEventAt,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    eventCount: meta.metadata.eventCount,
    toolCount: meta.metadata.toolCount,
    turnCount,
    changedFiles: meta.changedFiles,
    resolvedModel: meta.resolvedModel ?? null,
    inputTokens: meta.inputTokens ?? null,
    outputTokens: meta.outputTokens ?? null,
    totalTokens: meta.totalTokens ?? null,
    cachedReadTokens: meta.cachedReadTokens ?? null,
    thoughtTokens: meta.thoughtTokens ?? null,
    costAmount: meta.costAmount ?? null,
    costCurrency: meta.costCurrency ?? null,
    contextWindowUsed: meta.contextWindowUsed ?? null,
    contextWindowSize: meta.contextWindowSize ?? null,
    failureCategory: meta.failureCategory ?? null,
    toolCallCount: meta.toolCallCount ?? null,
    toolErrorCount: meta.toolErrorCount ?? null,
  }
}

function formatDuration(ms: number): string {
  if (ms < 0) return '0s'
  const totalSec = Math.floor(ms / 1000)
  if (totalSec < 60) return `${totalSec}s`
  const min = Math.floor(totalSec / 60)
  const sec = totalSec % 60
  if (min < 60) return `${min}m ${String(sec).padStart(2, '0')}s`
  const hr = Math.floor(min / 60)
  const remMin = min % 60
  return `${hr}h ${String(remMin).padStart(2, '0')}m`
}

function formatRelativeTime(dateStr: string | null | undefined): string {
  if (!dateStr) return 'never'
  const date = new Date(dateStr)
  const now = Date.now()
  const diff = now - date.getTime()
  if (diff < 60000) return 'just now'
  if (diff < 3600000) return `${Math.floor(diff / 60000)}m ago`
  if (diff < 86400000) return `${Math.floor(diff / 3600000)}h ago`
  return date.toLocaleDateString()
}

function getSessionStatusKind(
  rawStatus: string | undefined,
  lastActivityAt: string | null | undefined,
  isRunning: boolean,
  completedAt?: string | null,
): StatusKind {
  if (rawStatus === 'failed' || rawStatus === 'timeout' || rawStatus === 'cancelled') {
    return 'failed'
  }
  if (rawStatus === 'completed') return 'completed'
  if (rawStatus === 'probing') return 'probing'
  if (isRunning && completedAt) return 'finalizing'
  if (!isRunning) {
    return 'completed'
  }
  if (!lastActivityAt) return 'live'
  const lastActivity = new Date(lastActivityAt).getTime()
  const now = Date.now()
  const twoMinutes = 2 * 60 * 1000
  if (now - lastActivity > twoMinutes) return 'stale'
  return 'live'
}

function getStageLabel(stage: string | null): string {
  if (!stage) return 'Session'
  return stage.charAt(0).toUpperCase() + stage.slice(1)
}

function StatusBadge({ kind, failureReason }: { kind: StatusKind; failureReason?: string | null }) {
  const config: Record<StatusKind, { label: string; color: string; dot?: boolean }> = {
    loading: { label: 'Loading', color: 'bg-gray-100 text-gray-600' },
    live: { label: 'Running', color: 'bg-blue-100 text-blue-700', dot: true },
    probing: { label: 'Checking session', color: 'bg-yellow-100 text-yellow-700', dot: true },
    finalizing: { label: 'Finalizing', color: 'bg-yellow-100 text-yellow-700' },
    completed: { label: 'Completed', color: 'bg-green-100 text-green-700' },
    failed: { label: 'Session failed', color: 'bg-red-100 text-red-700' },
    stale: { label: 'Stale', color: 'bg-orange-100 text-orange-700' },
  }
  const { label, color, dot } = config[kind]
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${color}`}>
      {dot && (
        <span className="relative flex h-2 w-2">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-current opacity-75" />
          <span className="relative inline-flex rounded-full h-2 w-2 bg-current" />
        </span>
      )}
      {label}
      {kind === 'failed' && failureReason && (
        <span className="ml-1 text-red-500 truncate max-w-[200px]" title={failureReason}>
          {failureReason}
        </span>
      )}
    </span>
  )
}

function SessionNotFound({ issueNumber }: { issueNumber: number }) {
  const toProjectPath = useProjectPath()
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-gray-400 text-lg">Session not found</div>
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="text-sm text-blue-600 hover:text-blue-800 underline"
        >
          Back to issue #{issueNumber}
        </Link>
      </div>
    </div>
  )
}

function SessionLoadingState() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-gray-400">Loading session...</div>
    </div>
  )
}

function SessionApiErrorState({ issueNumber }: { issueNumber: number }) {
  const toProjectPath = useProjectPath()
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-red-400 text-lg">Failed to load session</div>
        <p className="text-gray-500 text-sm">An error occurred while fetching session data.</p>
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="text-sm text-blue-600 hover:text-blue-800 underline"
        >
          Back to issue #{issueNumber}
        </Link>
      </div>
    </div>
  )
}

function SessionWaitingState() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-blue-400 text-lg">Waiting for activity...</div>
        <p className="text-gray-500 text-sm">The session has started but no activity recorded yet.</p>
      </div>
    </div>
  )
}

function SessionEmptyState({ issueNumber }: { issueNumber: number }) {
  const toProjectPath = useProjectPath()
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-gray-400 text-lg">No activity recorded for this session</div>
        <p className="text-gray-500 text-sm">This session has no recorded transcript data.</p>
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="text-sm text-blue-600 hover:text-blue-800 underline"
        >
          Back to issue #{issueNumber}
        </Link>
      </div>
    </div>
  )
}

function SessionLegacyMissingState() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-yellow-500 text-lg">Incomplete Session Data</div>
        <p className="text-gray-500 text-sm">Prompt was not recorded for this historical session.</p>
        <p className="text-gray-400 text-xs">Only activity logs are available.</p>
      </div>
    </div>
  )
}

function JumpToBottomButton({ onClick }: { onClick: () => void }) {
  return (
    <Button
      onClick={onClick}
      className="absolute bottom-4 right-4 rounded-full bg-gray-800 text-xs text-white shadow-lg hover:bg-gray-700"
    >
      <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 17a.75.75 0 01-.75-.75V5.612L5.29 9.77a.75.75 0 01-1.08-1.04l5.25-5.5a.75.75 0 011.08 0l5.25 5.5a.75.75 0 11-1.08 1.04l-3.96-4.158V16.25A.75.75 0 0110 17z" clipRule="evenodd" />
      </svg>
      Jump to bottom
    </Button>
  )
}

interface SessionHeaderProps {
  issueNumber: number
  issueTitle?: string
  meta: CoderSessionDetail['metadata']
  statusKind: StatusKind
  turnCount: number
}

function SessionHeader({ issueNumber, issueTitle, meta, statusKind, turnCount }: SessionHeaderProps) {
  const toProjectPath = useProjectPath()
  const isTerminal = statusKind === 'completed' || statusKind === 'failed'
  const createdAt = meta?.createdAt ?? new Date().toISOString()
  const completedAt = meta?.completedAt ?? null
  const duration = isTerminal && completedAt
    ? new Date(completedAt).getTime() - new Date(createdAt).getTime()
    : isTerminal
      ? Date.now() - new Date(createdAt).getTime()
      : 0

  const changedFiles = meta?.changedFiles
  const fileSummary = changedFiles && changedFiles.length > 0
    ? changedFiles.length === 1
      ? `1 file changed`
      : `${changedFiles.length} files changed`
    : null

  const hasUsage =
    meta?.totalTokens != null ||
    meta?.inputTokens != null ||
    meta?.outputTokens != null ||
    meta?.cachedReadTokens != null ||
    meta?.thoughtTokens != null

  const contextWindowPct =
    meta?.contextWindowUsed != null && meta?.contextWindowSize != null && meta.contextWindowSize > 0
      ? Math.min(100, Math.round((meta.contextWindowUsed / meta.contextWindowSize) * 100))
      : null

  return (
    <div className="border-b border-gray-200 bg-white px-4 py-3 shrink-0">
      <div className="flex items-center gap-2 text-sm mb-2">
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="flex items-center gap-1 text-blue-600 hover:text-blue-800 transition-colors"
        >
          <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z" clipRule="evenodd" />
          </svg>
          <span>Issue #{issueNumber}</span>
        </Link>
        {issueTitle && (
          <>
            <span className="text-gray-300">/</span>
            <span className="text-gray-500 truncate max-w-[300px]">{issueTitle}</span>
          </>
        )}
      </div>

      <div className="flex items-center gap-3">
        <div className="flex items-center gap-2">
          <h1 className="text-lg font-semibold text-gray-900">
            {meta?.title ?? 'Session'}
          </h1>
        </div>

        <div className="flex items-center gap-2 text-xs text-gray-500 ml-auto shrink-0 flex-wrap justify-end">
          <StatusBadge kind={statusKind} failureReason={meta?.failureReason} />
          <span className="px-2 py-0.5 rounded-full bg-gray-100 text-gray-600 font-medium">
            {getStageLabel(meta?.stage ?? null)}
          </span>

          {/* Model badges */}
          {meta?.model && meta?.resolvedModel && meta.model !== meta.resolvedModel ? (
            <span className="text-gray-500">
              {meta.model} <span className="text-gray-300">→</span>{' '}
              <span className="text-blue-600">{meta.resolvedModel}</span>
            </span>
          ) : meta?.model ? (
            <span>{meta.model}</span>
          ) : null}

          <span className="text-gray-300">·</span>
          <span>{turnCount} turn{turnCount !== 1 ? 's' : ''}</span>

          {meta?.lastActivityAt && (
            <>
              <span className="text-gray-300">·</span>
              <span title={`Last activity: ${meta.lastActivityAt}`}>
                {formatRelativeTime(meta.lastActivityAt)}
              </span>
            </>
          )}
          {statusKind === 'probing' && meta?.probeSentAt && (
            <>
              <span className="text-gray-300">·</span>
              <span className="text-yellow-600" title={`Probe sent: ${meta.probeSentAt}`}>
                Checking since {formatRelativeTime(meta.probeSentAt)}
              </span>
            </>
          )}
          {fileSummary && (
            <>
              <span className="text-gray-300">·</span>
              <span>{fileSummary}</span>
            </>
          )}
          {isTerminal && (
            <>
              <span className="text-gray-300">·</span>
              <span className={statusKind === 'failed' ? 'text-red-600' : ''}>
                {formatDuration(duration)}
              </span>
            </>
          )}
          {meta?.sessionId && (
            <>
              <span className="text-gray-300">·</span>
              <span className="font-mono text-gray-400 text-xs">{meta.sessionId.slice(0, 8)}</span>
            </>
          )}
        </div>
      </div>

      {/* Observability bar */}
      {(hasUsage || meta?.costAmount != null || meta?.contextWindowUsed != null || meta?.failureCategory || meta?.toolCallCount != null) && (
        <div className="flex items-center gap-3 mt-2 text-xs text-gray-500 flex-wrap">
          {hasUsage && (
            <span>
              {meta?.totalTokens != null
                ? `${formatCompact(meta.totalTokens)} tokens`
                : [
                    meta?.inputTokens != null ? `${formatCompact(meta.inputTokens)} in` : '',
                    meta?.outputTokens != null ? `${formatCompact(meta.outputTokens)} out` : '',
                  ]
                    .filter(Boolean)
                    .join(' · ')}
            </span>
          )}
          {meta?.costAmount != null && meta?.costCurrency && (
            <span>{formatCost(meta.costAmount, meta.costCurrency)}</span>
          )}
          {meta?.contextWindowUsed != null && (
            <span>
              {meta?.contextWindowSize != null
                ? `${formatCompact(meta.contextWindowUsed)} / ${formatCompact(meta.contextWindowSize)} ctx`
                : `${formatCompact(meta.contextWindowUsed)} ctx used`}
              {contextWindowPct != null && (
                <span className="ml-1 text-gray-400">({contextWindowPct}%)</span>
              )}
            </span>
          )}
          {meta?.failureCategory && (
            <span className="px-1.5 py-0.5 rounded-full bg-red-50 text-red-600 text-[10px] font-medium">
              {meta.failureCategory}
            </span>
          )}
          {meta?.toolCallCount != null && (
            <span className={meta?.toolErrorCount ? 'text-orange-600 font-medium' : ''}>
              {meta.toolCallCount} tool{meta.toolCallCount !== 1 ? 's' : ''}
              {meta?.toolErrorCount ? ` · ${meta.toolErrorCount} error${meta.toolErrorCount !== 1 ? 's' : ''}` : ''}
            </span>
          )}
        </div>
      )}
    </div>
  )
}

export function SessionPage() {
  const { number: numberStr, sessionId, sessionName } = useParams<{ number: string; sessionId?: string; sessionName?: string }>()
  const { projectId } = useProject()
  const issueNumber = Number(numberStr)
  const decodedSessionId = sessionId ? decodeURIComponent(sessionId) : undefined
  const decodedSessionName = sessionName ? decodeURIComponent(sessionName) : undefined
  const routeSessionKey = decodedSessionName ?? decodedSessionId

  useDocumentTitle(`Session — Issue #${issueNumber} — Mohist`)

  const { data: issue } = useIssue(issueNumber)
  const { sessions, isLoading: sessionsLoading } = useCoderSessions(issueNumber)
  const session = sessions.find((s) => decodedSessionName
    ? (s.sessionName ?? s.executionId ?? s.id) === decodedSessionName
    : s.id === decodedSessionId)

  const routeSessionLookup = decodedSessionName ?? routeSessionKey
  const lookupKey = decodedSessionName ?? decodedSessionId

  const hasRoute = !!routeSessionLookup && !!projectId && !!decodedSessionName && issueNumber > 0
  const metadataQueryKey = useMemo(
    () => ['issues', issueNumber, projectId, 'agent-session-metadata', lookupKey] as const,
    [issueNumber, projectId, lookupKey],
  )
  const eventsQueryKey = useMemo(
    () => ['issues', issueNumber, projectId, 'agent-session-events', lookupKey] as const,
    [issueNumber, projectId, lookupKey],
  )

  const {
    data: metadata,
    isLoading: metadataLoading,
    isError: metadataError,
  } = useQuery<AgentSessionMetadata | null, Error>({
    queryKey: metadataQueryKey,
    queryFn: async () => {
      if (!decodedSessionName) return null
      return getAgentSessionMetadata(issueNumber, decodedSessionName, projectId)
    },
    enabled: hasRoute,
  })

  const {
    data: eventsResponse,
  } = useQuery<{ events: AgentSessionEvent[] } | null, Error>({
    queryKey: eventsQueryKey,
    queryFn: async () => {
      if (!decodedSessionName) return null
      return getAgentSessionEvents(issueNumber, decodedSessionName, projectId)
    },
    enabled: hasRoute && !!metadata,
  })

  const initialEvents = useMemo<AgentSessionEvent[]>(() => eventsResponse?.events ?? EMPTY_EVENTS, [eventsResponse])

  const lastEventAt = useMemo(() => {
    const list = eventsResponse?.events
    if (!list || list.length === 0) return null
    return list[list.length - 1]?.createdAt ?? null
  }, [eventsResponse])

  const detail: CoderSessionDetail | null = useMemo(() => {
    if (!metadata) return null
    const turnCount = eventsResponse?.events
      ? countTurnsFromEvents(eventsResponse.events)
      : 0
    return {
      id: metadata.id,
      acpSessionId: metadata.acpSessionId,
      executionId: null,
      taskDescription: metadata.title,
      status: metadata.status,
      createdAt: metadata.createdAt,
      completedAt: metadata.completedAt,
      model: metadata.model,
      coderType: null,
      stage: metadata.stage,
      title: metadata.title,
      metadata: buildSessionMetadata(metadata, lastEventAt, turnCount, metadata.acpSessionId),
      turns: [],
      incomplete: false,
    }
  }, [metadata, eventsResponse, lastEventAt])

  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)

  const rawStatus = detail?.metadata?.status ?? detail?.status ?? session?.status
  const apiStatusKind = detail?.metadata?.statusKind
  const isRunning = (rawStatus === 'running' || rawStatus === 'probing') && apiStatusKind !== 'completed' && apiStatusKind !== 'failed'
  const acpSessionId = detail?.acpSessionId ?? session?.acpSessionId ?? ''

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
  } = useSessionTranscript({
    issueNumber,
    sessionId: detail?.id ?? decodedSessionId ?? decodedSessionName ?? '',
    acpSessionId,
    initialEvents: initialEvents.length > 0 ? initialEvents : undefined,
    sessionQueryKeys: [metadataQueryKey, eventsQueryKey],
    isRunning,
  })

  const displayStatusKind: StatusKind = isFinalizing && isRunning ? 'finalizing' : statusKind
  const displayTurnCount = detail?.metadata?.turnCount ?? turns.length

  const displayTurns = turns.map((turn) => projectTurn(turn))

  const isUserScrollingRef = useRef(false)
  const isSelectingTextRef = useRef(false)

  const handleScroll = useCallback((evt?: Event) => {
    const container = scrollContainerRef.current
    if (!container) return

    const target = evt?.target as HTMLElement | null
    if (target && (target as HTMLElement).closest('[data-scrollable]')) {
      return
    }

    const distanceFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight
    const threshold = 200
    const wasNearBottom = isNearBottomRef.current
    isNearBottomRef.current = distanceFromBottom < threshold

    if (!wasNearBottom && isNearBottomRef.current) {
      isUserScrollingRef.current = false
    } else if (wasNearBottom && !isNearBottomRef.current) {
      isUserScrollingRef.current = true
    }

    setIsNearBottom(isNearBottomRef.current)
  }, [setIsNearBottom])

  const handleScrollToBottom = useCallback(() => {
    const container = scrollContainerRef.current
    if (!container) return
    container.scrollTo({
      top: container.scrollHeight,
      behavior: 'smooth',
    })
    scrollToBottom()
    isUserScrollingRef.current = false
  }, [scrollToBottom])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    let animationFrame: number | null = null

    const onScroll = (evt: Event) => {
      if (animationFrame !== null) {
        cancelAnimationFrame(animationFrame)
      }
      animationFrame = requestAnimationFrame(() => {
        handleScroll(evt)
      })
    }

    container.addEventListener('scroll', onScroll, { passive: true })
    return () => {
      container.removeEventListener('scroll', onScroll)
      if (animationFrame !== null) {
        cancelAnimationFrame(animationFrame)
      }
    }
  }, [handleScroll])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    const onSelectionChange = () => {
      const selection = window.getSelection()
      isSelectingTextRef.current = selection !== null && selection.toString().length > 0
    }

    document.addEventListener('selectionchange', onSelectionChange)
    return () => document.removeEventListener('selectionchange', onSelectionChange)
  }, [])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    if (!isNearBottomRef.current) return
    if (isUserScrollingRef.current || isSelectingTextRef.current) return

    container.scrollTop = container.scrollHeight
  }, [isRunning, transcriptVersion])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    if (!isNearBottomRef.current) return
    if (isUserScrollingRef.current || isSelectingTextRef.current) return

    container.scrollTop = container.scrollHeight
  }, [isRunning, detail?.metadata?.status])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    const handleResize = () => {
      if (isNearBottomRef.current && !isUserScrollingRef.current && !isSelectingTextRef.current) {
        container.scrollTop = container.scrollHeight
      }
    }

    window.addEventListener('resize', handleResize)
    return () => window.removeEventListener('resize', handleResize)
  }, [isRunning])

  if (!routeSessionKey || isNaN(issueNumber) || issueNumber <= 0) {
    return <SessionNotFound issueNumber={issueNumber || 0} />
  }

  if (sessionsLoading || metadataLoading) {
    return <SessionLoadingState />
  }

  if (metadataError || (!metadata && !session)) {
    return <SessionApiErrorState issueNumber={issueNumber} />
  }

  if (!detail) {
    return <SessionNotFound issueNumber={issueNumber} />
  }

  if (detail.incomplete && turns.length === 0) {
    return (
      <div className="flex flex-col flex-1 min-h-0">
        <SessionHeader
          issueNumber={issueNumber}
          issueTitle={issue?.title}
          meta={detail.metadata}
          statusKind={displayStatusKind}
          turnCount={displayTurnCount}
        />
        <SessionLegacyMissingState />
      </div>
    )
  }

  if (turns.length === 0 && isRunning) {
    return (
      <div className="flex flex-col flex-1 min-h-0">
        <SessionHeader
          issueNumber={issueNumber}
          issueTitle={issue?.title}
          meta={detail.metadata}
          statusKind={displayStatusKind}
          turnCount={displayTurnCount}
        />
        <SessionWaitingState />
      </div>
    )
  }

  if (turns.length === 0) {
    return (
      <div className="flex flex-col flex-1 min-h-0">
        <SessionHeader
          issueNumber={issueNumber}
          issueTitle={issue?.title}
          meta={detail.metadata}
          statusKind={displayStatusKind}
          turnCount={displayTurnCount}
        />
        <SessionEmptyState issueNumber={issueNumber} />
      </div>
    )
  }

  return (
    <div className="flex flex-col flex-1 min-h-0 relative">
      <div
        ref={scrollContainerRef}
        className="flex-1 overflow-y-auto"
      >
        <SessionTranscriptLayout
          title={detail.metadata.title ?? 'Session'}
          turnCount={displayTurnCount}
          turns={displayTurns}
          statusKind={displayStatusKind}
          isRunning={isRunning}
          isThinking={isThinking}
          isStreaming={isStreaming}
        />
      </div>

      {newContentAvailable && (
        <JumpToBottomButton onClick={handleScrollToBottom} />
      )}
    </div>
  )
}
