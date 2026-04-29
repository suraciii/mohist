import { useEffect, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import { useIssue } from '../hooks/useQueries'
import { useCoderSessions } from '../hooks/useCoderSessions'
import { useSessionTimeline } from '../hooks/useSessionTimeline'
import { ToolCallCard } from './ToolCallCard'
import type { CoderSessionItem } from '../lib/types'
import type { Round } from '../hooks/useSessionTimeline'

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

function getSessionDuration(session: CoderSessionItem): number {
  if (session.status === 'running') {
    return Date.now() - new Date(session.createdAt).getTime()
  }
  if (session.completedAt) {
    return new Date(session.completedAt).getTime() - new Date(session.createdAt).getTime()
  }
  return 0
}

function getStageLabel(stage: string | null): string {
  if (!stage) return 'Session'
  return stage.charAt(0).toUpperCase() + stage.slice(1)
}

function ConversationRound({
  round,
  isStreaming,
}: {
  round: Round
  isStreaming: boolean
}) {
  const isLiveRound = !round.completedAt
  const hasContent = round.agentText || round.toolCalls.length > 0 || round.recoveryEvents.length > 0

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-xs text-gray-400">
        <span className="font-medium text-gray-600">{round.label}</span>
        {round.startedAt && (
          <>
            <span className="text-gray-300">·</span>
            <span>{new Date(round.startedAt).toLocaleTimeString()}</span>
          </>
        )}
        {isLiveRound && (
          <>
            <span className="text-gray-300">·</span>
            <span className="flex items-center gap-1 text-blue-500">
              <span className="inline-block h-1.5 w-1.5 rounded-full bg-blue-500 animate-pulse" />
              Live
            </span>
          </>
        )}
      </div>

      {round.userText && (
        <div className="flex justify-end">
          <div className="max-w-[80%] rounded-2xl rounded-br-sm bg-blue-600 text-white px-4 py-2.5 text-sm whitespace-pre-wrap">
            {round.userText}
          </div>
        </div>
      )}

      {round.agentText && (
        <div className="max-w-[90%]">
          <div className="text-sm text-gray-800 whitespace-pre-wrap leading-relaxed">
            {round.agentText}
            {isLiveRound && isStreaming && (
              <span className="inline-block w-1.5 h-4 bg-blue-500 ml-0.5 animate-pulse align-text-bottom" />
            )}
          </div>
        </div>
      )}

      {round.thoughtText && (
        <details className="max-w-[90%]">
          <summary className="text-xs text-gray-400 cursor-pointer hover:text-gray-600 select-none">
            Thinking...{round.thoughtText.length > 500 ? ` (${(round.thoughtText.length / 1024).toFixed(1)}KB)` : ''}
          </summary>
          <pre className="mt-1 text-xs text-gray-500 whitespace-pre-wrap break-all max-h-48 overflow-auto bg-gray-50 rounded p-2">
            {round.thoughtText.length > 20000
              ? round.thoughtText.slice(0, 20000) + '\n... (truncated)'
              : round.thoughtText}
          </pre>
        </details>
      )}

      {round.toolCalls.length > 0 && (
        <div className="space-y-2 max-w-[90%]">
          {round.toolCalls.map((tc) => (
            <ToolCallCard key={tc.toolCallId ?? tc.executionId} entry={tc} />
          ))}
        </div>
      )}

      {round.recoveryEvents.length > 0 && (
        <div className="space-y-1 max-w-[90%]">
          {round.recoveryEvents.map((evt, i) => (
            <div key={i} className="flex items-center gap-1.5 text-xs text-amber-600">
              <svg className="h-3 w-3 shrink-0" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
              </svg>
              <span>
                {evt.status === 'detected' && 'LLM 连接中断'}
                {evt.status === 'recovering' && `恢复中 (attempt ${evt.attempt})`}
                {evt.status === 'recovered' && '恢复成功'}
                {evt.status === 'failed' && `恢复失败${evt.reason ? `: ${evt.reason}` : ''}`}
              </span>
            </div>
          ))}
        </div>
      )}

      {!hasContent && !isLiveRound && (
        <div className="text-xs text-gray-400">No output recorded</div>
      )}
    </div>
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

export function SessionPage() {
  const { number: numberStr, sessionId } = useParams<{ number: string; sessionId: string }>()
  const issueNumber = Number(numberStr)

  const { data: issue } = useIssue(issueNumber)
  const { sessions, isLoading: sessionsLoading } = useCoderSessions(issueNumber)

  const session = sessions.find((s) => s.id === sessionId)

  const { rounds, isStreaming, isLoading: timelineLoading } = useSessionTimeline(
    issueNumber,
    session,
  )

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
  }, [rounds, isStreaming])

  if (!sessionId || isNaN(issueNumber) || issueNumber <= 0) {
    return <SessionNotFound issueNumber={issueNumber || 0} />
  }

  if (sessionsLoading || timelineLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading session...</div>
      </div>
    )
  }

  if (!session) {
    return <SessionNotFound issueNumber={issueNumber} />
  }

  const isRunning = session.status === 'running'
  const duration = getSessionDuration(session)
  const model = session.model || 'unknown'
  const stageLabel = getStageLabel(session.stage)

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
            <h1 className="text-lg font-semibold text-gray-900">
              {session.taskDescription || stageLabel}
            </h1>
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
        className="flex-1 overflow-y-auto px-4 py-6 space-y-6"
      >
        {rounds.length === 0 && !isRunning && (
          <div className="text-center text-gray-400 text-sm py-12">
            No activity recorded for this session
          </div>
        )}

        {rounds.map((round) => (
          <ConversationRound
            key={`${round.roundIndex}-${round.label}`}
            round={round}
            isStreaming={isStreaming}
          />
        ))}

        {rounds.length === 0 && isRunning && (
          <div className="flex items-center gap-2 text-sm text-blue-500 justify-center py-12">
            <span className="inline-block h-2.5 w-2.5 rounded-full bg-blue-500 animate-pulse" />
            Waiting for activity...
          </div>
        )}
      </div>
    </div>
  )
}
