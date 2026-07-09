import { afterAll, afterEach, beforeAll } from 'vitest'
import { setupServer } from 'msw/node'

export const server = setupServer()

let _patchedOriginalFetch: typeof fetch | null = null

export function absolutizeRelativeFetchUrls() {
  if (typeof window !== 'undefined') return
  if (_patchedOriginalFetch) return
  _patchedOriginalFetch = globalThis.fetch.bind(globalThis)
  globalThis.fetch = ((input: RequestInfo | URL, init?: RequestInit) => {
    if (typeof input === 'string' && input.startsWith('/')) {
      return _patchedOriginalFetch!(new URL(input, 'http://localhost'), init)
    }
    return _patchedOriginalFetch!(input, init)
  }) as typeof fetch
}

export function useMswServer(...handlers: Parameters<typeof server.use>) {
  beforeAll(() => {
    server.resetHandlers()
    server.use(...handlers)
  })
  afterEach(() => {
    server.resetHandlers()
    server.use(...handlers)
  })
  afterAll(() => {
    server.resetHandlers()
  })
}
