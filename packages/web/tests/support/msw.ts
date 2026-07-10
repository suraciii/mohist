import { beforeEach } from 'vitest'
import { setupServer } from 'msw/node'

interface MswState {
  server: ReturnType<typeof setupServer>
  listening: boolean
  installedFetch: typeof fetch | null
}

const testGlobal = globalThis as typeof globalThis & {
  __mohistWebMswState?: MswState
}

// setup 与 inline project 可能在同一 worker 内重复求值，状态必须挂在全局。
const state = testGlobal.__mohistWebMswState ??= {
  server: setupServer(),
  listening: false,
  installedFetch: null,
}

export const server = state.server

function absolutizeRelativeFetchUrls() {
  const interceptedFetch = globalThis.fetch.bind(globalThis)
  const baseUrl = typeof window === 'undefined' ? 'http://localhost' : window.location.href
  globalThis.fetch = ((input: RequestInfo | URL, init?: RequestInit) => {
    if (typeof input === 'string' && input.startsWith('/')) {
      return interceptedFetch(new URL(input, baseUrl), init)
    }
    return interceptedFetch(input, init)
  }) as typeof fetch
}

export function ensureMswServerListening() {
  if (state.listening && globalThis.fetch === state.installedFetch) return
  if (state.listening) server.close()

  server.listen({ onUnhandledRequest: 'error' })
  absolutizeRelativeFetchUrls()
  state.installedFetch = globalThis.fetch
  state.listening = true
}

export function useMswServer(...handlers: Parameters<typeof server.use>) {
  beforeEach(() => {
    ensureMswServerListening()
    server.use(...handlers)
  })
}
