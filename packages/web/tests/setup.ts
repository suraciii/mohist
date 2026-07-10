import '@testing-library/jest-dom'
import { cleanup } from '@testing-library/react'
import { afterEach, beforeEach, vi } from 'vitest'
import { resetSonnerFake } from './support/sonner-fake'
import { resetSignalrFake } from './support/signalr-fake'
import { ensureMswServerListening, server } from './support/msw'

ensureMswServerListening()

beforeEach(() => {
  ensureMswServerListening()
})

let _reducedMotionOverride: boolean | undefined

const _matchMediaMock = ((query: string) => ({
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
  })) as typeof window.matchMedia

afterEach(() => {
  cleanup()
  server.resetHandlers()
  _reducedMotionOverride = undefined
  resetSonnerFake()
  resetSignalrFake()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  if (typeof window !== 'undefined') {
    window.localStorage.clear()
    window.sessionStorage.clear()
    document.title = ''
    document.documentElement.className = ''
    window.matchMedia = _matchMediaMock
    try {
      Object.defineProperty(window, 'innerWidth', { configurable: true, writable: true, value: 1280 })
    } catch (_) { /* ignore non-configurable */ }
  }
})

if (typeof window !== 'undefined') {
  if (!window.matchMedia) {
    window.matchMedia = _matchMediaMock
  }
  if (!window.innerWidth) {
    Object.defineProperty(window, 'innerWidth', { writable: true, value: 1280 })
  }
}

/**
 * Opt an individual test into prefers-reduced-motion: reduce.
 * The override is reset after each test via afterEach cleanup.
 */
export function setPrefersReducedMotion(reduce: boolean) {
  _reducedMotionOverride = reduce
  if (typeof window !== 'undefined') {
    window.matchMedia = _matchMediaMock
  }
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
