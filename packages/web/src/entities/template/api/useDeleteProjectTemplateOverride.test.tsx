import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import type { ReactNode } from 'react'
import { useDeleteProjectTemplateOverride } from '..'

// react-query mutations (MSW-backed) resolve through notifyManager's scheduled
// timers and fetch microtasks; advance the clock ourselves under fake timers
// instead of polling wall-clock time (waitFor's default 1000ms is too tight on
// slow CI — design/testing.md: advance fake time, don't poll harder).
async function flush() {
  await vi.advanceTimersByTimeAsync(1000)
}

const PROJECT_ID = 'test-project'
const KEY = 'proposal'

const defaultHandlers = [
  http.delete(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, () =>
    HttpResponse.json({ success: true, data: { message: `Override ${KEY} removed` } }),
  ),
]

const queryClients: QueryClient[] = []

function createQueryClient() {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  queryClients.push(qc)
  return qc
}

function renderUseDelete(projectId: string | undefined) {
  const queryClient = createQueryClient()
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
  const result = renderHook(() => useDeleteProjectTemplateOverride(projectId), { wrapper })
  return { queryClient, result, wrapper }
}

useMswServer(...defaultHandlers)

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('useDeleteProjectTemplateOverride hook', () => {
  it('issues DELETE /api/projects/{id}/templates/{key}/override with no body', async () => {
    const deleteRequests: { method: string; url: string; body: unknown }[] = []
    server.resetHandlers(
      http.delete(
        `/api/projects/${PROJECT_ID}/templates/${KEY}/override`,
        async ({ request }) => {
          let body: unknown = null
          const text = await request.text()
          if (text) {
            try {
              body = JSON.parse(text)
            } catch {
              body = text
            }
          }
          deleteRequests.push({ method: request.method, url: request.url, body })
          return HttpResponse.json({
            success: true,
            data: { message: `Override ${KEY} removed` },
          })
        },
      ),
    )

    const { result } = renderUseDelete(PROJECT_ID)

    await act(async () => {
      result.result.current.mutate({ key: KEY })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    expect(deleteRequests).toHaveLength(1)
    expect(deleteRequests[0].method).toBe('DELETE')
    expect(deleteRequests[0].url).toContain(
      `/api/projects/${PROJECT_ID}/templates/${KEY}/override`,
    )
    expect(deleteRequests[0].body).toBeNull()
  })

  it('returns the success envelope on success', async () => {
    const { result } = renderUseDelete(PROJECT_ID)

    await act(async () => {
      result.result.current.mutate({ key: KEY })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    expect(result.result.current.data).toEqual({ message: `Override ${KEY} removed` })
  })

  it('invalidates the project-templates list on success', async () => {
    const { queryClient, result } = renderUseDelete(PROJECT_ID)

    queryClient.setQueryData(['project-templates', PROJECT_ID], [])
    queryClient.setQueryData(['project-template', PROJECT_ID, KEY], null)
    queryClient.setQueryData(['project-template', PROJECT_ID, KEY, 'override'], null)

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    await act(async () => {
      result.result.current.mutate({ key: KEY })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    const calls = invalidateSpy.mock.calls.map((c) => c[0]?.queryKey)
    const hasList = calls.some(
      (k) => Array.isArray(k) && k[0] === 'project-templates' && k[1] === PROJECT_ID,
    )
    expect(hasList).toBe(true)
  })

  it('surfaces API errors from the DELETE response', async () => {
    server.resetHandlers(
      http.delete(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, () =>
        HttpResponse.json(
          { success: false, error: 'Server error', code: 'server_error' },
          { status: 500 },
        ),
      ),
    )

    const { result } = renderUseDelete(PROJECT_ID)

    await act(async () => {
      result.result.current.mutate({ key: KEY })
      await flush()
    })

    expect(result.result.current.isError).toBe(true)

    expect(result.result.current.error).toBeInstanceOf(Error)
    expect((result.result.current.error as Error).message).toBe('Server error')
  })
})
