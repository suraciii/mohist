import '@testing-library/jest-dom'
import { cleanup, configure } from '@testing-library/react'
import { afterEach, beforeEach, vi } from 'vitest'
import { restoreScopedProperties } from './support/scoped-property'
import { resetSonnerFake } from './support/sonner-fake'
import { resetSignalrFake } from './support/signalr-fake'
import {
  ensureMswServerListening,
  resetUnhandledRequests,
  server,
  takeUnhandledRequestError,
} from './support/msw'

ensureMswServerListening()
configure({ asyncUtilTimeout: 10_000 })

beforeEach(() => {
  resetUnhandledRequests()
  ensureMswServerListening()
  resetSignalrFake()
})

let _reducedMotionOverride: boolean | undefined
const _unexpectedConsoleCalls = new Map<string, number>()

function formatConsoleValue(value: unknown) {
  if (value instanceof Error) return value.stack ?? value.message
  if (typeof value === 'string') return value
  try {
    return JSON.stringify(value)
  } catch {
    return String(value)
  }
}

function recordUnexpectedConsoleCall(level: 'error' | 'warn', values: unknown[]) {
  const message = `${level}: ${values.map(formatConsoleValue).join(' ')}`
  _unexpectedConsoleCalls.set(message, (_unexpectedConsoleCalls.get(message) ?? 0) + 1)
}

function takeUnexpectedConsoleError(): Error | null {
  if (_unexpectedConsoleCalls.size === 0) return null

  const calls = [..._unexpectedConsoleCalls.entries()]
    .map(([message, count]) => `  - ${message}${count === 1 ? '' : ` (${count}x)`}`)
    .join('\n')
  _unexpectedConsoleCalls.clear()
  return new Error(`Unexpected console output:\n${calls}`)
}

beforeEach(() => {
  _unexpectedConsoleCalls.clear()
  vi.spyOn(console, 'error').mockImplementation((...values) => {
    recordUnexpectedConsoleCall('error', values)
  })
  vi.spyOn(console, 'warn').mockImplementation((...values) => {
    recordUnexpectedConsoleCall('warn', values)
  })
})

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

function restoreAnimationFrameFallback() {
  if (typeof window === 'undefined') return

  const requestFrame = (callback: FrameRequestCallback) =>
    setTimeout(() => callback(performance.now()), 16) as unknown as number
  const cancelFrame = (handle: number) => clearTimeout(handle)

  window.requestAnimationFrame = requestFrame
  window.cancelAnimationFrame = cancelFrame
  globalThis.requestAnimationFrame = requestFrame
  globalThis.cancelAnimationFrame = cancelFrame
}

beforeEach(() => {
  restoreAnimationFrameFallback()
})

afterEach(() => {
  cleanup()
  // isolate:false shares document.body across files; testing-library's
  // cleanup() only unmounts containers it tracks. Third-party portals
  // (e.g. base-ui Dialog) append their own nodes to body and also mutate
  // body attributes (overflow:hidden while open); both escape cleanup and
  // leak into the next file. Wipe body after React unmounts so no portal
  // debris or style side-effects survive between tests.
  if (typeof document !== 'undefined' && document.body) {
    document.body.innerHTML = ''
    document.body.removeAttribute('style')
  }
  restoreScopedProperties()
  server.resetHandlers()
  _reducedMotionOverride = undefined
  resetSonnerFake()
  resetSignalrFake()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  restoreAnimationFrameFallback()
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
  const errors = [takeUnhandledRequestError(), takeUnexpectedConsoleError()]
    .filter((error): error is Error => error !== null)
  if (errors.length === 1) throw errors[0]
  if (errors.length > 1) throw new AggregateError(errors, 'Test emitted unexpected boundary errors')
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

restoreAnimationFrameFallback()
