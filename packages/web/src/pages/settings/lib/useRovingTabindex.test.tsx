// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useRef, useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  computeNextIndex,
  useRovingTabindex,
  type RovingTabindexApi,
} from './useRovingTabindex'

afterEach(() => {
  cleanup()
})

function RovingHarness({
  count,
  activeIndex: controlledActive,
  onActivate,
  onReady,
}: {
  count: number
  activeIndex?: number
  onActivate?: (index: number) => void
  onReady?: (api: RovingTabindexApi) => void
}) {
  const [internalActive, setInternalActive] = useState(controlledActive ?? 0)
  const active = controlledActive ?? internalActive
  const handleActivate = (idx: number) => {
    setInternalActive(idx)
    onActivate?.(idx)
  }
  const roving = useRovingTabindex({
    itemCount: count,
    activeIndex: active,
    onActivate: handleActivate,
  })

  const holdRef = useRef<RovingTabindexApi | null>(null)
  holdRef.current = roving
  if (holdRef.current && onReady) onReady(holdRef.current)

  return (
    <div data-testid="container">
      {Array.from({ length: count }, (_, i) => (
        <a
          key={i}
          href={`#item-${i}`}
          ref={roving.getItemRef(i)}
          tabIndex={roving.getItemTabIndex(i)}
          onKeyDown={roving.onKeyDown}
          data-testid={`item-${i}`}
          data-active={i === active ? 'true' : 'false'}
        >
          {`item-${i}`}
        </a>
      ))}
    </div>
  )
}

describe('computeNextIndex (pure logic)', () => {
  it('moves ArrowDown / ArrowRight to the next item', () => {
    expect(computeNextIndex(0, 'ArrowDown', 4)).toBe(1)
    expect(computeNextIndex(0, 'ArrowRight', 4)).toBe(1)
    expect(computeNextIndex(2, 'ArrowDown', 4)).toBe(3)
  })

  it('moves ArrowUp / ArrowLeft to the previous item', () => {
    expect(computeNextIndex(3, 'ArrowUp', 4)).toBe(2)
    expect(computeNextIndex(3, 'ArrowLeft', 4)).toBe(2)
    expect(computeNextIndex(2, 'ArrowUp', 4)).toBe(1)
  })

  it('wraps from the last item back to the first on Down/Right', () => {
    expect(computeNextIndex(3, 'ArrowDown', 4)).toBe(0)
    expect(computeNextIndex(3, 'ArrowRight', 4)).toBe(0)
  })

  it('wraps from the first item to the last on Up/Left', () => {
    expect(computeNextIndex(0, 'ArrowUp', 4)).toBe(3)
    expect(computeNextIndex(0, 'ArrowLeft', 4)).toBe(3)
  })

  it('returns null for unrelated keys', () => {
    expect(computeNextIndex(0, 'Enter', 4)).toBeNull()
    expect(computeNextIndex(0, ' ', 4)).toBeNull()
    expect(computeNextIndex(0, 'Tab', 4)).toBeNull()
    expect(computeNextIndex(0, 'a', 4)).toBeNull()
  })

  it('returns null when there are no items', () => {
    expect(computeNextIndex(0, 'ArrowDown', 0)).toBeNull()
  })

  it('wraps within a single-item list', () => {
    expect(computeNextIndex(0, 'ArrowDown', 1)).toBe(0)
    expect(computeNextIndex(0, 'ArrowUp', 1)).toBe(0)
  })
})

