import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import type { FollowupStatus } from '../../../entities/coder-session'

export interface SessionFollowupComposerProps {
  onSend: (text: string) => Promise<void> | void
  isSending?: boolean
  disabled?: boolean
  className?: string
  placeholder?: string
  hasQueuedFollowup?: boolean
  state?: 'interactive' | 'queued' | 'unavailable' | 'closed'
  endedAt?: string | null
  followupStatus?: FollowupStatus | null
}

type ButtonState = 'idle' | 'sending' | 'sent'
type ResolvedState = 'interactive' | 'queued' | 'unavailable'

export function SessionFollowupComposer({
  onSend,
  isSending: isSendingProp = false,
  disabled = false,
  className,
  placeholder = 'Send a followup message to the agent...',
  hasQueuedFollowup = false,
  state,
  followupStatus = null,
}: SessionFollowupComposerProps) {
  const [text, setText] = useState('')
  const [inlineError, setInlineError] = useState<string | null>(null)
  const [sentFlash, setSentFlash] = useState(false)
  const [localSending, setLocalSending] = useState(false)
  const sentFlashTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const trimmed = text.trim()
  const isSending = isSendingProp || localSending

  const resolvedState: ResolvedState = state === 'closed' ? 'unavailable' : state ?? (
    disabled
      ? 'unavailable'
      : (isSending || hasQueuedFollowup)
        ? 'queued'
        : 'interactive'
  )
  const observedTurnStatus = followupStatus?.turnStatus?.toLowerCase()
  const observedInputAcceptance = followupStatus?.inputAcceptance?.toLowerCase()
  const isObservedAccepted = followupStatus?.outcome === 'accepted'
    && (observedInputAcceptance == null || observedInputAcceptance === 'accepted')
  const isObservedQueued = isObservedAccepted && observedTurnStatus === 'queued'
  const isObservedExecuting = isObservedAccepted && observedTurnStatus === 'executing'
  const observedTerminalStatus = isObservedAccepted && (
    observedTurnStatus === 'completed'
    || observedTurnStatus === 'failed'
    || observedTurnStatus === 'cancelled'
    || observedTurnStatus === 'unknown'
  )
    ? observedTurnStatus
    : null
  const isQueued = isObservedQueued || (resolvedState === 'queued' && !isObservedExecuting)

  const canSend =
    !disabled &&
    trimmed.length > 0 &&
    !isSending

  useEffect(() => {
    if (disabled) setInlineError(null)
  }, [disabled])

  useEffect(() => {
    return () => {
      if (sentFlashTimerRef.current !== null) {
        clearTimeout(sentFlashTimerRef.current)
      }
    }
  }, [])

  async function submit() {
    if (!canSend) return
    setInlineError(null)
    setSentFlash(false)
    if (sentFlashTimerRef.current !== null) {
      clearTimeout(sentFlashTimerRef.current)
      sentFlashTimerRef.current = null
    }
    setLocalSending(true)
    try {
      await onSend(trimmed)
      setText('')
      if (!hasQueuedFollowup) {
        setSentFlash(true)
        sentFlashTimerRef.current = setTimeout(() => {
          setSentFlash(false)
          sentFlashTimerRef.current = null
        }, 1500)
      }
    } catch (err: unknown) {
      setInlineError(err instanceof Error ? err.message : 'An unexpected error occurred.')
    } finally {
      setLocalSending(false)
    }
  }

  function handleSubmit(evt: FormEvent<HTMLFormElement>) {
    evt.preventDefault()
    submit()
  }

  function handleKeyDown(evt: KeyboardEvent<HTMLTextAreaElement>) {
    if (evt.key === 'Enter' && !evt.shiftKey && !evt.nativeEvent.isComposing) {
      evt.preventDefault()
      submit()
    }
  }

  if (resolvedState === 'unavailable') {
    return (
      <div
        data-testid="session-followup-composer"
        data-disabled="true"
        data-state="unavailable"
        className={cn(
          'shrink-0 border-t border-border bg-muted px-4 py-2 text-xs text-muted-foreground',
          className,
        )}
      >
        Session activity is unknown. Follow-up is unavailable until the activity is resolved.
      </div>
    )
  }

  const buttonState: ButtonState = isSending
    ? 'sending'
    : (sentFlash && !hasQueuedFollowup ? 'sent' : 'idle')

  const statusLabel = followupStatus?.outcome === 'rejected'
    ? 'Rejected'
    : followupStatus?.outcome === 'unknown'
      ? 'Outcome unknown — retry with the same key'
      : isObservedExecuting
        ? 'Executing'
        : observedTerminalStatus
          ? observedTerminalStatus[0].toUpperCase() + observedTerminalStatus.slice(1)
        : isQueued
          ? followupStatus ? 'Accepted — pending' : 'Queued — waiting for agent...'
    : buttonState === 'sending'
      ? 'Sending...'
      : buttonState === 'sent'
        ? 'Sent'
        : null

  return (
    <form
      data-testid="session-followup-composer"
      data-state={resolvedState}
      onSubmit={handleSubmit}
      className={cn(
        'shrink-0 border-t border-border bg-background px-4 py-2 md:py-3',
        className,
      )}
    >
      <div className="flex items-end gap-2">
        <Textarea
          data-testid="session-followup-input"
          value={text}
          onChange={(evt) => setText(evt.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={placeholder}
          rows={2}
          disabled={disabled || isSending}
          aria-label="Followup message"
          className="h-10 min-h-10 resize-none md:h-auto md:min-h-12"
        />
        <Button
          type="submit"
          size="sm"
          disabled={!canSend}
          data-testid="session-followup-send"
          data-state={buttonState}
        >
          {isSending ? 'Sending...' : 'Send'}
        </Button>
      </div>

      <div className="mt-1 flex items-center justify-between text-xs" aria-live="polite">
        <span
          data-testid="session-followup-status"
          data-tone={
            isQueued
              ? 'queued'
              : isObservedExecuting
                ? 'executing'
                : observedTerminalStatus === 'completed'
                  ? 'success'
                  : observedTerminalStatus
                    ? 'terminal'
                : followupStatus?.outcome === 'rejected' || followupStatus?.outcome === 'unknown'
                  ? 'outcome'
              : buttonState === 'sent'
                ? 'success'
                : 'neutral'
          }
          className={cn(
            isQueued
              ? 'text-warning'
              : isObservedExecuting
                ? 'text-info'
                : observedTerminalStatus === 'completed'
                  ? 'text-success'
                  : observedTerminalStatus === 'failed'
                    ? 'text-destructive'
                    : observedTerminalStatus
                      ? 'text-warning'
                : followupStatus?.outcome === 'rejected'
                  ? 'text-destructive'
                  : followupStatus?.outcome === 'unknown'
                    ? 'text-warning'
                    : buttonState === 'sent'
                      ? 'text-success'
                      : 'text-transparent',
          )}
        >
          {statusLabel ?? 'placeholder'}
        </span>
        {inlineError && (
          <span
            role="alert"
            data-testid="session-followup-error"
            data-tone="danger"
            className="text-danger"
          >
            {inlineError}
          </span>
        )}
      </div>
    </form>
  )
}
