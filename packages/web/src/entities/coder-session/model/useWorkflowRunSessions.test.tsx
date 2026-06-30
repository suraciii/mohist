import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { getWorkflowRunSessions } from '../api/client'
import type { WorkflowRunSession } from './types'
import { useWorkflowRunSessions } from './useWorkflowRunSessions'

vi.mock('../api/client', () => ({
  getWorkflowRunSessions: vi.fn(),
}))

const eventHandlers = new Map<string, ((detail: unknown) => void)[]>()

vi.mock('../../agent/@x/events', () => ({
  onAgentEvent: vi.fn((name: string, handler: (detail: unknown) => void) => {
    if (!eventHandlers.has(name)) eventHandlers.set(name, [])
    eventHandlers.get(name)!.push(handler)
    return () => {
      const handlers = eventHandlers.get(name)
      if (handlers) {
        const idx = handlers.indexOf(handler)
        if (idx !== -1) handlers.splice(idx, 1)
      }
    }
  }),
}))

const mockedGetWorkflowRunSessions = vi.mocked(getWorkflowRunSessions)

function dispatchAgentEvent(name: string, detail: unknown) {
  const handlers = eventHandlers.get(name) ?? []
  for (const handler of handlers) handler(detail)
}

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
    ...(overrides.usage !== undefined ? { usage: overrides.usage } : {}),
    ...(overrides.eventSummary !== undefined ? { eventSummary: overrides.eventSummary } : {}),
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
  beforeEach(() => {
    eventHandlers.clear()
  })

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

  describe('event handlers', () => {
    it('usage.updated applies contextUsagePercent and healthStatus to matched session', async () => {
      mockedGetWorkflowRunSessions.mockResolvedValue([
        session({ id: 'sess-1', acpSessionId: 'acp-1', status: 'running' }),
      ])

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1'),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      dispatchAgentEvent('usage.updated', {
        coderSessionId: 'sess-1',
        acpSessionId: 'acp-1',
        contextUsagePercent: 72,
        healthStatus: 'yellow',
      })

      await waitFor(() => {
        const session = result.current.sessions[0]
        expect(session.usage?.contextUsagePercent).toBe(72)
        expect(session.usage?.healthStatus).toBe('yellow')
      })
    })

    it('usage.updated does not trigger a refetch', async () => {
      mockedGetWorkflowRunSessions.mockResolvedValue([
        session({ id: 'sess-1', status: 'running' }),
      ])

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1'),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      const fetchCountBefore = mockedGetWorkflowRunSessions.mock.calls.length

      dispatchAgentEvent('usage.updated', {
        coderSessionId: 'sess-1',
        contextUsagePercent: 72,
        healthStatus: 'yellow',
      })

      await waitFor(() => {
        expect(result.current.sessions[0].usage?.contextUsagePercent).toBe(72)
      })

      expect(mockedGetWorkflowRunSessions.mock.calls.length).toBe(fetchCountBefore)
    })

    it('context_health_update updates matched session fields', async () => {
      mockedGetWorkflowRunSessions.mockResolvedValue([
        session({ id: 'sess-1', acpSessionId: 'acp-1', status: 'running' }),
      ])

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1'),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      dispatchAgentEvent('context_health_update', {
        coderSessionId: 'sess-1',
        acpSessionId: 'acp-1',
        healthStatus: 'red',
        contextUsagePercent: 91,
        contextWindowUsed: 182000,
        contextWindowSize: 200000,
      })

      await waitFor(() => {
        const session = result.current.sessions[0]
        expect(session.usage?.healthStatus).toBe('red')
        expect(session.usage?.contextUsagePercent).toBe(91)
        expect(session.usage?.contextWindowUsed).toBe(182000)
        expect(session.usage?.contextWindowSize).toBe(200000)
      })
    })

    it('context_health_update updates session matched by acpSessionId', async () => {
      mockedGetWorkflowRunSessions.mockResolvedValue([
        session({ id: 'sess-1', acpSessionId: 'acp-1', status: 'running' }),
      ])

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1'),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      dispatchAgentEvent('context_health_update', {
        coderSessionId: undefined,
        acpSessionId: 'acp-1',
        healthStatus: 'yellow',
        contextUsagePercent: 65,
        contextWindowUsed: 130000,
        contextWindowSize: 200000,
      })

      await waitFor(() => {
        const session = result.current.sessions[0]
        expect(session.usage?.healthStatus).toBe('yellow')
        expect(session.usage?.contextUsagePercent).toBe(65)
      })
    })

    it('context_health_update ignores sessions whose identifiers do not match', async () => {
      mockedGetWorkflowRunSessions.mockResolvedValue([
        session({ id: 'sess-1', acpSessionId: 'acp-1', usage: { healthStatus: 'green', contextUsagePercent: 30 }, status: 'running' }),
      ])

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1'),
        { wrapper },
      )

      await waitFor(() => {
        expect(result.current.sessions.length).toBe(1)
      })

      dispatchAgentEvent('context_health_update', {
        coderSessionId: 'sess-unknown',
        acpSessionId: 'acp-unknown',
        healthStatus: 'red',
        contextUsagePercent: 95,
        contextWindowUsed: 190000,
        contextWindowSize: 200000,
      })

      await waitFor(() => {
        const session = result.current.sessions[0]
        expect(session.usage?.healthStatus).toBe('green')
        expect(session.usage?.contextUsagePercent).toBe(30)
      })
    })
  })
})