describe('useRovingTabindex hook', () => {
  it('marks only the active item tabIndex=0 and the rest -1', () => {
    render(<RovingHarness count={4} activeIndex={2} />)

    const items = [0, 1, 2, 3].map((i) => screen.getByTestId(`item-${i}`))

    expect(items[0].tabIndex).toBe(-1)
    expect(items[1].tabIndex).toBe(-1)
    expect(items[2].tabIndex).toBe(0)
    expect(items[3].tabIndex).toBe(-1)

    expect(items[0].getAttribute('data-active')).toBe('false')
    expect(items[2].getAttribute('data-active')).toBe('true')
  })

  it('updates roving tabIndex when the active item changes', () => {
    const { rerender } = render(<RovingHarness count={3} activeIndex={0} />)

    expect(screen.getByTestId('item-0').tabIndex).toBe(0)
    expect(screen.getByTestId('item-1').tabIndex).toBe(-1)

    rerender(<RovingHarness count={3} activeIndex={2} />)

    expect(screen.getByTestId('item-0').tabIndex).toBe(-1)
    expect(screen.getByTestId('item-1').tabIndex).toBe(-1)
    expect(screen.getByTestId('item-2').tabIndex).toBe(0)
  })

  it('ArrowDown moves focus to the next item and roving tabIndex follows', () => {
    const onActivate = vi.fn()
    render(<RovingHarness count={4} activeIndex={1} onActivate={onActivate} />)
    const start = screen.getByTestId('item-1')
    start.focus()

    fireEvent.keyDown(start, { key: 'ArrowDown' })

    const next = screen.getByTestId('item-2')
    expect(next).toHaveFocus()
    expect(onActivate).toHaveBeenCalledWith(2)
  })

  it('ArrowUp moves focus to the previous item', () => {
    const onActivate = vi.fn()
    render(<RovingHarness count={4} activeIndex={2} onActivate={onActivate} />)
    const start = screen.getByTestId('item-2')
    start.focus()

    fireEvent.keyDown(start, { key: 'ArrowUp' })

    expect(screen.getByTestId('item-1')).toHaveFocus()
    expect(onActivate).toHaveBeenCalledWith(1)
  })

  it('ArrowLeft / ArrowRight behave like Up / Down', () => {
    render(<RovingHarness count={3} activeIndex={1} />)
    const start = screen.getByTestId('item-1')
    start.focus()

    fireEvent.keyDown(start, { key: 'ArrowRight' })
    expect(screen.getByTestId('item-2')).toHaveFocus()

    fireEvent.keyDown(screen.getByTestId('item-2'), { key: 'ArrowLeft' })
    expect(screen.getByTestId('item-1')).toHaveFocus()
  })

  it('wraps from the last item to the first on ArrowDown', () => {
    const onActivate = vi.fn()
    render(<RovingHarness count={3} activeIndex={2} onActivate={onActivate} />)
    const last = screen.getByTestId('item-2')
    last.focus()

    fireEvent.keyDown(last, { key: 'ArrowDown' })

    expect(screen.getByTestId('item-0')).toHaveFocus()
    expect(onActivate).toHaveBeenLastCalledWith(0)
  })

  it('wraps from the first item to the last on ArrowUp', () => {
    const onActivate = vi.fn()
    render(<RovingHarness count={3} activeIndex={0} onActivate={onActivate} />)
    const first = screen.getByTestId('item-0')
    first.focus()

    fireEvent.keyDown(first, { key: 'ArrowUp' })

    expect(screen.getByTestId('item-2')).toHaveFocus()
    expect(onActivate).toHaveBeenLastCalledWith(2)
  })

  it('does not react to keys outside the arrow set', () => {
    const onActivate = vi.fn()
    render(<RovingHarness count={3} activeIndex={1} onActivate={onActivate} />)
    const start = screen.getByTestId('item-1')
    start.focus()

    fireEvent.keyDown(start, { key: 'Tab' })
    fireEvent.keyDown(start, { key: 'Enter' })
    fireEvent.keyDown(start, { key: 'a' })

    expect(start).toHaveFocus()
    expect(onActivate).not.toHaveBeenCalled()
  })

  it('does not move focus when the event target is not a registered item', () => {
    const onActivate = vi.fn()
    render(
      <div>
        <button data-testid="external-button" type="button">
          External
        </button>
        <RovingHarness count={3} activeIndex={1} onActivate={onActivate} />
      </div>,
    )

    fireEvent.keyDown(screen.getByTestId('external-button'), { key: 'ArrowDown' })
    expect(onActivate).not.toHaveBeenCalled()
  })
})
