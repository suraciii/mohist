import { useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import type { DisplayTurn } from '../model/session-transcript-display'
import { useTurnKeyboardNav } from '../model/useTurnKeyboardNav'
import { useNow } from '../model/use-now'
import { selectActiveToolCall } from '../model/select-active-tool-call'
import { selectToolCallGroupIds } from '../model/select-failed-tool-calls'
import { useTranscriptLocate } from '../model/use-transcript-locate'
import type { TurnRefsMap } from '../model/turn-refs'
import { formatDuration } from '../model/format-duration'
import { TurnList } from './TurnList'
import { CopyFullTextButton } from './CopyFullTextButton'
import { CurrentActivityBar } from './CurrentActivityBar'
import { MiniTimeline } from './MiniTimeline'

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
  turns: DisplayTurn[]
  isRunning: boolean
  isThinking?: boolean
  isStreaming?: boolean
  scrollContainerRef?: RefObject<HTMLElement | null>
  now?: number
}

export function SessionTranscriptLayout({
  turns,
  isRunning,
  isThinking,
  isStreaming,
  scrollContainerRef,
  now: providedNow,
}: SessionTranscriptLayoutProps) {
  const { expansionRegistry, highlightRegistry, locate } = useTranscriptLocate({ scrollContainerRef })
  const toolCallGroupIds = useMemo(() => selectToolCallGroupIds(turns), [turns])
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

  const liveNow = useNow({ intervalMs: 1000, enabled: isRunning, now: providedNow })
  const now = liveNow
  const activeTool = isRunning ? selectActiveToolCall(turns) : null
  const [thinkingStartedAt, setThinkingStartedAt] = useState<number | null>(null)
  const wasThinkingRef = useRef<boolean>(false)

  useEffect(() => {
    const wasThinking = wasThinkingRef.current
    const isThinkingValue = isThinking ?? false
    if (isThinkingValue && !wasThinking) {
      setThinkingStartedAt(providedNow ?? Date.now())
    } else if (!isThinkingValue && wasThinking) {
      setThinkingStartedAt(null)
    }
    wasThinkingRef.current = isThinkingValue
  }, [isThinking, providedNow])

  useEffect(() => {
    if (!isRunning) {
      setThinkingStartedAt(null)
      wasThinkingRef.current = false
    }
  }, [isRunning])

  return (
    <div className="block xl:flex xl:flex-row xl:items-start px-4 py-6 min-w-0" data-scrollable="">
      <MiniTimeline turns={turns} locate={locate} groupIdsByToolCallId={toolCallGroupIds} />
      <div className="min-w-0 flex-1">
        {turns.length === 0 ? (
          <TranscriptEmptyState isRunning={isRunning} />
        ) : (
          <div className="min-w-0">
            <div className="mb-3 flex items-center justify-end gap-2">
              <CopyFullTextButton turns={turns} />
            </div>
            <TurnList turns={turns} turnRefs={turnRefs} isRunning={isRunning} now={now} expansionRegistry={expansionRegistry} highlightRegistry={highlightRegistry} />
            {isRunning && isThinking && turns.length > 0 && now !== undefined && thinkingStartedAt !== null && (
              <ThinkingPlaceholder now={now} thinkingStartedAt={thinkingStartedAt} />
            )}
            {isRunning && isStreaming && <StreamingIndicator />}
            {activeTool && now !== undefined && scrollContainerRef && (
              <CurrentActivityBar
                activeTool={activeTool}
                now={now}
                scrollContainerRef={scrollContainerRef}
              />
            )}
          </div>
        )}
      </div>
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

interface ThinkingPlaceholderProps {
  now: number
  thinkingStartedAt: number | null
}

function ThinkingPlaceholder({ now, thinkingStartedAt }: ThinkingPlaceholderProps) {
  const elapsedMs = thinkingStartedAt === null ? null : now - thinkingStartedAt
  const elapsedText =
    elapsedMs !== null && Number.isFinite(elapsedMs) && elapsedMs >= 0
      ? formatDuration(elapsedMs)
      : null

  return (
    <div className="flex items-center gap-2 py-4 pl-4" data-testid="transcript-thinking-indicator" data-tone="info" role="status">
      <span className="h-3 w-3 rounded-full bg-info animate-pulse" aria-hidden="true" />
      <span className="text-sm text-muted-foreground/70">Thinking...</span>
      {elapsedText && (
        <span
          data-testid="transcript-thinking-elapsed"
          data-elapsed-mode="live"
          className="ml-auto text-xs tabular-nums text-muted-foreground/70 shrink-0"
        >
          {elapsedText}
        </span>
      )}
    </div>
  )
}