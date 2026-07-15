import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'

export interface SessionFollowupComposerProps {
  onSend: (text: string) => Promise<void> | void
  isSending?: boolean
  disabled?: boolean
  className?: string
  placeholder?: string
}

type ComposerState = 'idle' | 'sending' | 'sent'

export function SessionFollowupComposer({
  onSend,
  isSending: isSendingProp = false,
  disabled = false,
  className,
  placeholder = 'Send a followup message to the agent...',
}: SessionFollowupComposerProps) {
  const [text, setText] = useState('')
  const [inlineError, setInlineError] = useState<string | null>(null)
  const [sentFlash, setSentFlash] = useState(false)
  const [localSending, setLocalSending] = useState(false)
  const sentFlashTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const trimmed = text.trim()
  const isSending = isSendingProp || localSending
  const canSend = !disabled && trimmed.length > 0 && !isSending
  const composerState: ComposerState = isSending ? 'sending' : (sentFlash ? 'sent' : 'idle')

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
      setSentFlash(true)
      sentFlashTimerRef.current = setTimeout(() => {
        setSentFlash(false)
        sentFlashTimerRef.current = null
      }, 1500)
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

  if (disabled) {
    return (
      <div
        data-testid="session-followup-composer"
        data-disabled="true"
        className={cn(
          'shrink-0 border-t border-border bg-muted px-4 py-2 text-xs text-muted-foreground',
          className,
        )}
      >
        Session is no longer accepting followups.
      </div>
    )
  }

  const statusLabel = composerState === 'sending'
    ? 'Sending...'
    : composerState === 'sent'
      ? 'Sent'
      : null

  return (
    <form
      data-testid="session-followup-composer"
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
          disabled={isSending}
          aria-label="Followup message"
          className="h-10 min-h-10 resize-none md:h-auto md:min-h-12"
        />
        <Button
          type="submit"
          size="sm"
          disabled={!canSend}
          data-testid="session-followup-send"
          data-state={composerState}
        >
          {isSending ? 'Sending...' : 'Send'}
        </Button>
      </div>

      <div className="mt-1 flex items-center justify-between text-xs" aria-live="polite">
        <span
          data-testid="session-followup-status"
          data-tone={composerState === 'sent' ? 'success' : 'neutral'}
          className={cn(
            composerState === 'sent' ? 'text-success' : 'text-transparent',
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
