// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, render } from '@testing-library/react'
import { createElement, useState } from 'react'
import { useConfirmOutsideClick } from './useConfirmOutsideClick'

const FIVE_SECONDS_MS = 5000

let externalSetConfirming: ((value: boolean) => void) | null = null

interface ConfirmingPanelProps {
  timeoutMs?: number
  initialConfirming?: boolean
}

function ConfirmingPanel({ timeoutMs, initialConfirming = false }: ConfirmingPanelProps) {
  const [confirming, setConfirming] = useState(initialConfirming)
  externalSetConfirming = setConfirming
  const ref = useConfirmOutsideClick({
    confirming,
    setConfirming,
    ...(timeoutMs !== undefined ? { timeoutMs } : {}),
  })
  return createElement(
    'div',
    null,
    confirming
      ? createElement(
          'div',
          { ref, 'data-testid': 'panel' },
          createElement(
            'button',
            { type: 'button', 'data-testid': 'inside-button' },
            'inside',
          ),
        )
      : null,
    createElement(
      'button',
      { type: 'button', 'data-testid': 'outside-button' },
      'outside',
    ),
  )
}

function setConfirmingExternally(value: boolean) {
  if (externalSetConfirming === null) throw new Error('setConfirming callback not registered')
  const set = externalSetConfirming
  act(() => { set(value) })
}

beforeEach(() => {
  vi.useFakeTimers()
  externalSetConfirming = null
})

afterEach(() => {
  vi.useRealTimers()
})

describe('useConfirmOutsideClick', () => {
  it('exposes a ref pointing at an HTMLDivElement', () => {
    const { getByTestId } = render(createElement(ConfirmingPanel, { initialConfirming: true }))
    const panel = getByTestId('panel')
    expect(panel).toBeInstanceOf(HTMLDivElement)
  })

  it('does not register a listener or timer while confirming is false', () => {
    const { queryByTestId } = render(createElement(ConfirmingPanel, { initialConfirming: false }))
    expect(queryByTestId('panel')).toBeNull()
    expect(queryByTestId('outside-button')).not.toBeNull()

    vi.advanceTimersByTime(FIVE_SECONDS_MS * 2)
    expect(queryByTestId('panel')).toBeNull()
  })

  it('dismisses the panel when an outside mousedown fires while confirming is true', () => {
    const { getByTestId, queryByTestId } = render(createElement(ConfirmingPanel, { initialConfirming: true }))
    expect(getByTestId('panel')).toBeInTheDocument()

    act(() => {
      getByTestId('outside-button').dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))
    })

    expect(queryByTestId('panel')).toBeNull()
  })

  it('does not dismiss when the mousedown lands inside the panel', () => {
    const { getByTestId } = render(createElement(ConfirmingPanel, { initialConfirming: true }))

    act(() => {
      getByTestId('inside-button').dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))
    })

    expect(getByTestId('panel')).toBeInTheDocument()
  })

  it('auto-dismisses exactly 5000ms after confirming flips to true (fake timers, no wall clock)', () => {
    const { getByTestId, queryByTestId } = render(createElement(ConfirmingPanel, { initialConfirming: false }))
    expect(queryByTestId('panel')).toBeNull()

    act(() => { setConfirmingExternally(true) })
    expect(getByTestId('panel')).toBeInTheDocument()

    vi.advanceTimersByTime(FIVE_SECONDS_MS - 1)
    expect(getByTestId('panel')).toBeInTheDocument()

    act(() => { vi.advanceTimersByTime(1) })
    expect(queryByTestId('panel')).toBeNull()
  })

  it('respects a custom timeoutMs when provided', () => {
    const { getByTestId, queryByTestId } = render(
      createElement(ConfirmingPanel, { initialConfirming: true, timeoutMs: 1234 }),
    )
    expect(getByTestId('panel')).toBeInTheDocument()

    vi.advanceTimersByTime(1233)
    expect(getByTestId('panel')).toBeInTheDocument()

    act(() => { vi.advanceTimersByTime(1) })
    expect(queryByTestId('panel')).toBeNull()
  })

  it('clears the prior timer when confirming flips true → false → true (resets 5000ms each time)', () => {
    const { getByTestId, queryByTestId } = render(createElement(ConfirmingPanel, { initialConfirming: true }))

    vi.advanceTimersByTime(3000)
    expect(getByTestId('panel')).toBeInTheDocument()

    act(() => { setConfirmingExternally(false) })
    vi.advanceTimersByTime(FIVE_SECONDS_MS)
    expect(queryByTestId('panel')).toBeNull()

    act(() => { setConfirmingExternally(true) })
    expect(getByTestId('panel')).toBeInTheDocument()

    vi.advanceTimersByTime(FIVE_SECONDS_MS - 1)
    expect(getByTestId('panel')).toBeInTheDocument()

    act(() => { vi.advanceTimersByTime(1) })
    expect(queryByTestId('panel')).toBeNull()
  })

  it('removes the mousedown listener when confirming flips back to false', () => {
    const addSpy = vi.spyOn(document, 'addEventListener')
    const removeSpy = vi.spyOn(document, 'removeEventListener')

    render(createElement(ConfirmingPanel, { initialConfirming: true }))
    const addedWhileOpen = addSpy.mock.calls.filter(([type]) => type === 'mousedown').length
    expect(addedWhileOpen).toBeGreaterThan(0)
    expect(removeSpy.mock.calls.filter(([type]) => type === 'mousedown').length).toBe(0)

    act(() => { setConfirmingExternally(false) })
    expect(
      removeSpy.mock.calls.filter(([type]) => type === 'mousedown').length,
    ).toBeGreaterThanOrEqual(addedWhileOpen)

    addSpy.mockRestore()
    removeSpy.mockRestore()
  })
})
