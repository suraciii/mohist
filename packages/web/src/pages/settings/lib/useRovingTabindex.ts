import { useCallback, useRef } from 'react'

const NAV_KEYS = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'])

export interface UseRovingTabindexOptions {
  itemCount: number
  activeIndex: number
  onActivate?: (index: number) => void
}

export interface RovingTabindexApi {
  getItemTabIndex(index: number): 0 | -1
  getItemRef(index: number): (el: HTMLElement | null) => void
  onKeyDown: (event: React.KeyboardEvent<HTMLElement>) => void
  focusItem: (index: number) => void
}

export function computeNextIndex(
  current: number,
  key: string,
  itemCount: number,
): number | null {
  if (itemCount <= 0) return null
  if (key === 'ArrowDown' || key === 'ArrowRight') {
    return (current + 1) % itemCount
  }
  if (key === 'ArrowUp' || key === 'ArrowLeft') {
    return (current - 1 + itemCount) % itemCount
  }
  return null
}

export function useRovingTabindex(
  options: UseRovingTabindexOptions,
): RovingTabindexApi {
  const { itemCount, activeIndex, onActivate } = options
  const refs = useRef<(HTMLElement | null)[]>([])

  const getItemTabIndex = useCallback(
    (index: number) => (index >= 0 && index === activeIndex ? 0 : -1),
    [activeIndex],
  )

  const getItemRef = useCallback(
    (index: number) => (el: HTMLElement | null) => {
      refs.current[index] = el
    },
    [],
  )

  const focusItem = useCallback((index: number) => {
    const el = refs.current[index]
    if (el) el.focus()
  }, [])

  const handleKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLElement>) => {
      if (!NAV_KEYS.has(event.key)) return
      const currentIndex = refs.current.findIndex((r) => r === event.target)
      if (currentIndex < 0) return
      const next = computeNextIndex(currentIndex, event.key, itemCount)
      if (next === null) return
      event.preventDefault()
      focusItem(next)
      onActivate?.(next)
    },
    [itemCount, focusItem, onActivate],
  )

  return {
    getItemTabIndex,
    getItemRef,
    onKeyDown: handleKeyDown,
    focusItem,
  }
}
