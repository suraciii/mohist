import { useEffect, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useIssue } from '../hooks/useQueries'
import { useCoderSessions } from '../hooks/useCoderSessions'
import { api } from '../lib/api'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { SessionTranscriptView } from './SessionTranscriptView'
import { useSessionTranscript } from '../hooks/useSessionTranscript'
import type { CoderSessionDetail, SessionStatusKind } from '../lib/types'

type StatusKind = SessionStatusKind

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
): StatusKind {
  if (!isRunning) {
    if (rawStatus === 'failed' || rawStatus === 'timeout' || rawStatus === 'cancelled') {
      return 'failed'
    }
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

function StatusBadge({ kind }: { kind: StatusKind }) {
  const config: Record<StatusKind, { label: string; color: string; dot?: boolean }> = {
    loading: { label: 'Loading', color: 'bg-gray-100 text-gray-600' },
    live: { label: 'Live', color: 'bg-blue-100 text-blue-700', dot: true },
    finalizing: { label: 'Finalizing', color: 'bg-yellow-100 text-yellow-700' },
    completed: { label: 'Completed', color: 'bg-green-100 text-green-700' },
    failed: { label: 'Failed', color: 'bg-red-100 text-red-700' },
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
      : Date.now() - new Date(createdAt).getTime()

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
          <StatusBadge kind={statusKind} />
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
    queryKey: ['issues', issueNumber, 'coder-sessions', sessionId],
    queryFn: () => api.getCoderSessionDetail(issueNumber, sessionId!),
    enabled: !!sessionId && sessionId.length > 0 && issueNumber > 0,
  })

  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const scrollToBottomPendingRef = useRef(false)

  const rawStatus = detail?.metadata?.status ?? session?.status
  const isRunning = rawStatus === 'running'
  const acpSessionId = detail?.acpSessionId ?? session?.acpSessionId ?? ''

  const statusKind: StatusKind = detail
    ? (detail.metadata.statusKind ?? getSessionStatusKind(rawStatus, detail.metadata.lastActivityAt, isRunning))
    : getSessionStatusKind(rawStatus, undefined, isRunning)

  const {
    turns,
    transcriptVersion,
    scrollToBottom,
    newContentAvailable,
    setIsNearBottom,
  } = useSessionTranscript({
    issueNumber,
    sessionId: sessionId ?? '',
    acpSessionId,
    initialTurns: detail?.turns ?? [],
    isRunning,
  })

  const handleScroll = useCallback(() => {
    const container = scrollContainerRef.current
    if (!container) return
    const threshold = 200
    const distanceFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight
    isNearBottomRef.current = distanceFromBottom < threshold
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
  }, [scrollToBottom])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    container.addEventListener('scroll', handleScroll, { passive: true })
    return () => container.removeEventListener('scroll', handleScroll)
  }, [handleScroll])

  useEffect(() => {
    if (!isNearBottomRef.current) return
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    container.scrollTop = container.scrollHeight
    if (scrollToBottomPendingRef.current) {
      scrollToBottomPendingRef.current = false
    }
  }, [transcriptVersion, isRunning])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    container.scrollTop = container.scrollHeight
  }, [isRunning, detail?.metadata?.status])

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
          statusKind={statusKind}
          turnCount={turns.length}
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
          statusKind={statusKind}
          turnCount={turns.length}
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
          statusKind={statusKind}
          turnCount={turns.length}
        />
        <SessionEmptyState issueNumber={issueNumber} />
      </div>
    )
  }

  return (
    <div className="flex flex-col flex-1 min-h-0 relative">
      <SessionHeader
        issueNumber={issueNumber}
        issueTitle={issue?.title}
        meta={detail.metadata}
        statusKind={statusKind}
        turnCount={turns.length}
      />

      <div
        ref={scrollContainerRef}
        className="flex-1 overflow-y-auto px-4 py-6"
      >
        <SessionTranscriptView turns={turns} isRunning={isRunning} />
      </div>

      {newContentAvailable && (
        <JumpToBottomButton onClick={handleScrollToBottom} />
      )}
    </div>
  )
}

function formatRelativeTime(isoString: string | null | undefined): string {
  if (!isoString) return 'unknown'
  const date = new Date(isoString)
  const now = Date.now()
  const diffMs = now - date.getTime()
  if (diffMs < 0) return 'just now'
  const diffSec = Math.floor(diffMs / 1000)
  if (diffSec < 10) return 'just now'
  if (diffSec < 60) return `${diffSec}s ago`
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return `${diffMin}m ago`
  const diffHr = Math.floor(diffMin / 60)
  if (diffHr < 24) return `${diffHr}h ago`
  return date.toLocaleDateString()
}

function getSessionDuration(createdAt: string, completedAt: string | null): number {
  if (completedAt) {
    return new Date(completedAt).getTime() - new Date(createdAt).getTime()
  }
  return Date.now() - new Date(createdAt).getTime()
}

function getStageLabel(stage: string | null): string {
  if (!stage) return 'Session'
  return stage.charAt(0).toUpperCase() + stage.slice(1)
}

