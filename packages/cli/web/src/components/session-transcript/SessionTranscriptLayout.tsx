import type { DisplayTurn } from '../../lib/session-transcript-display'
import { StickySessionTitle } from './StickySessionTitle'
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
}

export function SessionTranscriptLayout({
  title,
  turnCount,
  turns,
  statusKind,
  isRunning,
  isThinking,
}: SessionTranscriptLayoutProps) {
  return (
    <div className="flex flex-col h-full">
      <StickySessionTitle
        title={title}
        statusKind={statusKind}
        turnCount={turnCount}
        isRunning={isRunning}
      />

      <div className="flex-1 overflow-y-auto px-4 py-6">
        {turns.length === 0 ? (
          <TranscriptEmptyState isRunning={isRunning} />
        ) : (
          <TurnList turns={turns} />
        )}
        {isThinking && turns.length > 0 && (
          <ThinkingPlaceholder />
        )}
      </div>
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