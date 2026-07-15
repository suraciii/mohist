import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import type { WorkflowRunSession } from './types'
import { useWorkflowRunSessions } from './useWorkflowRunSessions'
import { dispatchAgentEvent } from '../../agent/model/events'

let _sessionsData: WorkflowRunSession[] = []
let _sessionsResponses: Array<WorkflowRunSession[] | 'never'> = []

const workflowRunSessionsFetcher = vi.fn((_workflowRunId: string) => {
  const response = _sessionsResponses.length > 0
    ? _sessionsResponses.shift()!
    : _sessionsData
  if (response === 'never') return new Promise<WorkflowRunSession[]>(() => {})
  return Promise.resolve(response)
})

function session(overrides: Partial<WorkflowRunSession>): WorkflowRunSession {
  return {
    id: overrides.id ?? 'session-id',
    workflowRunId: overrides.workflowRunId ?? 'wr-1',
    sessionName: overrides.sessionName ?? 'plan',
    runtimeSessionId: overrides.runtimeSessionId ?? null,
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
    ...(overrides.usage !== undefined ? { usage: overrides.usage } : {}),
    ...(overrides.eventSummary !== undefined ? { eventSummary: overrides.eventSummary } : {}),
  }
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
}

describe('useWorkflowRunSessions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _sessionsData = []
    _sessionsResponses = []
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('does not expose previous workflow run sessions while a new workflow run is loading', async () => {
    _sessionsResponses = [
      [session({ id: 's-wr-1', workflowRunId: 'wr-1', sessionName: 'old-run-session' })],
      'never',
    ]

    const queryClient = createQueryClient()
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    )

    const { result, rerender } = renderHook(
      ({ workflowRunId }: { workflowRunId: string }) => useWorkflowRunSessions(workflowRunId, workflowRunSessionsFetcher),
      { initialProps: { workflowRunId: 'wr-1' }, wrapper },
    )

    await waitFor(() => {
      expect(result.current.sessions.map((s) => s.sessionName)).toEqual(['old-run-session'])
    })

    rerender({ workflowRunId: 'wr-2' })

    expect(result.current.isLoading).toBe(true)
    expect(result.current.sessions).toEqual([])
  })

  describe('event handlers', () => {
    it('usage.updated applies contextUsagePercent and healthStatus to matched session', async () => {
      _sessionsData = [session({ id: 'sess-1', runtimeSessionId: 'acp-1', status: 'running' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      act(() => {
        ;(dispatchAgentEvent as any)('usage.updated', {
          sessionId: 'sess-1',
          runtimeSessionId: 'acp-1',
          contextUsagePercent: 72,
          healthStatus: 'yellow',
        })
      })

      await waitFor(() => {
        const s = result.current.sessions[0]
        expect(s.usage?.contextUsagePercent).toBe(72)
        expect(s.usage?.healthStatus).toBe('yellow')
      })
    })

    it('usage.updated does not trigger a refetch', async () => {
      _sessionsData = [session({ id: 'sess-1', status: 'running' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      const fetchCountBefore = workflowRunSessionsFetcher.mock.calls.length

      act(() => {
        ;(dispatchAgentEvent as any)('usage.updated', {
          sessionId: 'sess-1',
          contextUsagePercent: 72,
          healthStatus: 'yellow',
        })
      })

      await waitFor(() => {
        expect(result.current.sessions[0].usage?.contextUsagePercent).toBe(72)
      })

      expect(workflowRunSessionsFetcher.mock.calls.length).toBe(fetchCountBefore)
    })

    it('context_health_update updates matched session fields', async () => {
      _sessionsData = [session({ id: 'sess-1', runtimeSessionId: 'acp-1', status: 'running' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      act(() => {
        ;(dispatchAgentEvent as any)('context_health_update', {
          sessionId: 'sess-1',
          runtimeSessionId: 'acp-1',
          healthStatus: 'red',
          contextUsagePercent: 91,
          contextWindowUsed: 182000,
          contextWindowSize: 200000,
        })
      })

      await waitFor(() => {
        const s = result.current.sessions[0]
        expect(s.usage?.healthStatus).toBe('red')
        expect(s.usage?.contextUsagePercent).toBe(91)
        expect(s.usage?.contextWindowUsed).toBe(182000)
        expect(s.usage?.contextWindowSize).toBe(200000)
      })
    })

    it('context_health_update updates session matched by runtimeSessionId', async () => {
      _sessionsData = [session({ id: 'sess-1', runtimeSessionId: 'acp-1', status: 'running' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      act(() => {
        ;(dispatchAgentEvent as any)('context_health_update', {
          sessionId: undefined,
          runtimeSessionId: 'acp-1',
          healthStatus: 'yellow',
          contextUsagePercent: 65,
          contextWindowUsed: 130000,
          contextWindowSize: 200000,
        })
      })

      await waitFor(() => {
        const s = result.current.sessions[0]
        expect(s.usage?.healthStatus).toBe('yellow')
        expect(s.usage?.contextUsagePercent).toBe(65)
      })
    })

    it('context_health_update ignores sessions whose identifiers do not match', async () => {
      _sessionsData = [session({
        id: 'sess-1',
        runtimeSessionId: 'acp-1',
        usage: { healthStatus: 'green', contextUsagePercent: 30 },
        status: 'running',
      })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      act(() => {
        ;(dispatchAgentEvent as any)('context_health_update', {
          sessionId: 'sess-unknown',
          runtimeSessionId: 'acp-unknown',
          healthStatus: 'red',
          contextUsagePercent: 95,
          contextWindowUsed: 190000,
          contextWindowSize: 200000,
        })
      })

      await waitFor(() => {
        const s = result.current.sessions[0]
        expect(s.usage?.healthStatus).toBe('green')
        expect(s.usage?.contextUsagePercent).toBe(30)
      })
    })
  })
})
