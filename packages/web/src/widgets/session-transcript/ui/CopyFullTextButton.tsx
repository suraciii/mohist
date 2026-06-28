import { useEffect, useMemo, useState } from 'react'
import { toast } from 'sonner'
import type { DisplayTurn } from '../model/session-transcript-display'
import { serializeTranscriptPlainText } from '../model/serialize-transcript'

interface CopyFullTextButtonProps {
  turns: DisplayTurn[]
  label?: string
}

export function CopyFullTextButton({
  turns,
  label = 'Copy full text',
}: CopyFullTextButtonProps) {
  const serialized = useMemo(() => serializeTranscriptPlainText(turns), [turns])
  const [status, setStatus] = useState<'idle' | 'copied' | 'failed'>('idle')

  useEffect(() => {
    if (status === 'idle') return
    const timer = setTimeout(() => setStatus('idle'), 2000)
    return () => clearTimeout(timer)
  }, [status])

  const isEmpty = turns.length === 0
  const disabled = isEmpty

  function handleCopy() {
    if (isEmpty) return
    if (!navigator.clipboard?.writeText) {
      setStatus('failed')
      toast.error('Clipboard is unavailable in this browser.')
      return
    }
    navigator.clipboard.writeText(serialized).then(
      () => setStatus('copied'),
      () => {
        setStatus('failed')
        toast.error('Failed to copy transcript to clipboard.')
      },
    )
  }

  const buttonLabel = status === 'copied'
    ? 'Copied!'
    : label

  return (
    <button
      type="button"
      data-copy-full-text=""
      data-state={status}
      aria-label="Copy full transcript text"
      disabled={disabled}
      onClick={handleCopy}
      className="inline-flex items-center gap-1 px-2.5 py-1 text-xs rounded border border-gray-200 bg-white text-gray-700 hover:bg-gray-50 transition-colors disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:bg-white"
    >
      {buttonLabel}
    </button>
  )
}