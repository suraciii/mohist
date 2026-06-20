// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { usePauseEpic, useResumeEpic } from './queries'

const mocks = vi.hoisted(() => ({
  pauseEpic: vi.fn(),
  resumeEpic: vi.fn(),
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
  getEpics: vi.fn(),
  markEpicDone: vi.fn(),
  pauseEpic: mocks.pauseEpic,
  removeEpicIssue: vi.fn(),
  resumeEpic: mocks.resumeEpic,
  updateEpic: vi.fn(),
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
    mocks.resumeEpic.mockResolvedValue({ id: 'epic-1', status: 'active' })
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
})
