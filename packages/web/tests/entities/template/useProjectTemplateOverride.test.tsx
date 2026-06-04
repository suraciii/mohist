// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import type { ReactNode } from 'react'
import { useProjectTemplateOverride } from '../../../src/entities/template'

const PROJECT_ID = 'test-project'
const KEY = 'proposal'

const OVERRIDE_ROW = {
  projectId: PROJECT_ID,
  key: KEY,
  displayName: 'Generate Proposal',
  description: 'project override description',
  tags: ['plan', 'openspec'],
  stage: 'plan',
  body: 'project override body',
  updatedAt: '2024-01-01T00:00:00.000Z',
}

const defaultHandlers = [
  http.get(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, () =>
    HttpResponse.json({ success: true, data: OVERRIDE_ROW }),
  ),
]

const server = setupServer(...defaultHandlers)

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

function renderUseProjectTemplateOverride(
  projectId: string | undefined,
  key: string | undefined,
) {
  const queryClient = createQueryClient()
  return renderHook(() => useProjectTemplateOverride(projectId, key), {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  })
}

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})

afterEach(() => {
  server.resetHandlers(...defaultHandlers)
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

afterAll(() => {
  server.close()
  vi.restoreAllMocks()
})

describe('useProjectTemplateOverride hook', () => {
  it('issues GET /api/projects/{id}/templates/{key}/override and returns the override row', async () => {
    const requests: { method: string; url: string }[] = []
    server.use(
      http.get(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, ({ request }) => {
        requests.push({ method: request.method, url: request.url })
        return HttpResponse.json({ success: true, data: OVERRIDE_ROW })
      }),
    )

    const { result } = renderUseProjectTemplateOverride(PROJECT_ID, KEY)

    await waitFor(() => {
      expect(result.current.data).toEqual(OVERRIDE_ROW)
    })

    expect(requests).toHaveLength(1)
    expect(requests[0].method).toBe('GET')
    expect(requests[0].url).toContain(
      `/api/projects/${PROJECT_ID}/templates/${KEY}/override`,
    )
  })

  it('does not retry on 404 and surfaces the error', async () => {
    const calls: string[] = []
    server.resetHandlers(
      http.get(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, ({ request }) => {
        calls.push(request.url)
        return HttpResponse.json(
          { success: false, error: 'No override', code: 'not_found' },
          { status: 404 },
        )
      }),
    )

    const { result } = renderUseProjectTemplateOverride(PROJECT_ID, KEY)

    await waitFor(() => {
      expect(result.current.isError).toBe(true)
    })

    expect(result.current.error).toMatchObject({ status: 404 })
    expect(calls).toHaveLength(1)
  })

  it('does not fetch when either projectId or key is missing', () => {
    let fetchCalled = false
    server.use(
      http.get(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`, () => {
        fetchCalled = true
        return HttpResponse.json({ success: true, data: OVERRIDE_ROW })
      }),
    )

    const { result: resultNoKey } = renderUseProjectTemplateOverride(PROJECT_ID, undefined)
    const { result: resultNoProject } = renderUseProjectTemplateOverride(undefined, KEY)

    expect(resultNoKey.current.isLoading).toBe(false)
    expect(resultNoKey.current.data).toBeUndefined()
    expect(resultNoProject.current.isLoading).toBe(false)
    expect(resultNoProject.current.data).toBeUndefined()
    expect(fetchCalled).toBe(false)
  })

  it('scopes the fetch to the provided projectId and key', async () => {
    const seenUrls: string[] = []
    const customKey = 'custom-key'
    server.resetHandlers(
      http.get(`/api/projects/${PROJECT_ID}/templates/${customKey}/override`, ({ request }) => {
        seenUrls.push(request.url)
        return HttpResponse.json({
          success: true,
          data: { ...OVERRIDE_ROW, key: customKey },
        })
      }),
    )

    const { result } = renderUseProjectTemplateOverride(PROJECT_ID, customKey)

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true)
    })

    expect(seenUrls).toHaveLength(1)
    expect(seenUrls[0]).toContain(`/api/projects/${PROJECT_ID}/templates/${customKey}/override`)
  })
})
