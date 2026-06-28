import type { DisplayTurn } from '../model/session-transcript-display'
import { TurnList } from './TurnList'

interface TranscriptEmptyStateProps {
  isRunning: boolean
}

export function TranscriptEmptyState({ isRunning }: TranscriptEmptyStateProps) {
  if (isRunning) {
    return (
      <div className="flex items-center gap-2 text-sm text-blue-500 justify-center py-12">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-blue-500 animate-pulse" />
        Waiting for activity...
      </div>
    )
  }

  return (
    <div className="text-center text-gray-400 text-sm py-12">
      No activity recorded for this session
    </div>
  )
}

interface SessionTranscriptLayoutProps {
  title: string
  turnCount: number
  turns: DisplayTurn[]
  statusKind: 'loading' | 'live' | 'probing' | 'finalizing' | 'completed' | 'failed' | 'stale'
  isRunning: boolean
  isThinking?: boolean
  isStreaming?: boolean
}

export function SessionTranscriptLayout({
  turns,
  isRunning,
  isThinking,
  isStreaming,
}: SessionTranscriptLayoutProps) {
  return (
    <div className="flex-1 overflow-y-auto px-4 py-6" data-scrollable="">
      {turns.length === 0 ? (
        <TranscriptEmptyState isRunning={isRunning} />
      ) : (
        <TurnList turns={turns} />
      )}
      {isThinking && turns.length > 0 && (
        <ThinkingPlaceholder />
      )}
      {isStreaming && <StreamingIndicator />}
    </div>
  )
}

function StreamingIndicator() {
  return (
    <div className="flex items-center gap-2 py-2 pl-4">
      <span className="relative flex h-2 w-2">
        <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
        <span className="relative inline-flex rounded-full h-2 w-2 bg-blue-500" />
      </span>
      <span className="text-xs text-blue-500">Streaming...</span>
    </div>
  )
}

function ThinkingPlaceholder() {
  return (
    <div className="flex items-center gap-2 py-4 pl-4">
      <span className="h-3 w-3 rounded-full bg-blue-400 animate-pulse" />
      <span className="text-sm text-gray-400">Thinking...</span>
    </div>
  )
}