import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'
import type { WorkflowRunSession } from './types'
import { useWorkflowRunSessions } from './useWorkflowRunSessions'
import { dispatchAgentEvent } from '../../agent/model/events'

// Advance react-query's notifyManager timers under fake timers instead of
// polling wall-clock (design/testing.md: advance fake time, don't poll harder).
async function flush() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000)
  })
}

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
    runtime: overrides.runtime ?? 'opencode',
    projectId: overrides.projectId ?? 'project-1',
    issueNumber: overrides.issueNumber ?? 42,
    runnerId: overrides.runnerId ?? 'runner-1',
    // Issue 484: sessions carry an `activity` (idle/active/unknown) instead
    // of a `status`. The legacy `status` field is still present on the DTO
    // (some event payloads and fixtures set it) but the hook no longer reads
    // it for live patching — activity is the source of truth. Default to
    // 'idle' (a finished/awaiting-followup session).
    activity: overrides.activity ?? 'idle',
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
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
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

    await flush()
    expect(result.current.sessions.map((s) => s.sessionName)).toEqual(['old-run-session'])

    rerender({ workflowRunId: 'wr-2' })

    expect(result.current.isLoading).toBe(true)
    expect(result.current.sessions).toEqual([])
  })

  describe('event handlers', () => {
    it('usage.updated applies contextUsagePercent and healthStatus to matched session', async () => {
      _sessionsData = [session({ id: 'sess-1', runtimeSessionId: 'runtime-1', activity: 'active' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions.length).toBe(1)

      act(() => {
        ;(dispatchAgentEvent as any)('usage.updated', {
          sessionId: 'sess-1',
          runtimeSessionId: 'runtime-1',
          runtime: 'opencode',
          contextUsagePercent: 72,
          healthStatus: 'yellow',
        })
      })

      await flush()
      const s = result.current.sessions[0]
      expect(s.usage?.contextUsagePercent).toBe(72)
      expect(s.usage?.healthStatus).toBe('yellow')
    })

    it('refetches sessions when a runtime binding changes', async () => {
      _sessionsResponses = [
        [session({ id: 'sess-1', runtimeSessionId: 'runtime-old' })],
        [session({ id: 'sess-1', runtimeSessionId: 'runtime-new' })],
      ]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )
      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions[0]?.runtimeSessionId).toBe('runtime-old')

      act(() => {
        dispatchAgentEvent('com.mohist.agent-session.runtime-bound', {
          issueNumber: 1,
          projectId: 'project-1',
        })
      })

      await flush()
      expect(workflowRunSessionsFetcher).toHaveBeenCalledTimes(2)
      expect(result.current.sessions[0]?.runtimeSessionId).toBe('runtime-new')
    })

    // Issue 484 / D6: `session.followup_completed` and `session.followup_failed`
    // are deprecated — the hook no longer subscribes to them, so dispatching
    // them must NOT refetch the sessions list nor patch any session field.
    // (Follow-up status now flows through `coder_session_status_changed` /
    // `session.activity` instead.) This replaces the former assertions that
    // the hook refetched on these events without applying the status globally.
    it.each([
      'session.followup_completed',
      'session.followup_failed',
    ] as const)('ignores deprecated %s event (no refetch, no activity patch)', async (eventName) => {
      _sessionsData = [session({ id: 'sess-1', runtimeSessionId: 'runtime-1', activity: 'active' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )
      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions[0]?.activity).toBe('active')
      const fetchCountBefore = workflowRunSessionsFetcher.mock.calls.length

      act(() => {
        // The deprecated follow-up events carry a typed `status` on the wire,
        // but the hook no longer subscribes — cast the payload so the union
        // event name doesn't force an impossible intersected status type.
        ;(dispatchAgentEvent as any)(eventName, {
          sessionId: 'sess-1',
          runtimeSessionId: 'runtime-1',
          runtime: 'opencode',
          operationId: 'operation-1',
        })
      })

      await flush()
      // Activity is unchanged and no refetch was triggered.
      expect(result.current.sessions[0]?.activity).toBe('active')
      expect(workflowRunSessionsFetcher.mock.calls.length).toBe(fetchCountBefore)
    })

    it('ignores runtime events without a physical binding', async () => {
      _sessionsData = [session({ id: 'sess-1', activity: 'active' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )

      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions.length).toBe(1)

      const fetchCountBefore = workflowRunSessionsFetcher.mock.calls.length

      act(() => {
        ;(dispatchAgentEvent as any)('usage.updated', {
          sessionId: 'sess-1',
          runtime: 'opencode',
          contextUsagePercent: 72,
          healthStatus: 'yellow',
        })
      })

      await flush()
      expect(result.current.sessions[0].usage?.contextUsagePercent).toBeUndefined()

      expect(workflowRunSessionsFetcher.mock.calls.length).toBe(fetchCountBefore)
    })

    // Issue 484: `session.closed` is deprecated and no longer handled. The
    // equivalent "session reached idle after finishing" signal now arrives
    // via `coder_session_status_changed`, which patches `activity` (sessions
    // never enter a terminal status — finishing brings activity back to
    // idle). A status of 'completed' maps to activity 'idle'.
    it('updates activity to idle from a current runtime session status-changed event', async () => {
      _sessionsData = [session({ id: 'sess-1', runtimeSessionId: 'runtime-1', activity: 'active' })]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )
      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions).toHaveLength(1)
      act(() => {
        dispatchAgentEvent('coder_session_status_changed', {
          sessionId: 'sess-1',
          runtimeSessionId: 'runtime-1',
          runtime: 'opencode',
          issueNumber: 42,
          projectId: 'project-1',
          status: 'completed',
        })
      })

      expect(result.current.sessions[0].activity).toBe('idle')
    })

    it('ignores a stale status-changed event for a different runtime binding', async () => {
      _sessionsData = [session({
        id: 'sess-1',
        runtimeSessionId: 'runtime-current',
        activity: 'active',
      })]
      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )
      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions).toHaveLength(1)
      act(() => {
        dispatchAgentEvent('coder_session_status_changed', {
          sessionId: 'sess-1',
          runtimeSessionId: 'runtime-old',
          runtime: 'opencode',
          issueNumber: 42,
          projectId: 'project-1',
          status: 'completed',
        })
      })

      expect(result.current.sessions[0].activity).toBe('active')
    })

    it('ignores a stale physical runtime event for the current logical session', async () => {
      _sessionsData = [session({
        id: 'sess-1',
        runtimeSessionId: 'runtime-current',
        activity: 'active',
        usage: { contextUsagePercent: 30 },
      })]
      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )
      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions.length).toBe(1)
      act(() => {
        ;(dispatchAgentEvent as any)('usage.updated', {
          sessionId: 'sess-1',
          runtimeSessionId: 'runtime-old',
          runtime: 'opencode',
          contextUsagePercent: 99,
        })
      })

      expect(result.current.sessions[0].usage?.contextUsagePercent).toBe(30)
    })

    it('updates only the session with the matching logical id and runtime binding', async () => {
      _sessionsData = [
        session({ id: 'sess-opencode', runtime: 'opencode', runtimeSessionId: 'shared-runtime-id', usage: { contextUsagePercent: 20 } }),
        session({ id: 'sess-other', runtime: 'other-runtime', runtimeSessionId: 'shared-runtime-id', usage: { contextUsagePercent: 30 } }),
      ]

      const queryClient = createQueryClient()
      const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      )
      const { result } = renderHook(
        () => useWorkflowRunSessions('wr-1', workflowRunSessionsFetcher),
        { wrapper },
      )

      await flush()
      expect(result.current.sessions).toHaveLength(2)
      act(() => {
        ;(dispatchAgentEvent as any)('usage.updated', {
          sessionId: 'sess-other',
          runtime: 'other-runtime',
          runtimeSessionId: 'shared-runtime-id',
          contextUsagePercent: 75,
        })
      })

      expect(result.current.sessions[0].usage?.contextUsagePercent).toBe(20)
      expect(result.current.sessions[1].usage?.contextUsagePercent).toBe(75)
    })

  })
})