function deriveStatusKind(
  rawStatus: string | null | undefined,
  lastActivityAt: string | null | undefined,
  isRunning: boolean,
  issueStage: string | null | undefined,
): StatusKind {
  if (isRunning) {
    if (!lastActivityAt) {
      return 'waiting'
    }
    const lastActivityMs = Date.now() - new Date(lastActivityAt).getTime()
    if (lastActivityMs > STALE_THRESHOLD_MS) {
      return 'stale'
    }
    if (issueStage && !RUNNING_STAGES.includes(issueStage)) {
      return 'finalizing'
    }
    return 'live'
  }
  if (rawStatus === 'completed') return 'completed'
  if (rawStatus === 'failed' || rawStatus === 'timeout' || rawStatus === 'cancelled') return 'failed'
  return 'failed'
}

function StatusBadge({ kind }: { kind: StatusKind }) {
  if (kind === 'waiting') {
    return (
      <span className="flex items-center gap-1 text-amber-600 font-medium">
        <svg className="h-3.5 w-3.5 animate-spin" viewBox="0 0 20 20" fill="none">
          <circle className="opacity-25" cx="10" cy="10" r="8" stroke="currentColor" strokeWidth="3" />
          <path className="opacity-75" fill="currentColor" d="M10 2a8 8 0 018 8h-2a6 6 0 00-6-6V2z" />
        </svg>
        Waiting for first activity
      </span>
    )
  }
  if (kind === 'live') {
    return (
      <span className="flex items-center gap-1 text-blue-600 font-medium">
        <span className="relative flex h-2 w-2">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
          <span className="relative inline-flex rounded-full h-2 w-2 bg-blue-500" />
        </span>
        Live
      </span>
    )
  }
  if (kind === 'finalizing') {
    return (
      <span className="flex items-center gap-1 text-amber-600 font-medium">
        <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm1-12a1 1 0 10-2 0v4a1 1 0 00.293.707l2.828 2.828a1 1 0 101.414-1.414L11 9.586V6z" clipRule="evenodd" />
        </svg>
        Finalizing
      </span>
    )
  }
  if (kind === 'stale') {
    return (
      <span className="flex items-center gap-1 text-gray-500 font-medium">
        <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm1-11a1 1 0 10-2 0v3.586L7.707 9.293a1 1 0 00-1.414 1.414l3 3a1 1 0 001.414 0l3-3a1 1 0 00-1.414-1.414L11 10.586V7z" clipRule="evenodd" />
        </svg>
        Stale
      </span>
    )
  }
  if (kind === 'completed') {
    return (
      <span className="flex items-center gap-1 text-green-600 font-medium">
        <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
        </svg>
        Completed
      </span>
    )
  }
  if (kind === 'failed') {
    return (
      <span className="flex items-center gap-1 text-red-600 font-medium">
        <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z" clipRule="evenodd" />
        </svg>
        Failed
      </span>
    )
  }
  if (kind === 'error') {
    return (
      <span className="flex items-center gap-1 text-red-600 font-medium">
        <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7 4a1 1 0 11-2 0 1 1 0 012 0zm1-5a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
        </svg>
        Error loading session
      </span>
    )
  }
  return null
}

