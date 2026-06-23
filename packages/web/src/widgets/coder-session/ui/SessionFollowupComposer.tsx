import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { ApiError } from '@/shared/api/client'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import { useFollowupMutation } from '../../../entities/coder-session'

export interface SessionFollowupComposerProps {
  issueNumber: number
  sessionName: string
  disabled?: boolean
  className?: string
  placeholder?: string
}

type ComposerState = 'idle' | 'sending' | 'sent'

function resolveFollowupErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 409) {
      return err.message && err.message.trim().length > 0
        ? err.message
        : 'Session is no longer active.'
    }
    if (err.status === 503) {
      return err.message && err.message.trim().length > 0
        ? err.message
        : 'Runner is offline. Try again once the runner reconnects.'
    }
    if (err.status === 404) {
      return 'Session not found.'
    }
    return err.message || `Request failed with status ${err.status}.`
  }
  if (err instanceof Error) return err.message
  return 'An unexpected error occurred.'
}

export function SessionFollowupComposer({
  issueNumber,
  sessionName,
  disabled = false,
  className,
  placeholder = 'Send a followup message to the agent...',
}: SessionFollowupComposerProps) {
  const [text, setText] = useState('')
  const [inlineError, setInlineError] = useState<string | null>(null)
  const [sentFlash, setSentFlash] = useState(false)
  const sentFlashTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const followupMutation = useFollowupMutation()

  const trimmed = text.trim()
  const isDisabled = disabled
  const canSend = !isDisabled && trimmed.length > 0 && !followupMutation.isPending
  const isSending = followupMutation.isPending
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

  function submit() {
    if (!canSend) return
    setInlineError(null)
    setSentFlash(false)
    if (sentFlashTimerRef.current !== null) {
      clearTimeout(sentFlashTimerRef.current)
      sentFlashTimerRef.current = null
    }
    followupMutation.mutate(
      { issueNumber, sessionName, text: trimmed },
      {
        onSuccess: () => {
          setText('')
          setSentFlash(true)
          sentFlashTimerRef.current = setTimeout(() => {
            setSentFlash(false)
            sentFlashTimerRef.current = null
          }, 1500)
        },
        onError: (err) => {
          setInlineError(resolveFollowupErrorMessage(err))
        },
      },
    )
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
          'shrink-0 border-t border-gray-200 bg-gray-50 px-4 py-2 text-xs text-gray-500',
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
        'shrink-0 border-t border-gray-200 bg-white px-4 py-3',
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
          className="min-h-12 resize-none"
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
          className={cn(
            composerState === 'sent' ? 'text-green-600' : 'text-transparent',
          )}
        >
          {statusLabel ?? 'placeholder'}
        </span>
        {inlineError && (
          <span
            role="alert"
            data-testid="session-followup-error"
            className="text-red-600"
          >
            {inlineError}
          </span>
        )}
      </div>
    </form>
  )
}
