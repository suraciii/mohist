import { afterEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { getWorkflowRunSessions } from '../api/client'
import type { WorkflowRunSession } from './types'
import { useWorkflowRunSessions } from './useWorkflowRunSessions'

vi.mock('../api/client', () => ({
  getWorkflowRunSessions: vi.fn(),
}))

vi.mock('../../agent/@x/events', () => ({
  onAgentEvent: vi.fn(() => vi.fn()),
}))

const mockedGetWorkflowRunSessions = vi.mocked(getWorkflowRunSessions)

function session(overrides: Partial<WorkflowRunSession>): WorkflowRunSession {
  return {
    id: overrides.id ?? 'session-id',
    workflowRunId: overrides.workflowRunId ?? 'wr-1',
    sessionName: overrides.sessionName ?? 'plan',
    acpSessionId: overrides.acpSessionId ?? null,
    projectId: overrides.projectId ?? 'project-1',
    issueNumber: overrides.issueNumber ?? 42,
    runnerId: overrides.runnerId ?? 'runner-1',
    status: overrides.status ?? 'completed',
    stage: overrides.stage ?? 'plan',
    model: overrides.model ?? 'minimax/MiniMax-M3',
    workDir: overrides.workDir ?? null,
    processPid: overrides.processPid ?? null,
    createdAt: overrides.createdAt ?? '2026-06-15T10:00:00.000Z',
    startedAt: overrides.startedAt ?? null,
    completedAt: overrides.completedAt ?? null,
    lastDataAt: overrides.lastDataAt ?? null,
    failureReason: overrides.failureReason ?? null,
    exitCode: overrides.exitCode ?? null,
  }
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
}

function never<T>(): Promise<T> {
  return new Promise(() => {})
}

describe('useWorkflowRunSessions', () => {
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('does not expose previous workflow run sessions while a new workflow run is loading', async () => {
    mockedGetWorkflowRunSessions.mockImplementation((workflowRunId: string) => {
      if (workflowRunId === 'wr-1') {
        return Promise.resolve([
          session({ id: 's-wr-1', workflowRunId: 'wr-1', sessionName: 'old-run-session' }),
        ])
      }
      if (workflowRunId === 'wr-2') {
        return never<WorkflowRunSession[]>()
      }
      return Promise.resolve([])
    })

    const queryClient = createQueryClient()
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    )

    const { result, rerender } = renderHook(
      ({ workflowRunId }: { workflowRunId: string }) => useWorkflowRunSessions(workflowRunId),
      { initialProps: { workflowRunId: 'wr-1' }, wrapper },
    )

    await waitFor(() => {
      expect(result.current.sessions.map((s) => s.sessionName)).toEqual(['old-run-session'])
    })

    rerender({ workflowRunId: 'wr-2' })

    expect(result.current.isLoading).toBe(true)
    expect(result.current.sessions).toEqual([])
  })
})
