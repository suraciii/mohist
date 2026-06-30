import '@testing-library/jest-dom'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

let _reducedMotionOverride: boolean | undefined

afterEach(() => {
  cleanup()
  _reducedMotionOverride = undefined
})

if (typeof window !== 'undefined' && !window.matchMedia) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: _reducedMotionOverride === true
        ? query === '(prefers-reduced-motion: reduce)'
        : false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  })
}

/**
 * Opt an individual test into prefers-reduced-motion: reduce.
 * The override is reset after each test via afterEach cleanup.
 */
export function setPrefersReducedMotion(reduce: boolean) {
  _reducedMotionOverride = reduce
}

if (typeof window !== 'undefined' && !window.innerWidth) {
  Object.defineProperty(window, 'innerWidth', { writable: true, value: 1280 })
}

if (typeof window !== 'undefined' && !window.ResizeObserver) {
  class ResizeObserverPolyfill {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  Object.defineProperty(window, 'ResizeObserver', {
    writable: true,
    configurable: true,
    value: ResizeObserverPolyfill,
  })
}

if (typeof window !== 'undefined' && typeof window.Element !== 'undefined' && !window.Element.prototype.scrollIntoView) {
  window.Element.prototype.scrollIntoView = function scrollIntoView() {}
}

if (typeof window !== 'undefined' && !window.requestAnimationFrame) {
  window.requestAnimationFrame = (cb: FrameRequestCallback) => setTimeout(() => cb(performance.now()), 16) as unknown as number
  window.cancelAnimationFrame = (handle: number) => clearTimeout(handle)
}
