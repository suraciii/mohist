import { useEffect, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { useIssue } from '../hooks/useQueries'
import { useCoderSessions } from '../hooks/useCoderSessions'
import { api } from '../lib/api'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { SessionTranscriptView } from './SessionTranscriptView'
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

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    const handleScroll = () => {
      const threshold = 200
      const distanceFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight
      isNearBottomRef.current = distanceFromBottom < threshold
    }

    container.addEventListener('scroll', handleScroll, { passive: true })
    return () => container.removeEventListener('scroll', handleScroll)
  }, [])

  useEffect(() => {
    if (!isNearBottomRef.current) return
    const container = scrollContainerRef.current
    if (!container) return
    container.scrollTop = container.scrollHeight
  }, [detail?.turns.length])

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
  const isRunning = (meta?.status ?? session?.status) === 'running'
  const title = meta?.title ?? session?.taskDescription ?? meta?.stage ?? 'Session'
  const model = meta?.model ?? session?.model ?? 'unknown'
  const stageLabel = getStageLabel(meta?.stage ?? session?.stage ?? null)
  const createdAt = meta?.createdAt ?? session?.createdAt ?? new Date().toISOString()
  const completedAt = meta?.completedAt ?? session?.completedAt ?? null
  const duration = getSessionDuration(createdAt, completedAt)

  return (
    <div className="flex flex-col flex-1 min-h-0">
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
        {detail ? (
          <SessionTranscriptView turns={detail.turns} isRunning={isRunning} />
        ) : (
          <div className="text-center text-gray-400 text-sm py-12">
            No activity recorded for this session
          </div>
        )}
      </div>
    </div>
  )
}