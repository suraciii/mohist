// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { act, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import type { ReactNode } from 'react'
import { usePreviewProjectTemplate } from '..'

const PROJECT_ID = 'test-project'
const KEY = 'proposal'

const VARIABLES = {
  openspecChangeDir: 'openspec/changes/issue-1',
  issue: { number: 1, title: 'Demo issue' },
  project: { id: 'demo-project', name: 'Demo' },
  mohist: { system: 'mohist' },
}

const PREVIEW_RESPONSE = {
  rendered: 'proposal body for issue 1 in openspec/changes/issue-1',
  missingVariables: ['unknownVar'],
  depth: 1,
}

const defaultHandlers = [
  http.post(`/api/projects/${PROJECT_ID}/templates/${KEY}/preview`, () =>
    HttpResponse.json({ success: true, data: PREVIEW_RESPONSE }),
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

function renderUsePreview(projectId: string | undefined, key: string | undefined) {
  const queryClient = createQueryClient()
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
  const result = renderHook(() => usePreviewProjectTemplate(projectId, key), { wrapper })
  return { queryClient, result, wrapper }
}

useMswServer(...defaultHandlers)

afterEach(() => {
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('usePreviewProjectTemplate hook', () => {
  it('issues POST /api/projects/{id}/templates/{key}/preview with the variables payload', async () => {
    const previewRequests: { method: string; url: string; body: unknown }[] = []
    server.resetHandlers(
      http.post(
        `/api/projects/${PROJECT_ID}/templates/${KEY}/preview`,
        async ({ request }) => {
          const body = await request.json()
          previewRequests.push({ method: request.method, url: request.url, body })
          return HttpResponse.json({ success: true, data: PREVIEW_RESPONSE })
        },
      ),
    )

    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
    })

    await waitFor(() => {
      expect(result.result.current.isSuccess).toBe(true)
    })

    expect(previewRequests).toHaveLength(1)
    expect(previewRequests[0].method).toBe('POST')
    expect(previewRequests[0].url).toContain(
      `/api/projects/${PROJECT_ID}/templates/${KEY}/preview`,
    )
    expect(previewRequests[0].body).toEqual({ variables: VARIABLES })
  })

  it('returns the { rendered, missingVariables, depth } shape on success', async () => {
    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
    })

    await waitFor(() => {
      expect(result.result.current.isSuccess).toBe(true)
    })

    expect(result.result.current.data).toEqual(PREVIEW_RESPONSE)
    expect(result.result.current.data).toMatchObject({
      rendered: expect.any(String),
      missingVariables: expect.any(Array),
      depth: expect.any(Number),
    })
  })

  it('records empty missingVariables when all references resolve', async () => {
    server.resetHandlers(
      http.post(
        `/api/projects/${PROJECT_ID}/templates/${KEY}/preview`,
        () =>
          HttpResponse.json({
            success: true,
            data: {
              rendered: 'fully resolved body',
              missingVariables: [],
              depth: 1,
            },
          }),
      ),
    )

    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
    })

    await waitFor(() => {
      expect(result.result.current.isSuccess).toBe(true)
    })

    expect(result.result.current.data?.missingVariables).toEqual([])
    expect(result.result.current.data?.rendered).toBe('fully resolved body')
  })

  it('surfaces API errors from the preview endpoint', async () => {
    server.resetHandlers(
      http.post(`/api/projects/${PROJECT_ID}/templates/${KEY}/preview`, () =>
        HttpResponse.json(
          { success: false, error: 'Render failed', code: 'render_error' },
          { status: 500 },
        ),
      ),
    )

    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
    })

    await waitFor(() => {
      expect(result.result.current.isError).toBe(true)
    })

    expect(result.result.current.error).toBeInstanceOf(Error)
    expect((result.result.current.error as Error).message).toBe('Render failed')
  })
})
