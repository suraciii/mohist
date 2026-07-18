import { type KeyboardEvent, type RefObject, useCallback } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayToolPart } from '../model/session-transcript-display'
import { formatElapsedNow } from '../model/format-duration'
import { deriveVerbLedTitle } from './tool-views/shared'

interface CurrentActivityBarProps {
  activeTool: DisplayToolPart
  now: number
  scrollContainerRef: RefObject<HTMLElement | null>
}

export function CurrentActivityBar({ activeTool, now, scrollContainerRef }: CurrentActivityBarProps) {
  const handleJump = useCallback(() => {
    const container = scrollContainerRef.current
    if (!container) return
    const escapedId = CSS.escape(activeTool.toolCallId)
    const row = container.querySelector(`[data-tool-call-id="${escapedId}"]`)
    row?.scrollIntoView({ block: 'center' })
  }, [activeTool.toolCallId, scrollContainerRef])

  const handleKeyDown = useCallback((event: KeyboardEvent<HTMLButtonElement>) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      handleJump()
    }
  }, [handleJump])

  const verbTitle = deriveVerbLedTitle(activeTool.status, activeTool.normalizedName, activeTool.input, activeTool.toolName)
  const duration = formatElapsedNow(activeTool.startedAt, now)

  return (
    <div
      data-testid="transcript-current-activity-bar"
      data-active-tool-call-id={activeTool.toolCallId}
      className="sticky bottom-0 z-10 -mx-4 border-t border-border bg-background/95 backdrop-blur px-4 py-2 shadow-sm"
    >
      <Button
        type="button"
        variant="ghost"
        size="sm"
        onClick={handleJump}
        onKeyDown={handleKeyDown}
        data-testid="transcript-current-activity-bar-jump"
        aria-label={`Jump to ${verbTitle.verb}${verbTitle.target ? ` ${verbTitle.target}` : ''}`}
        className="flex h-auto w-full items-center gap-2 justify-start text-left px-2 py-1.5 rounded-sm hover:bg-muted/60"
      >
        <span className="relative flex h-2 w-2 shrink-0" data-tone="info" aria-hidden="true">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-info/70 opacity-75" />
          <span className="relative inline-flex rounded-full h-2 w-2 bg-info" />
        </span>
        <span
          data-testid="transcript-current-activity-bar-verb-title"
          className="text-xs font-medium text-foreground truncate min-w-0"
        >
          {verbTitle.verb}
          {verbTitle.target ? ` ${verbTitle.target}` : ''}
          {verbTitle.trailingEllipsis ? '…' : ''}
        </span>
        {duration && (
          <span
            data-testid="transcript-current-activity-bar-duration"
            data-duration-mode="live"
            className="ml-auto text-xs tabular-nums text-muted-foreground/80 shrink-0"
          >
            {duration}
          </span>
        )}
      </Button>
    </div>
  )
}