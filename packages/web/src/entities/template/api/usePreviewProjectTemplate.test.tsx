import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { usePreviewProjectTemplate, type ProjectTemplatePreviewer } from '..'

// react-query mutations resolve through notifyManager's scheduled timers;
// advance the clock ourselves under fake timers instead of polling wall-clock
// time (waitFor's default 1000ms is too tight on slow CI).
async function flush() {
  await vi.advanceTimersByTimeAsync(1000)
}

const PROJECT_ID = 'test-project'
const KEY = 'proposal'

const VARIABLES = {
  issue: { number: 1, projectId: 'demo-project', title: 'Demo issue' },
  repository: { baseBranch: 'main' },
  workspace: { branch: 'feature/issue-1' },
  vars: {},
}

const PREVIEW_RESPONSE = {
  rendered: 'proposal body for issue 1 in openspec/changes/issue-1',
  missingVariables: ['unknownVar'],
  depth: 1,
}

let previewResponse = PREVIEW_RESPONSE
let previewError: Error | null = null
const previewCalls: Parameters<ProjectTemplatePreviewer>[] = []

const previewer: ProjectTemplatePreviewer = async (...args) => {
  previewCalls.push(args)
  if (previewError) throw previewError
  return previewResponse
}

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
  const result = renderHook(() => usePreviewProjectTemplate(projectId, key, previewer), { wrapper })
  return { queryClient, result, wrapper }
}

beforeEach(() => {
  previewResponse = PREVIEW_RESPONSE
  previewError = null
  previewCalls.length = 0
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('usePreviewProjectTemplate hook', () => {
  it('calls the preview client with project, key, and variables', async () => {
    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    expect(previewCalls).toEqual([[PROJECT_ID, KEY, VARIABLES]])
  })

  it('returns the { rendered, missingVariables, depth } shape on success', async () => {
    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    expect(result.result.current.data).toEqual(PREVIEW_RESPONSE)
    expect(result.result.current.data).toMatchObject({
      rendered: expect.any(String),
      missingVariables: expect.any(Array),
      depth: expect.any(Number),
    })
  })

  it('records empty missingVariables when all references resolve', async () => {
    previewResponse = {
      rendered: 'fully resolved body',
      missingVariables: [],
      depth: 1,
    }

    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
      await flush()
    })

    expect(result.result.current.isSuccess).toBe(true)

    expect(result.result.current.data?.missingVariables).toEqual([])
    expect(result.result.current.data?.rendered).toBe('fully resolved body')
  })

  it('surfaces API errors from the preview endpoint', async () => {
    previewError = new Error('Render failed')

    const { result } = renderUsePreview(PROJECT_ID, KEY)

    await act(async () => {
      result.result.current.mutate({ variables: VARIABLES })
      await flush()
    })

    expect(result.result.current.isError).toBe(true)

    expect(result.result.current.error).toBeInstanceOf(Error)
    expect((result.result.current.error as Error).message).toBe('Render failed')
  })
})
