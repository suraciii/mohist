// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { useMswServer } from '../../../../tests/support/msw'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { useWorkflowProfile } from './queries'

interface CapturedRequest {
  method: string
  url: string
}

const PROFILE_DETAIL = {
  id: 'mohist/local',
  displayName: 'Mohist Local',
  description: 'Default workflow profile',
  isDefault: true,
  yaml: 'stages: []\n',
  stages: [],
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

function renderUseWorkflowProfile(id: string | null) {
  const queryClient = createQueryClient()
  return renderHook(() => useWorkflowProfile(id), {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  })
}

let requests: CapturedRequest[] = []

useMswServer(
  http.get('/api/workflow-templates/system/mohist/local', ({ request }) => {
    requests.push({ method: request.method, url: request.url })
    return HttpResponse.json({ success: true, data: PROFILE_DETAIL })
  }),
)

afterEach(() => {
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
  requests = []
})

describe('getWorkflowProfile (workflow profile detail URL)', () => {
  it('issues a request to the literal path with the slash left unencoded', async () => {
    const { result } = renderUseWorkflowProfile('mohist/local')

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true)
    })

    expect(requests).toHaveLength(1)
    expect(requests[0].method).toBe('GET')
    expect(requests[0].url).toContain('/api/workflow-templates/system/mohist/local')
    expect(requests[0].url).not.toContain('%2F')
    expect(requests[0].url).not.toContain('mohist%2Fdefault')
  })

  it('returns the profile detail payload', async () => {
    const { result } = renderUseWorkflowProfile('mohist/local')

    await waitFor(() => {
      expect(result.current.data).toEqual(PROFILE_DETAIL)
    })

    expect(result.current.data?.id).toBe('mohist/local')
    expect(result.current.data?.yaml).toBe(PROFILE_DETAIL.yaml)
    expect(result.current.data?.stages).toEqual([])
  })

  it('does not fetch when id is null', () => {
    const { result } = renderUseWorkflowProfile(null)

    expect(result.current.isLoading).toBe(false)
    expect(result.current.data).toBeUndefined()
    expect(requests).toHaveLength(0)
  })
})
