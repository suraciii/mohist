import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import { formatSessionTime } from '@/shared/lib/format-time'

export interface SessionFollowupComposerProps {
  onSend: (text: string) => Promise<void> | void
  isSending?: boolean
  disabled?: boolean
  className?: string
  placeholder?: string
  /** End timestamp (ISO string) for the session; powers the closed-state copy. */
  endedAt?: string | null
  /**
   * Whether a submitted followup is queued awaiting the agent's first response.
   * When true (or `isSending` is true) the composer enters the queued state:
   * input disabled, "queued" indicator visible, transient `Sent` flash suppressed.
   */
  hasQueuedFollowup?: boolean
  /**
   * Explicit state override that wins over derivation. When supplied the
   * `disabled` / `isSending` / `hasQueuedFollowup` signals still gate the
   * underlying controls, but the rendered state, copy, and queued indicator
   * follow `state` directly.
   */
  state?: 'interactive' | 'queued' | 'closed'
}

type ButtonState = 'idle' | 'sending' | 'sent'
type ResolvedState = 'interactive' | 'queued' | 'closed'

const SESSION_TIME_RELATIVE_PATTERN = /^\d+[mhd] ago$|^just now$/

export function SessionFollowupComposer({
  onSend,
  isSending: isSendingProp = false,
  disabled = false,
  className,
  placeholder = 'Send a followup message to the agent...',
  endedAt,
  hasQueuedFollowup = false,
  state,
}: SessionFollowupComposerProps) {
  const [text, setText] = useState('')
  const [inlineError, setInlineError] = useState<string | null>(null)
  const [sentFlash, setSentFlash] = useState(false)
  const [localSending, setLocalSending] = useState(false)
  const sentFlashTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const trimmed = text.trim()
  const isSending = isSendingProp || localSending

  const resolvedState: ResolvedState = state ?? (
    disabled
      ? 'closed'
      : (isSending || hasQueuedFollowup)
        ? 'queued'
        : 'interactive'
  )
  const isQueued = resolvedState === 'queued'

  const canSend =
    !disabled &&
    resolvedState === 'interactive' &&
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

  if (resolvedState === 'closed') {
    let closedCopy: string
    if (endedAt) {
      const parsed = Date.parse(endedAt)
      if (Number.isFinite(parsed)) {
        const out = formatSessionTime({
          date: endedAt,
          statusKind: 'completed',
          now: Date.now(),
        })
        const relative = SESSION_TIME_RELATIVE_PATTERN.test(out.primary)
          ? out.primary
          : out.secondary
        closedCopy = `Session ended ${relative} — not accepting new followups.`
      } else {
        closedCopy = 'Session is no longer accepting followups.'
      }
    } else {
      closedCopy = 'Session is no longer accepting followups.'
    }
    return (
      <div
        data-testid="session-followup-composer"
        data-disabled="true"
        data-state="closed"
        className={cn(
          'shrink-0 border-t border-border bg-muted px-4 py-2 text-xs text-muted-foreground',
          className,
        )}
      >
        {closedCopy}
      </div>
    )
  }

  const buttonState: ButtonState = isSending
    ? 'sending'
    : (sentFlash && !hasQueuedFollowup ? 'sent' : 'idle')

  const statusLabel = isQueued
    ? 'Queued — waiting for agent...'
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
          disabled={disabled || isSending || isQueued}
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
              : buttonState === 'sent'
                ? 'success'
                : 'neutral'
          }
          className={cn(
            isQueued
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
