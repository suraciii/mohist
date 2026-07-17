import { useEffect, useRef, useState, type RefObject } from 'react'
import type { DisplayTurn } from '../model/session-transcript-display'
import { useTurnKeyboardNav } from '../model/useTurnKeyboardNav'
import type { TurnRefsMap } from '../model/turn-refs'
import { TurnList } from './TurnList'
import { CopyFullTextButton } from './CopyFullTextButton'

interface TranscriptEmptyStateProps {
  isRunning: boolean
}

export function TranscriptEmptyState({ isRunning }: TranscriptEmptyStateProps) {
  if (isRunning) {
    return (
      <div
        className="flex items-center gap-2 text-sm text-info justify-center py-12"
        data-testid="transcript-empty-state"
        data-tone="info"
      >
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-info animate-pulse" />
        Waiting for activity...
      </div>
    )
  }

  return (
    <div
      className="text-center text-muted-foreground/70 text-sm py-12"
      data-testid="transcript-empty-state"
      data-tone="neutral"
    >
      No activity recorded for this session
    </div>
  )
}

export type { TurnRefsMap } from '../model/turn-refs'

interface SessionTranscriptLayoutProps {
  title: string
  turnCount: number
  turns: DisplayTurn[]
  statusKind: 'loading' | 'live' | 'probing' | 'finalizing' | 'completed' | 'failed' | 'stale'
  isRunning: boolean
  isThinking?: boolean
  isStreaming?: boolean
  scrollContainerRef?: RefObject<HTMLElement | null>
}

export function SessionTranscriptLayout({
  turns,
  isRunning,
  isThinking,
  isStreaming,
  scrollContainerRef,
}: SessionTranscriptLayoutProps) {
  const turnRefs = useRef<TurnRefsMap>(new Map()).current
  const [, setRefsVersion] = useState(0)

  useEffect(() => {
    setRefsVersion((version) => version + 1)
  }, [turns.length])

  useTurnKeyboardNav({
    scrollContainerRef,
    turnRefs,
    turnCount: turns.length,
  })

  return (
    <div className="px-4 py-6 min-w-0" data-scrollable="">
      {turns.length === 0 ? (
        <TranscriptEmptyState isRunning={isRunning} />
      ) : (
        <div className="min-w-0">
          <div className="mb-3 flex items-center justify-end gap-2">
            <CopyFullTextButton turns={turns} />
          </div>
          <TurnList turns={turns} turnRefs={turnRefs} isRunning={isRunning} />
          {isRunning && isThinking && turns.length > 0 && <ThinkingPlaceholder />}
          {isRunning && isStreaming && <StreamingIndicator />}
        </div>
      )}
    </div>
  )
}

function StreamingIndicator() {
  return (
    <div className="flex items-center gap-2 py-2 pl-4" data-testid="transcript-streaming-indicator" data-tone="info" role="status">
      <span className="relative flex h-2 w-2" aria-hidden="true">
        <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-info/70 opacity-75" />
        <span className="relative inline-flex rounded-full h-2 w-2 bg-info" />
      </span>
      <span className="text-xs text-info">Streaming...</span>
    </div>
  )
}

function ThinkingPlaceholder() {
  return (
    <div className="flex items-center gap-2 py-4 pl-4" data-testid="transcript-thinking-indicator" data-tone="info" role="status">
      <span className="h-3 w-3 rounded-full bg-info animate-pulse" aria-hidden="true" />
      <span className="text-sm text-muted-foreground/70">Thinking...</span>
    </div>
  )
}