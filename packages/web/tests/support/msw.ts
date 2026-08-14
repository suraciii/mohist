import { beforeEach } from 'vitest'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'

const baselineHandlers = [
  http.get('*/api/workflow-runs/:workflowRunId', ({ params }) =>
    HttpResponse.json({
      success: true,
      data: {
        status: { workflowRunId: String(params.workflowRunId), status: 'completed' },
        issueRef: null,
        workflowProfileId: 'mohist/local',
        agentAction: null,
        agentRuntime: null,
      },
    }),
  ),
]

interface MswState {
  server: ReturnType<typeof setupServer>
  listening: boolean
  installedFetch: typeof fetch | null
  unhandledRequests: Map<string, number>
}

const testGlobal = globalThis as typeof globalThis & {
  __mohistWebMswState?: MswState
}

// setup 与 inline project 可能在同一 worker 内重复求值，状态必须挂在全局。
const state = (testGlobal.__mohistWebMswState ??= {
  server: setupServer(...baselineHandlers),
  listening: false,
  installedFetch: null,
  unhandledRequests: new Map(),
})

export const server = state.server

function rejectUnhandledRequest(request: Request): never {
  const url = new URL(request.url)
  const description = `${request.method} ${url.pathname}${url.search}`
  state.unhandledRequests.set(description, (state.unhandledRequests.get(description) ?? 0) + 1)
  throw new Error(`Unhandled MSW request: ${description}`)
}

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

  server.listen({ onUnhandledRequest: rejectUnhandledRequest })
  absolutizeRelativeFetchUrls()
  state.installedFetch = globalThis.fetch
  state.listening = true
}

export function resetUnhandledRequests() {
  state.unhandledRequests.clear()
}

export function takeUnhandledRequestError(): Error | null {
  if (state.unhandledRequests.size === 0) return null

  const requests = [...state.unhandledRequests.entries()]
    .map(([request, count]) => `  - ${request}${count === 1 ? '' : ` (${count}x)`}`)
    .join('\n')
  state.unhandledRequests.clear()
  return new Error(`Unhandled MSW requests:\n${requests}`)
}

export function useMswServer(...handlers: Parameters<typeof server.use>) {
  beforeEach(() => {
    ensureMswServerListening()
    server.use(...handlers)
  })
}
