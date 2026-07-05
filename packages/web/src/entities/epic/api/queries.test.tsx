// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useEpicEvents, usePauseEpic, useReopenEpic, useResumeEpic, useStartEpic, useStartIssue } from './queries'

const mocks = vi.hoisted(() => ({
  getEpicEvents: vi.fn(),
  pauseEpic: vi.fn(),
  reopenEpic: vi.fn(),
  resumeEpic: vi.fn(),
  startEpic: vi.fn(),
  startIssue: vi.fn(),
  useProject: vi.fn(),
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}))

vi.mock('./client', () => ({
  addEpicIssue: vi.fn(),
  closeEpic: vi.fn(),
  createEpic: vi.fn(),
  getEpic: vi.fn(),
  getEpicEvents: mocks.getEpicEvents,
  getEpics: vi.fn(),
  markEpicDone: vi.fn(),
  pauseEpic: mocks.pauseEpic,
  removeEpicIssue: vi.fn(),
  reopenEpic: mocks.reopenEpic,
  resumeEpic: mocks.resumeEpic,
  startEpic: mocks.startEpic,
  updateEpic: vi.fn(),
}))

vi.mock('../../issue', () => ({
  startIssue: mocks.startIssue,
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: mocks.useProject,
}))

vi.mock('sonner', () => ({
  toast: mocks.toast,
}))

function wrapperFor(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  }
}

describe('epic lifecycle query invalidation', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.pauseEpic.mockResolvedValue({ id: 'epic-1', status: 'paused' })
    mocks.reopenEpic.mockResolvedValue({ id: 'epic-1', status: 'idle' })
    mocks.resumeEpic.mockResolvedValue({ id: 'epic-1', status: 'running' })
    mocks.startEpic.mockResolvedValue({ id: 'epic-1', status: 'running' })
    mocks.startIssue.mockResolvedValue({ issue: { number: 7 }, message: 'started' })
  })

  it('invalidates the project-scoped detail query after pausing an epic', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const { result } = renderHook(() => usePauseEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate({ id: 'epic-1', reason: 'wait' })

    await waitFor(() => expect(mocks.pauseEpic).toHaveBeenCalledWith('epic-1', 'wait', 'proj-1'))
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 'epic-1'] })
    })
    expect(invalidateSpy).not.toHaveBeenCalledWith({ queryKey: ['epics', 'epic-1'] })
  })

  it('invalidates the project-scoped detail query after resuming an epic', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const { result } = renderHook(() => useResumeEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate('epic-1')

    await waitFor(() => expect(mocks.resumeEpic).toHaveBeenCalledWith('epic-1', 'proj-1'))
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 'epic-1'] })
    })
    expect(invalidateSpy).not.toHaveBeenCalledWith({ queryKey: ['epics', 'epic-1'] })
  })

  it('starts an issue through the existing issue start path and invalidates epic and issue caches', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const { result } = renderHook(() => useStartIssue(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate(7)

    await waitFor(() => expect(mocks.startIssue).toHaveBeenCalledWith(7, 'proj-1'))
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    })
    expect(mocks.toast.success).toHaveBeenCalledWith('Issue started')
  })

  it('surfaces start failures through toast.error', async () => {
    mocks.startIssue.mockRejectedValueOnce(new Error('Issue is still a draft'))
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const { result } = renderHook(() => useStartIssue(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate(7)

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith('Issue is still a draft'))
  })

  it('calls startEpic(id, projectId) when useStartEpic is mutated', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const { result } = renderHook(() => useStartEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate('epic-1')

    await waitFor(() => expect(mocks.startEpic).toHaveBeenCalledWith('epic-1', 'proj-1'))
  })

  it('invalidates the project-scoped epic detail query after starting an epic', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const { result } = renderHook(() => useStartEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate('epic-1')

    await waitFor(() => expect(mocks.startEpic).toHaveBeenCalledWith('epic-1', 'proj-1'))
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 'epic-1'] })
    })
    expect(invalidateSpy).not.toHaveBeenCalledWith({ queryKey: ['epics', 'epic-1'] })
    expect(mocks.toast.success).toHaveBeenCalledWith('Epic started')
  })

  it('forwards a null projectId when useProject returns none', async () => {
    mocks.useProject.mockReturnValue({ projectId: null })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const { result } = renderHook(() => useStartEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate('epic-1')

    await waitFor(() => expect(mocks.startEpic).toHaveBeenCalledWith('epic-1', null))
  })

  it('surfaces start failures through toast.error when starting an epic', async () => {
    mocks.startEpic.mockRejectedValueOnce(new Error('EPIC_NOT_RUNNING'))
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const { result } = renderHook(() => useStartEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate('epic-1')

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith('EPIC_NOT_RUNNING'))
  })

  it('calls reopenEpic(id, projectId) when useReopenEpic is mutated', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const { result } = renderHook(() => useReopenEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate('epic-1')

    await waitFor(() => expect(mocks.reopenEpic).toHaveBeenCalledWith('epic-1', 'proj-1'))
  })

  it('invalidates epic and issue caches after reopening an epic', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const { result } = renderHook(() => useReopenEpic(), { wrapper: wrapperFor(queryClient) })

    result.current.mutate('epic-1')

    await waitFor(() => expect(mocks.reopenEpic).toHaveBeenCalledWith('epic-1', 'proj-1'))
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['epics', 'proj-1', 'epic-1'] })
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    })
    expect(mocks.toast.success).toHaveBeenCalledWith('Epic reopened')
  })
})

describe('useEpicEvents query', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.getEpicEvents.mockResolvedValue([])
  })

  it('uses queryKey ["epics", projectId, id, "events"] and disables the query when id is empty', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { result } = renderHook(() => useEpicEvents(null), { wrapper: wrapperFor(queryClient) })

    expect(result.current.fetchStatus).toBe('idle')
    expect(mocks.getEpicEvents).not.toHaveBeenCalled()
  })

  it('fetches via getEpicEvents(epicId, projectId) and exposes the resolved data', async () => {
    const events = [
      {
        id: 1,
        eventId: 'evt-1',
        source: '/mohist/epics/epic-1',
        type: 'com.mohist.epic.created',
        specVersion: '1.0',
        subject: '1',
        time: '2026-06-30T12:00:00+00:00',
        dataContentType: 'application/json',
        data: { title: 'Auth epic', description: 'desc', priority: 'p2' },
        extensions: { projectid: 'proj-1', epicid: 'epic-1', epicno: '1' },
      },
    ]
    mocks.getEpicEvents.mockResolvedValue(events)
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { result } = renderHook(() => useEpicEvents('epic-1'), { wrapper: wrapperFor(queryClient) })

    await waitFor(() => expect(result.current.data).toEqual(events))
    expect(mocks.getEpicEvents).toHaveBeenCalledWith('epic-1', 'proj-1')
  })

  it('does not run the query when useProject has no projectId', () => {
    mocks.useProject.mockReturnValue({ projectId: null })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    renderHook(() => useEpicEvents('epic-1'), { wrapper: wrapperFor(queryClient) })

    expect(mocks.getEpicEvents).not.toHaveBeenCalled()
  })

  it('passes enabled=false through to the underlying query', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    renderHook(() => useEpicEvents('epic-1', false), { wrapper: wrapperFor(queryClient) })

    expect(mocks.getEpicEvents).not.toHaveBeenCalled()
  })
})
