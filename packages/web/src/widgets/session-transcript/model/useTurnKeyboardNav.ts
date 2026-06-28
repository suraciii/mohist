import { useEffect, useRef, type RefObject } from 'react'

const FOCUS_BAIL_SELECTOR =
  'input, textarea, select, [contenteditable], [data-composer-input]'

const ACTIVATION_THRESHOLD_PX = 120

function computeCurrentIndex(
  containerEl: HTMLElement | null,
  refs: Map<number, HTMLDivElement>,
): number {
  if (!containerEl) return 0
  const containerRect = containerEl.getBoundingClientRect()
  const threshold = containerRect.top + ACTIVATION_THRESHOLD_PX

  let currentIndex = 0
  const sortedKeys = Array.from(refs.keys()).sort((a, b) => a - b)
  for (const index of sortedKeys) {
    const el = refs.get(index)
    if (!el) continue
    const rect = el.getBoundingClientRect()
    if (rect.top <= threshold) {
      currentIndex = index
    } else {
      break
    }
  }
  return currentIndex
}

function isEditableFocused(): boolean {
  const active = document.activeElement
  if (!active || !(active instanceof Element)) return false
  return active.closest(FOCUS_BAIL_SELECTOR) !== null
}

export interface UseTurnKeyboardNavOptions {
  scrollContainerRef: RefObject<HTMLElement | null> | null | undefined
  turnRefs: Map<number, HTMLDivElement>
  turnCount: number
}

export function useTurnKeyboardNav({
  scrollContainerRef,
  turnRefs,
  turnCount,
}: UseTurnKeyboardNavOptions): void {
  const turnRefsRef = useRef(turnRefs)
  turnRefsRef.current = turnRefs
  const turnCountRef = useRef(turnCount)
  turnCountRef.current = turnCount

  useEffect(() => {
    const handler = (event: Event) => {
      const ke = event as KeyboardEvent
      if (ke.metaKey || ke.ctrlKey || ke.altKey) return
      if (ke.key !== 'j' && ke.key !== 'k' && ke.key !== 'g' && ke.key !== 'G') return
      if (isEditableFocused()) return

      const refs = turnRefsRef.current
      const count = turnCountRef.current
      if (count === 0) return

      const containerEl = scrollContainerRef?.current ?? null

      let targetIndex: number
      if (ke.key === 'j') {
        const currentIndex = computeCurrentIndex(containerEl, refs)
        targetIndex = Math.min(currentIndex + 1, count)
        if (targetIndex === currentIndex) return
      } else if (ke.key === 'k') {
        const currentIndex = computeCurrentIndex(containerEl, refs)
        targetIndex = Math.max(currentIndex - 1, 1)
        if (targetIndex === currentIndex) return
      } else if (ke.key === 'G' || (ke.key === 'g' && ke.shiftKey)) {
        targetIndex = count
      } else if (ke.key === 'g' && !ke.shiftKey) {
        targetIndex = 1
      } else {
        return
      }

      if (targetIndex < 1 || targetIndex > count) return

      const ref = refs.get(targetIndex)
      if (!ref) return

      ref.scrollIntoView({ block: 'start' })
    }

    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [scrollContainerRef])
}
