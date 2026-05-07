import { useEffect, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useIssue } from '../hooks/useQueries'
import { useCoderSessions } from '../hooks/useCoderSessions'
import { api } from '../lib/api'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { SessionTranscriptView } from './SessionTranscriptView'
import { useSessionTranscript } from '../hooks/useSessionTranscript'
import type { CoderSessionDetail } from '../lib/types'

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

  const { data: detail, isLoading: detailLoading } = useQuery<CoderSessionDetail>({
    queryKey: ['issues', issueNumber, 'coder-sessions', sessionId],
    queryFn: () => api.getCoderSessionDetail(issueNumber, sessionId!),
    enabled: !!sessionId && sessionId.length > 0 && issueNumber > 0,
  })

  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const scrollToBottomPendingRef = useRef(false)

  const isRunning = (detail?.metadata?.status ?? session?.status) === 'running'
  const acpSessionId = detail?.acpSessionId ?? session?.acpSessionId ?? ''

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
        <div className="text-gray-400">Loading session...</div>
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
  const duration = getSessionDuration(createdAt, completedAt)

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
            <span className={isRunning ? 'text-blue-600 font-medium' : ''}>
              {formatDuration(duration)}
            </span>
            {meta?.sessionId && (
              <>
                <span className="text-gray-300">·</span>
                <span className="font-mono text-gray-400 text-xs">{meta.sessionId.slice(0, 8)}</span>
              </>
            )}
            {isRunning && (
              <span className="flex items-center gap-1 text-blue-600 font-medium">
                <span className="relative flex h-2 w-2">
                  <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
                  <span className="relative inline-flex rounded-full h-2 w-2 bg-blue-500" />
                </span>
                Live
              </span>
            )}
          </div>
        </div>
      </div>

      <div
        ref={scrollContainerRef}
        className="flex-1 overflow-y-auto px-4 py-6"
      >
        {turns.length > 0 ? (
          <SessionTranscriptView turns={turns} isRunning={isRunning} />
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
