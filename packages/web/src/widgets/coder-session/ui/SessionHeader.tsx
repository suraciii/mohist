import { Link } from 'react-router-dom'
import type { CoderSessionSummary } from '../../../entities/coder-session'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'

interface SessionHeaderProps {
  session: CoderSessionSummary
  issueNumber: number
  showTranscriptLink?: boolean
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

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

export function getSessionLabel(session: CoderSessionSummary): string {
  if (session.title) return session.title

  if (session.executionId) {
    const taskMatch = session.executionId.match(/\b(T-\d+)\b/)
    if (taskMatch) return taskMatch[1]

    const stagePrefix = session.executionId.split('-')[0]
    const stageName = stagePrefix.charAt(0).toUpperCase() + stagePrefix.slice(1)
    if (stageName === 'Plan' || stageName === 'Check' || stageName === 'Build') return stageName
  }

  if (session.stage === 'plan') return 'Plan'
  if (session.stage === 'check') return 'Check'

  if (session.taskDescription) {
    const truncated = session.taskDescription.length > 24
      ? session.taskDescription.slice(0, 21) + '...'
      : session.taskDescription
    return truncated
  }
  return 'Session'
}

export function getSessionStatusLabel(session: CoderSessionSummary): string {
  if (session.status === 'running') return 'Running'
  if (session.status === 'probing') return 'Checking session'
  if (session.status === 'failed') return 'Session failed'
  if (session.status === 'completed') return 'Completed'
  if (session.status === 'cancelled') return 'Cancelled'
  return session.status
}

function StatusIcon({ status }: { status: string }) {
  if (status === 'running' || status === 'probing') {
    const color = status === 'probing' ? 'bg-yellow-400' : 'bg-blue-400'
    const dotColor = status === 'probing' ? 'bg-yellow-500' : 'bg-blue-500'
    return (
      <span className="relative flex h-3 w-3">
        <span className={`animate-ping absolute inline-flex h-full w-full rounded-full ${color} opacity-75`} />
        <span className={`relative inline-flex rounded-full h-3 w-3 ${dotColor}`} />
      </span>
    )
  }
  if (status === 'completed') {
    return (
      <svg className="h-3.5 w-3.5 text-green-500" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  return (
    <svg className="h-3.5 w-3.5 text-red-500" viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
    </svg>
  )
}

export function SessionHeader({ session, issueNumber, showTranscriptLink }: SessionHeaderProps) {
  const label = getSessionLabel(session)
  const sessionName = session.sessionName ?? session.executionId ?? session.id
  const transcriptPath = `/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(sessionName)}`
  const startTime = formatTime(session.createdAt)

  const isActive = session.status === 'running' || session.status === 'probing'
  const durationMs = isActive
    ? Date.now() - new Date(session.createdAt).getTime()
    : session.completedAt
      ? new Date(session.completedAt).getTime() - new Date(session.createdAt).getTime()
      : 0

  const hasUsage =
    session.totalTokens != null ||
    session.inputTokens != null ||
    session.outputTokens != null ||
    session.cachedReadTokens != null ||
    session.thoughtTokens != null

  const hasContextWindow = session.contextWindowUsed != null

  const content = (
    <>
      <StatusIcon status={session.status} />
      <span className="text-sm font-medium text-gray-800 truncate max-w-[200px]">{label}</span>

      {/* Model badges */}
      {session.model && session.resolvedModel && session.model !== session.resolvedModel ? (
        <span className="text-xs text-gray-400">
          <span className="text-gray-500">{session.model}</span>
          <span className="text-gray-300 mx-1">→</span>
          <span className="text-blue-600">{session.resolvedModel}</span>
        </span>
      ) : session.model ? (
        <span className="text-xs text-gray-400">{session.model}</span>
      ) : null}

      {session.status === 'probing' && (
        <span className="text-xs text-yellow-600 font-medium">Checking session</span>
      )}
      {session.status === 'failed' && session.failureReason && (
        <span className="text-xs text-red-500 truncate max-w-[150px]" title={session.failureReason}>
          {session.failureReason}
        </span>
      )}

      {/* Usage badge */}
      {hasUsage && (
        <span className="text-xs text-gray-500">
          {session.totalTokens != null
            ? `${formatCompact(session.totalTokens)} tokens`
            : [
                session.inputTokens != null ? `${formatCompact(session.inputTokens)} in` : '',
                session.outputTokens != null ? `${formatCompact(session.outputTokens)} out` : '',
              ]
                .filter(Boolean)
                .join(' · ')}
        </span>
      )}

      {/* Cost badge */}
      {session.costAmount != null && session.costCurrency && (
        <span className="text-xs text-gray-500">{formatCost(session.costAmount, session.costCurrency)}</span>
      )}

      {/* Context window badge */}
      {hasContextWindow && (
        <span className="text-xs text-gray-500">
          {session.contextWindowSize != null
            ? `${formatCompact(session.contextWindowUsed)} / ${formatCompact(session.contextWindowSize)} ctx`
            : `${formatCompact(session.contextWindowUsed)} ctx used`}
        </span>
      )}

      {/* Tool/error counts */}
      {session.toolCallCount != null && (
        <span className={`text-xs ${session.toolErrorCount ? 'text-orange-600 font-medium' : 'text-gray-500'}`}>
          {session.toolCallCount} tool{session.toolCallCount !== 1 ? 's' : ''}
          {session.toolErrorCount ? ` · ${session.toolErrorCount} error${session.toolErrorCount !== 1 ? 's' : ''}` : ''}
        </span>
      )}

      <span className="text-xs text-gray-400 ml-auto flex items-center gap-2 shrink-0">
        <span>{startTime}</span>
        <span className="text-gray-300">·</span>
        <span className={isActive ? (session.status === 'probing' ? 'text-yellow-600 font-medium' : 'text-blue-600 font-medium') : 'text-gray-500'}>
          {formatDuration(durationMs)}
        </span>
      </span>
      {showTranscriptLink ? (
        <Link
          to={transcriptPath}
          className="text-xs text-blue-600 hover:text-blue-800 shrink-0 ml-2"
        >
          View transcript
        </Link>
      ) : (
        <svg
          className="h-4 w-4 text-gray-400 shrink-0"
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      )}
    </>
  )

  if (showTranscriptLink) {
    return (
      <div className="flex items-center gap-2.5 w-full text-left px-3 py-2 hover:bg-gray-50/80 transition-colors rounded-t-lg border-b border-gray-100">
        {content}
      </div>
    )
  }

  return (
    <Link
      to={transcriptPath}
      className="flex items-center gap-2.5 w-full text-left px-3 py-2 hover:bg-gray-50/80 transition-colors rounded-t-lg"
    >
      {content}
    </Link>
  )
}
