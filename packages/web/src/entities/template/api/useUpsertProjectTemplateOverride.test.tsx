import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import type { ReactNode } from 'react'
import {
  useUpsertProjectTemplateOverride,
  type ProjectTemplateOverridePayload,
} from '..'

// react-query mutations (MSW-backed) resolve through notifyManager's scheduled
// timers and fetch microtasks; advance the clock ourselves under fake timers
// instead of polling wall-clock time (waitFor's default 1000ms is too tight on
// slow CI — design/testing.md: advance fake time, don't poll harder).
async function flush() {
  await vi.advanceTimersByTimeAsync(1000)
}

const PROJECT_ID = 'test-project'
const KEY = 'proposal'

const PAYLOAD: ProjectTemplateOverridePayload = {
  displayName: 'Updated Proposal',
  description: 'Updated description',
  tags: ['plan', 'openspec'],
  stage: 'plan',
  body: 'updated body content',
}

const STORED_ROW = {
  projectId: PROJECT_ID,
  key: KEY,
  ...PAYLOAD,
  updatedAt: '2024-01-01T00:00:00.000Z',
}

const defaultHandlers = [
  http.put(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, () =>
    HttpResponse.json({ success: true, data: STORED_ROW }),
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

function renderUseUpsert(projectId: string | undefined) {
  const queryClient = createQueryClient()
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
  const result = renderHook(() => useUpsertProjectTemplateOverride(projectId), { wrapper })
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

describe('useUpsertProjectTemplateOverride hook', () => {
  it('issues PUT /api/projects/{id}/templates/{key}/override with the payload', async () => {
    const putRequests: { method: string; url: string; body: unknown }[] = []
    server.resetHandlers(
      http.put(
        `/api/projects/${PROJECT_ID}/templates/${KEY}/override`,
        async ({ request }) => {
          const body = await request.json()
          putRequests.push({ method: request.method, url: request.url, body })
          return HttpResponse.json({ success: true, data: STORED_ROW })
        },
      ),
    )

    const { result } = renderUseUpsert(PROJECT_ID)

    await act(async () => {
      result.result.current.mutate({ key: KEY, payload: PAYLOAD })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    expect(putRequests).toHaveLength(1)
    expect(putRequests[0].method).toBe('PUT')
    expect(putRequests[0].url).toContain(
      `/api/projects/${PROJECT_ID}/templates/${KEY}/override`,
    )
    expect(putRequests[0].body).toEqual(PAYLOAD)
  })

  it('returns the stored row on success', async () => {
    const { result } = renderUseUpsert(PROJECT_ID)

    await act(async () => {
      result.result.current.mutate({ key: KEY, payload: PAYLOAD })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    expect(result.result.current.data).toEqual(STORED_ROW)
  })

  it('invalidates the project-templates list and the project-template entry on success', async () => {
    const { queryClient, result } = renderUseUpsert(PROJECT_ID)

    queryClient.setQueryData(['project-templates', PROJECT_ID], [])
    queryClient.setQueryData(['project-template', PROJECT_ID, KEY], null)
    queryClient.setQueryData(['project-template', PROJECT_ID, KEY, 'override'], null)

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    await act(async () => {
      result.result.current.mutate({ key: KEY, payload: PAYLOAD })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    const calls = invalidateSpy.mock.calls.map((c) => c[0]?.queryKey)
    const hasList = calls.some(
      (k) => Array.isArray(k) && k[0] === 'project-templates' && k[1] === PROJECT_ID,
    )
    const hasEntry = calls.some(
      (k) =>
        Array.isArray(k) &&
        k[0] === 'project-template' &&
        k[1] === PROJECT_ID &&
        k[2] === KEY,
    )
    expect(hasList).toBe(true)
    expect(hasEntry).toBe(true)
  })

  it('surfaces API errors from the PUT response', async () => {
    server.resetHandlers(
      http.put(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, () =>
        HttpResponse.json(
          { success: false, error: 'Body is required', code: 'bad_request' },
          { status: 400 },
        ),
      ),
    )

    const { result } = renderUseUpsert(PROJECT_ID)

    await act(async () => {
      result.result.current.mutate({ key: KEY, payload: PAYLOAD })
      await flush()
    })

    expect(result.result.current.isError).toBe(true)

    expect(result.result.current.error).toBeInstanceOf(Error)
    expect((result.result.current.error as Error).message).toBe('Body is required')
  })
})
