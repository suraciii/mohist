import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'
import { request } from '../../../shared/api/client'
import { getWorkflowProfile } from './client'
import { useWorkflowProfile, type WorkflowProfileFetcher } from './queries'

// react-query resolves via notifyManager's scheduled timers; advance the clock
// ourselves under fake timers instead of polling wall-clock time (waitFor's
// default 1000ms is too tight on slow CI — design/testing.md: advance fake
// time, don't poll harder).
async function flush() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000)
  })
}

interface CapturedRequest {
  path: string
  init?: RequestInit
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
  return renderHook(() => useWorkflowProfile(id, workflowProfileFetcher), {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  })
}

let requests: CapturedRequest[] = []

const requester: typeof request = async <T,>(path: string, init?: RequestInit) => {
  requests.push({ path, init })
  return PROFILE_DETAIL as T
}

const workflowProfileFetcher: WorkflowProfileFetcher = (id) => getWorkflowProfile(id, requester)

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
  requests = []
})

describe('getWorkflowProfile (workflow profile detail URL)', () => {
  it('issues a request to the literal path with the slash left unencoded', async () => {
    const { result } = renderUseWorkflowProfile('mohist/local')

    await flush()
    expect(result.current.isSuccess).toBe(true)

    expect(requests).toHaveLength(1)
    expect(requests[0].init).toBeUndefined()
    expect(requests[0].path).toBe('/workflow-templates/system/mohist/local')
    expect(requests[0].path).not.toContain('%2F')
    expect(requests[0].path).not.toContain('mohist%2Fdefault')
  })

  it('returns the profile detail payload', async () => {
    const { result } = renderUseWorkflowProfile('mohist/local')

    await flush()
    expect(result.current.data).toEqual(PROFILE_DETAIL)

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
