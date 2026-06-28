import { useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import type { DisplayTurn } from '../model/session-transcript-display'
import { useTurnKeyboardNav } from '../model/useTurnKeyboardNav'
import { TurnList } from './TurnList'
import { TurnTocRail, buildTurnTocEntries } from './TurnToc'
import { TranscriptToolbar } from './TranscriptToolbar'
import { CopyFullTextButton } from './CopyFullTextButton'

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

export type TurnRefsMap = Map<number, HTMLDivElement>

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
  const [refsVersion, setRefsVersion] = useState(0)

  useEffect(() => {
    setRefsVersion((version) => version + 1)
  }, [turns.length])

  const entries = useMemo(
    () => buildTurnTocEntries(turns, turnRefs),
    [turns, turnRefs, refsVersion],
  )

  useTurnKeyboardNav({
    scrollContainerRef,
    turnRefs,
    turnCount: turns.length,
  })

  return (
    <div className="px-4 py-6 min-w-0" data-scrollable="">
      {turns.length === 0 ? (
        <div className="max-w-2xl mx-auto">
          <TranscriptEmptyState isRunning={isRunning} />
        </div>
      ) : (
        <div className="lg:grid lg:grid-cols-[1fr_180px] lg:gap-6 lg:max-w-4xl lg:mx-auto">
          <div className="min-w-0">
            <TranscriptToolbar
              entries={entries}
              rightSlot={<CopyFullTextButton turns={turns} />}
            />
            <TurnList turns={turns} turnRefs={turnRefs} />
            {isThinking && turns.length > 0 && <ThinkingPlaceholder />}
            {isStreaming && <StreamingIndicator />}
          </div>
          <TurnTocRail
            entries={entries}
            actionSlot={<CopyFullTextButton turns={turns} label="Copy" />}
          />
        </div>
      )}
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
