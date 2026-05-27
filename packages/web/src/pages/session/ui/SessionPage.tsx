import { useEffect, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useIssue } from '../../../entities/issue/api/queries'
import { useCoderSessions } from '../../../entities/coder-session/model/useCoderSessions'
import { api } from '../../../shared/api/client'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { useProject } from '../../../entities/project/model/ProjectContext'
import { useSessionTranscript } from '../../../widgets/session-transcript/model/useSessionTranscript'
import { projectTurn } from '../../../widgets/session-transcript/model/session-transcript-display'
import type { CoderSessionDetail, SessionStatusKind } from '../../../shared/api/types'
import { SessionTranscriptLayout } from '../../../widgets/session-transcript/ui/SessionTranscriptLayout'

type StatusKind = SessionStatusKind

const EMPTY_TURNS: CoderSessionDetail['turns'] = []

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
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-gray-400 text-lg">Session not found</div>
        <Link
          to={`/issue/${issueNumber}`}
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
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-red-400 text-lg">Failed to load session</div>
        <p className="text-gray-500 text-sm">An error occurred while fetching session data.</p>
        <Link
          to={`/issue/${issueNumber}`}
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
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-gray-400 text-lg">No activity recorded for this session</div>
        <p className="text-gray-500 text-sm">This session has no recorded transcript data.</p>
        <Link
          to={`/issue/${issueNumber}`}
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
    <button
      onClick={onClick}
      className="absolute bottom-4 right-4 flex items-center gap-1.5 px-3 py-1.5 bg-gray-800 text-white text-xs font-medium rounded-full shadow-lg hover:bg-gray-700 transition-colors"
    >
      <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 17a.75.75 0 01-.75-.75V5.612L5.29 9.77a.75.75 0 01-1.08-1.04l5.25-5.5a.75.75 0 011.08 0l5.25 5.5a.75.75 0 11-1.08 1.04l-3.96-4.158V16.25A.75.75 0 0110 17z" clipRule="evenodd" />
      </svg>
      Jump to bottom
    </button>
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

  return (
    <div className="border-b border-gray-200 bg-white px-4 py-3 shrink-0">
      <div className="flex items-center gap-2 text-sm mb-2">
        <Link
          to={`/issue/${issueNumber}`}
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
          {meta?.model && <span>{meta.model}</span>}
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
    </div>
  )
}

export function SessionPage() {
  const { number: numberStr, sessionId } = useParams<{ number: string; sessionId: string }>()
  const { projectId } = useProject()
  const issueNumber = Number(numberStr)

  useDocumentTitle(`Session — Issue #${issueNumber} — Mohist`)

  const { data: issue } = useIssue(issueNumber)
  const { sessions, isLoading: sessionsLoading } = useCoderSessions(issueNumber)
  const session = sessions.find((s) => s.id === sessionId)

  const {
    data: detail,
    isLoading: detailLoading,
    isError: detailError,
  } = useQuery<CoderSessionDetail, Error>({
    queryKey: ['issues', issueNumber, projectId, 'coder-sessions', sessionId],
    queryFn: () => api.getCoderSessionDetail(issueNumber, sessionId!, projectId),
    enabled: !!sessionId && sessionId.length > 0 && issueNumber > 0 && !!projectId,
  })

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
    sessionId: sessionId ?? '',
    acpSessionId,
    initialTurns: detail?.turns ?? EMPTY_TURNS,
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

  if (!sessionId || isNaN(issueNumber) || issueNumber <= 0) {
    return <SessionNotFound issueNumber={issueNumber || 0} />
  }

  if (sessionsLoading || detailLoading) {
    return <SessionLoadingState />
  }

  if (detailError || (!detail && !session)) {
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
