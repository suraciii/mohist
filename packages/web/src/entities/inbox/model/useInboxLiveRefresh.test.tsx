// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const useQueryClientMock = vi.fn()
const useEventsConnectionMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-query')>()
  return {
    ...actual,
    useQueryClient: () => useQueryClientMock(),
  }
})

vi.mock('../../../shared/api/events-hub', () => ({
  useEventsConnection: (...args: unknown[]) => useEventsConnectionMock(...args),
}))

import { useInboxLiveRefresh } from './useInboxLiveRefresh'
import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'
import { ProjectProvider } from '../../project/model/ProjectContext'

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

function wrapperFor(queryClient: QueryClient, projectId: string | null = 'proj-1') {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={projectId} initialProjects={[TEST_PROJECT]}>
          {children}
        </ProjectProvider>
      </QueryClientProvider>
    )
  }
}

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('useInboxLiveRefresh', () => {
  it('subscribes via useEventsConnection with the projectId', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient) })

    expect(useEventsConnectionMock).toHaveBeenCalledTimes(1)
    const [passedProjectId, handler, transcriptHandler] = useEventsConnectionMock.mock.calls[0]
    expect(passedProjectId).toBe('proj-1')
    expect(typeof handler).toBe('function')
    expect(transcriptHandler).toBeUndefined()
  })

  it('forwards a null projectId when no project is selected', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient, null) })

    const [passedProjectId] = useEventsConnectionMock.mock.calls[0]
    expect(passedProjectId).toBeNull()
  })

  it('invalidates the inbox query on a com.mohist.workflow.run.failed event', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient) })

    const handler = useEventsConnectionMock.mock.calls[0][1] as (event: string) => void
    handler(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed)

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('invalidates the inbox query on a com.mohist.workflow.stage.approval-requested event', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient) })

    const handler = useEventsConnectionMock.mock.calls[0][1] as (event: string) => void
    handler(REVERSE_DNS_EVENT_TYPES.StageApprovalRequested)

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('invalidates the inbox query on a com.mohist.issue.work-started event', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient) })

    const handler = useEventsConnectionMock.mock.calls[0][1] as (event: string) => void
    handler(REVERSE_DNS_EVENT_TYPES.IssueWorkStarted)

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('invalidates the inbox query on a com.mohist.issue.work-completed event', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient) })

    const handler = useEventsConnectionMock.mock.calls[0][1] as (event: string) => void
    handler(REVERSE_DNS_EVENT_TYPES.IssueWorkCompleted)

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('does NOT invalidate the inbox query for unrelated events', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient) })

    const handler = useEventsConnectionMock.mock.calls[0][1] as (event: string) => void
    handler('com.mohist.issue.created')
    handler('com.mohist.workflow.run.started')
    handler('com.mohist.workflow.stage.started')
    handler('com.mohist.workflow.stage.completed')
    handler('some-other-event')

    expect(invalidateSpy).not.toHaveBeenCalled()
  })

  it('does NOT synthesize or persist items locally — only invalidates the query', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    useQueryClientMock.mockReturnValue(queryClient)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    renderHook(() => useInboxLiveRefresh(), { wrapper: wrapperFor(queryClient) })

    const handler = useEventsConnectionMock.mock.calls[0][1] as (event: string, data?: unknown) => void
    handler(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, { issueId: 'issue-42', projectId: 'proj-1' })

    expect(invalidateSpy).toHaveBeenCalledTimes(1)
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })
})
