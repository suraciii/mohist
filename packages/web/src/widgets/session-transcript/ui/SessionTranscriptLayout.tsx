import type { TimelineEntry, TimelineFact } from '@/entities/session'
import type { SessionTimelineCurrentActivity } from '../model/useSessionTimeline'
import type { DisplayTurn } from '../model/session-transcript-display'
import { useEffect, useMemo, useRef, useState, type RefObject } from 'react'
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
import { RawTimelineView } from './RawTimelineView'
import { TimelineItemList } from './TimelineItemList'
import type { TimelineReferenceResolver } from './TimelineItemRow'
import type { MarkdownAttachment } from '@/shared/ui/markdown-reader/MarkdownReader'

export type SessionTimelineView = 'summary' | 'raw'

interface SessionTranscriptLayoutProps {
  entries?: TimelineEntry[]
  facts?: TimelineFact[]
  currentActivity?: SessionTimelineCurrentActivity
  viewMode?: SessionTimelineView
  resolveReference?: TimelineReferenceResolver
  turns?: DisplayTurn[]
  isRunning?: boolean
  isThinking?: boolean
  isStreaming?: boolean
  scrollContainerRef?: RefObject<HTMLElement | null>
  now?: number
  inputIdsByTurn?: string[][]
  resolveAttachment?: (inputId: string, attachmentId: string) => MarkdownAttachment | null | undefined
}

export function TranscriptEmptyState({
  currentActivity,
  isRunning = false,
}: {
  currentActivity?: SessionTimelineCurrentActivity
  isRunning?: boolean
}) {
  if (!currentActivity) {
    return (
      <div
        className={isRunning ? 'flex items-center gap-2 text-sm text-info justify-center py-12' : 'text-center text-muted-foreground/70 text-sm py-12'}
        data-testid="transcript-empty-state"
        data-tone={isRunning ? 'info' : 'neutral'}
      >
        {isRunning ? 'Waiting for activity...' : 'No activity recorded for this session'}
      </div>
    )
  }

  const stateKind = currentActivity.state === 'active' || currentActivity.state === 'queued'
    ? 'active-no-content'
    : `${currentActivity.state}-no-content`

  return (
    <div
      className="flex items-center justify-center py-12"
      data-testid="session-empty-state"
      data-state-kind={stateKind}
      data-tone={currentActivity.state === 'unknown' ? 'warning' : currentActivity.state === 'idle' ? 'neutral' : 'info'}
    >
      <div className="text-center space-y-2">
        <div className="text-sm font-medium">{currentActivity.label}</div>
        <p className="text-sm text-muted-foreground">
          {currentActivity.state === 'unknown'
            ? 'Mohist cannot confirm whether execution is still active.'
            : currentActivity.state === 'idle'
              ? 'No activity recorded for this session.'
              : 'Waiting for runtime activity.'}
        </p>
      </div>
    </div>
  )
}

function CurrentActivity({ activity }: { activity: SessionTimelineCurrentActivity }) {
  return (
    <div
      className="mb-3 flex items-center gap-2 border-b border-border px-1 pb-2 text-xs text-muted-foreground"
      data-testid="timeline-current-activity"
      data-activity-state={activity.state}
      role="status"
      aria-live="polite"
    >
      <span className="font-medium text-foreground">Current activity</span>
      <span data-testid="timeline-current-activity-label">{activity.label}</span>
    </div>
  )
}

export function SessionTranscriptLayout({
  entries,
  facts,
  currentActivity,
  viewMode = 'summary',
  resolveReference,
  turns = [],
  isRunning = false,
  isThinking,
  isStreaming,
  scrollContainerRef,
  now,
  inputIdsByTurn,
  resolveAttachment,
}: SessionTranscriptLayoutProps) {
  if (entries !== undefined || facts !== undefined || currentActivity !== undefined) {
    const timelineEntries = entries ?? []
    const timelineFacts = facts ?? []
    const timelineActivity = currentActivity ?? { state: 'unknown', label: '状态未知' } satisfies SessionTimelineCurrentActivity
    return (
      <div className="block px-4 py-6 min-w-0" data-scrollable="" data-testid="session-timeline-layout" data-timeline-view={viewMode}>
        <div className="min-w-0">
          <CurrentActivity activity={timelineActivity} />
          {timelineEntries.length === 0 ? (
            <TranscriptEmptyState currentActivity={timelineActivity} />
          ) : viewMode === 'raw' ? (
            <RawTimelineView facts={timelineFacts} />
          ) : (
            <TimelineItemList entries={timelineEntries} resolveReference={resolveReference} />
          )}
        </div>
      </div>
    )
  }

  return (
    <LegacySessionTranscriptLayout
      turns={turns}
      isRunning={isRunning}
      isThinking={isThinking}
      isStreaming={isStreaming}
      scrollContainerRef={scrollContainerRef}
      now={now}
      inputIdsByTurn={inputIdsByTurn}
      resolveAttachment={resolveAttachment}
    />
  )
}

function LegacySessionTranscriptLayout({
  turns,
  isRunning,
  isThinking,
  isStreaming,
  scrollContainerRef,
  now: providedNow,
  inputIdsByTurn,
  resolveAttachment,
}: {
  turns: DisplayTurn[]
  isRunning: boolean
  isThinking?: boolean
  isStreaming?: boolean
  scrollContainerRef?: RefObject<HTMLElement | null>
  now?: number
  inputIdsByTurn?: string[][]
  resolveAttachment?: (inputId: string, attachmentId: string) => MarkdownAttachment | null | undefined
}) {
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
            <TurnList
              turns={turns}
              turnRefs={turnRefs}
              isRunning={isRunning}
              now={liveNow}
              expansionRegistry={expansionRegistry}
              highlightRegistry={highlightRegistry}
              inputIdsByTurn={inputIdsByTurn}
              resolveAttachment={resolveAttachment}
            />
            {isRunning && isThinking && turns.length > 0 && liveNow !== undefined && thinkingStartedAt !== null && (
              <ThinkingPlaceholder now={liveNow} thinkingStartedAt={thinkingStartedAt} />
            )}
            {isRunning && isStreaming && <StreamingIndicator />}
            {activeTool && liveNow !== undefined && scrollContainerRef && (
              <CurrentActivityBar
                activeTool={activeTool}
                now={liveNow}
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
