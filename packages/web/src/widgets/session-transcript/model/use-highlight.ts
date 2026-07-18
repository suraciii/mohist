import { useCallback, useEffect, useState } from 'react'

export const HIGHLIGHT_DURATION_MS = 1500

interface UseHighlightOptions {
  durationMs?: number
  now?: number
}

export function useHighlight({ durationMs = HIGHLIGHT_DURATION_MS, now }: UseHighlightOptions = {}) {
  const [isHighlighted, setIsHighlighted] = useState(false)

  const setHighlighted = useCallback((on: boolean) => {
    setIsHighlighted(on)
  }, [])

  useEffect(() => {
    if (!isHighlighted) return
    const timeout = window.setTimeout(() => setIsHighlighted(false), durationMs)
    return () => window.clearTimeout(timeout)
  }, [durationMs, isHighlighted, now])

  useEffect(() => {
    if (!isHighlighted) return

    const dismissOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setIsHighlighted(false)
    }

    window.addEventListener('keydown', dismissOnEscape)
    return () => window.removeEventListener('keydown', dismissOnEscape)
  }, [isHighlighted])

  return { isHighlighted, setHighlighted }
}
