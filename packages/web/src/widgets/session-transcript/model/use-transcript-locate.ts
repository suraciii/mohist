import { useCallback, useRef, type RefObject } from 'react'

export interface TranscriptLocateTarget {
  toolCallId?: string
  turnId?: string
  groupId?: string
}

export type ExpansionRegistry = Map<string, () => void>
export type HighlightRegistry = Map<string, (on: boolean) => void>

interface UseTranscriptLocateOptions {
  scrollContainerRef?: RefObject<HTMLElement | null>
}

export function useTranscriptLocate({ scrollContainerRef }: UseTranscriptLocateOptions) {
  const expansionRegistry = useRef<ExpansionRegistry>(new Map()).current
  const highlightRegistry = useRef<HighlightRegistry>(new Map()).current
  const lastHighlightedIdRef = useRef<string | null>(null)

  const locate = useCallback((target: TranscriptLocateTarget) => {
    target.groupId && expansionRegistry.get(target.groupId)?.()

    requestAnimationFrame(() => {
      const rowAnchorId = target.toolCallId ?? target.turnId
      const container = scrollContainerRef?.current
      if (!container || !rowAnchorId) return

      const attribute = target.toolCallId ? 'data-tool-call-id' : 'data-turn-id'
      const row = container.querySelector(`[${attribute}="${CSS.escape(rowAnchorId)}"]`)
      if (!row) return

      row.scrollIntoView({ block: 'center' })

      const previousId = lastHighlightedIdRef.current
      if (previousId) highlightRegistry.get(previousId)?.(false)
      highlightRegistry.get(rowAnchorId)?.(true)
      lastHighlightedIdRef.current = rowAnchorId
    })
  }, [expansionRegistry, highlightRegistry, scrollContainerRef])

  return { locate, expansionRegistry, highlightRegistry }
}