function ChangedFilesSummary({ changedFiles }: { changedFiles?: FileChangeSummary[] | null }) {
  if (!changedFiles || changedFiles.length === 0) return null
  const created = changedFiles.filter(f => f.operation === 'created').length
  const modified = changedFiles.filter(f => f.operation === 'modified').length
  const deleted = changedFiles.filter(f => f.operation === 'deleted').length
  const moved = changedFiles.filter(f => f.operation === 'moved').length
  const parts: string[] = []
  if (created > 0) parts.push(`${created} created`)
  if (modified > 0) parts.push(`${modified} modified`)
  if (deleted > 0) parts.push(`${deleted} deleted`)
  if (moved > 0) parts.push(`${moved} moved`)
  return (
    <span className="text-xs text-gray-500">
      {changedFiles.length} file{changedFiles.length !== 1 ? 's' : ''} changed: {parts.join(', ')}
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

export function SessionPage() {
  const { number: numberStr, sessionId } = useParams<{ number: string; sessionId: string }>()
  const issueNumber = Number(numberStr)

  useDocumentTitle(`Session — Issue #${issueNumber} — Mohist`)

  const { data: issue } = useIssue(issueNumber)
  const { sessions, isLoading: sessionsLoading } = useCoderSessions(issueNumber)
  const session = sessions.find((s) => s.id === sessionId)

  const { data: detail, isLoading: detailLoading, isError } = useQuery<CoderSessionDetail, Error>({
    queryKey: ['issues', issueNumber, 'coder-sessions', sessionId],
    queryFn: () => api.getCoderSessionDetail(issueNumber, sessionId!),
    enabled: !!sessionId && sessionId.length > 0 && issueNumber > 0,
  })

  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const scrollToBottomPendingRef = useRef(false)

  const rawStatus = detail?.metadata?.status ?? session?.status
  const isRunning = rawStatus === 'running'
  const acpSessionId = detail?.acpSessionId ?? session?.acpSessionId ?? ''
  const lastActivityAt = detail?.metadata?.lastActivityAt ?? null
  const issueStage = issue?.stage ?? null

  const statusKind: StatusKind = deriveStatusKind(rawStatus, lastActivityAt, isRunning, issueStage)

  const {
    turns,
    transcriptVersion,
    scrollToBottom,
    newContentAvailable,
    setIsNearBottom,
  } = useSessionTranscript({
    issueNumber,
    sessionId: sessionId ?? '',
    acpSessionId,
    initialTurns: detail?.turns ?? [],
    isRunning,
  })

  const handleScroll = useCallback(() => {
    const container = scrollContainerRef.current
    if (!container) return
    const threshold = 200
    const distanceFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight
    isNearBottomRef.current = distanceFromBottom < threshold
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
  }, [scrollToBottom])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    container.addEventListener('scroll', handleScroll, { passive: true })
    return () => container.removeEventListener('scroll', handleScroll)
  }, [handleScroll])

  useEffect(() => {
    if (!isNearBottomRef.current) return
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    container.scrollTop = container.scrollHeight
    if (scrollToBottomPendingRef.current) {
      scrollToBottomPendingRef.current = false
    }
  }, [transcriptVersion, isRunning])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    container.scrollTop = container.scrollHeight
  }, [isRunning, detail?.metadata?.status])

  if (!sessionId || isNaN(issueNumber) || issueNumber <= 0) {
    return <SessionNotFound issueNumber={issueNumber || 0} />
  }

  if (sessionsLoading || detailLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-center space-y-2">
          <div className="w-8 h-8 border-2 border-blue-500 border-t-transparent rounded-full animate-spin mx-auto" />
          <div className="text-gray-400 text-sm">Loading session detail...</div>
        </div>
      </div>
    )
  }

  if (isError) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-center space-y-3">
          <div className="text-red-500 text-lg">Failed to load session</div>
          <div className="text-gray-400 text-sm">Please try refreshing the page</div>
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

  if (!detail && !session) {
    return <SessionNotFound issueNumber={issueNumber} />
  }

  const meta = detail?.metadata
  const title = meta?.title ?? session?.taskDescription ?? meta?.stage ?? 'Session'
  const model = meta?.model ?? session?.model ?? 'unknown'
  const stageLabel = getStageLabel(meta?.stage ?? session?.stage ?? null)
  const createdAt = meta?.createdAt ?? session?.createdAt ?? new Date().toISOString()
  const completedAt = meta?.completedAt ?? session?.completedAt ?? null

  const turnCount = meta?.turnCount ?? detail?.turns?.length ?? 0
  const changedFiles = meta?.changedFiles ?? null

  const showDuration = completedAt || statusKind === 'completed' || statusKind === 'failed'
  const duration = showDuration ? getSessionDuration(createdAt, completedAt) : 0

  return (
    <div className="flex flex-col flex-1 min-h-0 relative">
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
          {issue && (
            <>
              <span className="text-gray-300">/</span>
              <span className="text-gray-500 truncate max-w-[300px]">{issue.title}</span>
            </>
          )}
        </div>

        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2">
            <h1 className="text-lg font-semibold text-gray-900">{title}</h1>
          </div>

          <div className="flex items-center gap-2 text-xs text-gray-500 ml-auto shrink-0">
            <span className="px-2 py-0.5 rounded-full bg-gray-100 text-gray-600 font-medium">
              {stageLabel}
            </span>
            <span>{model}</span>
            <span className="text-gray-300">·</span>
            <span>{turnCount} turn{turnCount !== 1 ? 's' : ''}</span>
            <span className="text-gray-300">·</span>
            <span>Last activity {formatRelativeTime(lastActivityAt)}</span>
            {showDuration && (
              <>
                <span className="text-gray-300">·</span>
                <span className={statusKind === 'completed' ? 'text-green-600' : statusKind === 'failed' ? 'text-red-600' : ''}>
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
            <span className="text-gray-300">·</span>
            <StatusBadge kind={statusKind} />
          </div>
        </div>

        {changedFiles && changedFiles.length > 0 && (
          <div className="mt-2">
            <ChangedFilesSummary changedFiles={changedFiles} />
          </div>
        )}
      </div>

      <div
        ref={scrollContainerRef}
        className="flex-1 overflow-y-auto px-4 py-6"
      >
        {turns.length > 0 ? (
          <SessionTranscriptView turns={turns} isRunning={isRunning} />
        ) : statusKind === 'waiting' ? (
          <div className="text-center text-amber-500 text-sm py-12">
            Waiting for first activity...
          </div>
        ) : statusKind === 'error' ? (
          <div className="text-center text-red-400 text-sm py-12">
            Failed to load transcript
          </div>
        ) : (
          <div className="text-center text-gray-400 text-sm py-12">
            No activity recorded for this session
          </div>
        )}
      </div>

      {newContentAvailable && (
        <JumpToBottomButton onClick={handleScrollToBottom} />
      )}
    </div>
  )
}