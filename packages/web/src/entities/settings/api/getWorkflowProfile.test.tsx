import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'
import { request } from '../../../shared/api/client'
import { ProjectProvider } from '../../project'
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
  projectId: 'proj-1',
  profileId: 'mohist/local',
  name: 'Mohist Local',
  description: 'Default workflow profile',
  sourceProvenance: 'BuiltIn',
  isBuiltIn: true,
  definitionSource: 'stages:\n  - stage: build\n',
  agentAction: 'mohist/opencode',
  agentRuntime: 'opencode',
  stages: [{ stage: 'build', requiresApproval: false, tasks: ['run'], checks: [] }],
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
      <QueryClientProvider client={queryClient}>
        <ProjectProvider
          initialProjectId="proj-1"
          initialProjects={[{ id: 'proj-1', name: 'Project', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}
        >
          {children}
        </ProjectProvider>
      </QueryClientProvider>
    ),
  })
}

let requests: CapturedRequest[] = []

const requester: typeof request = async <T,>(path: string, init?: RequestInit) => {
  requests.push({ path, init })
  return PROFILE_DETAIL as T
}

const workflowProfileFetcher: WorkflowProfileFetcher = (projectId, id) => getWorkflowProfile(projectId, id, requester)

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
    expect(requests[0].path).toBe('/projects/proj-1/workflow-profiles/mohist/local')
    expect(requests[0].path).not.toContain('%2F')
    expect(requests[0].path).not.toContain('mohist%2Fdefault')
  })

  it('returns the profile detail payload', async () => {
    const { result } = renderUseWorkflowProfile('mohist/local')

    await flush()
    expect(result.current.data).toEqual(expect.objectContaining({
      id: 'mohist/local',
      displayName: 'Mohist Local',
      description: PROFILE_DETAIL.description,
      isDefault: true,
      agentAction: 'mohist/opencode',
      agentRuntime: 'opencode',
      definitionSource: PROFILE_DETAIL.definitionSource,
    }))

    expect(result.current.data?.id).toBe('mohist/local')
    expect(result.current.data?.yaml).toBe(PROFILE_DETAIL.definitionSource)
    expect(result.current.data?.stages).toEqual(PROFILE_DETAIL.stages)
  })

  it('does not fetch when id is null', () => {
    const { result } = renderUseWorkflowProfile(null)

    expect(result.current.isLoading).toBe(false)
    expect(result.current.data).toBeUndefined()
    expect(requests).toHaveLength(0)
  })
})
