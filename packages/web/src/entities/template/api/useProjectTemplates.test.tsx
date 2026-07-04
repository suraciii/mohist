// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import type { ReactNode } from 'react'
import { useProjectTemplates } from '..'

const PROJECT_ID = 'test-project'
const OTHER_PROJECT_ID = 'other-project'

const BASE_TEMPLATES = [
  {
    key: 'proposal',
    displayName: 'Generate Proposal',
    description: 'Creates the OpenSpec proposal.md for an issue',
    tags: ['plan', 'openspec'],
    stage: 'plan',
    body: 'system proposal body',
    source: 'system' as const,
  },
  {
    key: 'build',
    displayName: 'Build Task',
    description: 'Implements a single build task',
    tags: ['build'],
    stage: 'build',
    body: 'system build body',
    source: 'system' as const,
  },
]

const defaultHandlers = [
  http.get(`/api/projects/${PROJECT_ID}/templates`, () =>
    HttpResponse.json({ success: true, data: BASE_TEMPLATES }),
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

function renderUseProjectTemplates(projectId: string | undefined) {
  const queryClient = createQueryClient()
  return renderHook(() => useProjectTemplates(projectId), {
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

describe('useProjectTemplates hook', () => {
  it('issues GET /api/projects/{id}/templates and returns the effective templates', async () => {
    const requests: { method: string; url: string }[] = []
    server.use(
      http.get(`/api/projects/${PROJECT_ID}/templates`, ({ request }) => {
        requests.push({ method: request.method, url: request.url })
        return HttpResponse.json({ success: true, data: BASE_TEMPLATES })
      }),
    )

    const { result } = renderUseProjectTemplates(PROJECT_ID)

    await waitFor(() => {
      expect(result.current.data).toEqual(BASE_TEMPLATES)
    })

    expect(requests).toHaveLength(1)
    expect(requests[0].method).toBe('GET')
    expect(requests[0].url).toContain(`/api/projects/${PROJECT_ID}/templates`)
  })

  it('returns templates with the source field preserved', async () => {
    const mixed = [
      { ...BASE_TEMPLATES[0], source: 'project-override' as const },
      { ...BASE_TEMPLATES[1], source: 'system' as const },
      {
        key: 'deploy-checklist',
        displayName: 'Deploy Checklist',
        description: 'project-unique',
        tags: ['deploy'],
        stage: 'check',
        body: 'deploy body',
        source: 'project-new' as const,
      },
    ]
    server.use(
      http.get(`/api/projects/${PROJECT_ID}/templates`, () =>
        HttpResponse.json({ success: true, data: mixed }),
      ),
    )

    const { result } = renderUseProjectTemplates(PROJECT_ID)

    await waitFor(() => {
      expect(result.current.data).toHaveLength(3)
    })

    const byKey = Object.fromEntries(
      result.current.data!.map((t: { key: string; source: string }) => [t.key, t]),
    )
    expect(byKey.proposal!.source).toBe('project-override')
    expect(byKey.build!.source).toBe('system')
    expect(byKey['deploy-checklist']!.source).toBe('project-new')
  })

  it('does not fetch when projectId is undefined', () => {
    let fetchCalled = false
    server.use(
      http.get(`/api/projects/${PROJECT_ID}/templates`, () => {
        fetchCalled = true
        return HttpResponse.json({ success: true, data: [] })
      }),
    )

    const { result } = renderUseProjectTemplates(undefined)

    expect(result.current.isLoading).toBe(false)
    expect(result.current.data).toBeUndefined()
    expect(fetchCalled).toBe(false)
  })

  it('scopes the fetch to the provided projectId', async () => {
    const seenUrls: string[] = []
    server.resetHandlers(
      http.get(`/api/projects/${OTHER_PROJECT_ID}/templates`, ({ request }) => {
        seenUrls.push(request.url)
        return HttpResponse.json({ success: true, data: [] })
      }),
    )

    const { result } = renderUseProjectTemplates(OTHER_PROJECT_ID)

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true)
    })

    expect(seenUrls).toHaveLength(1)
    expect(seenUrls[0]).toContain(`/api/projects/${OTHER_PROJECT_ID}/templates`)
  })
})
