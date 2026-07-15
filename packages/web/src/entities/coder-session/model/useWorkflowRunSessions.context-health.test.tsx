import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import type { WorkflowRunSession } from './types'
import { useWorkflowRunSessions } from './useWorkflowRunSessions'
import { dispatchAgentEvent } from '../../agent/model/events'

let sessions: WorkflowRunSession[] = []
const fetchSessions = vi.fn(() => Promise.resolve(sessions))

function session(overrides: Partial<WorkflowRunSession>): WorkflowRunSession {
  return {
    id: overrides.id ?? 'sess-1',
    workflowRunId: overrides.workflowRunId ?? 'wr-1',
    sessionName: overrides.sessionName ?? 'plan',
    runtimeSessionId: overrides.runtimeSessionId ?? 'runtime-1',
    projectId: overrides.projectId ?? 'project-1',
    issueNumber: overrides.issueNumber ?? 42,
    runnerId: overrides.runnerId ?? 'runner-1',
    status: overrides.status ?? 'running',
    stage: overrides.stage ?? 'plan',
    model: overrides.model ?? null,
    workDir: overrides.workDir ?? null,
    processPid: overrides.processPid ?? null,
    createdAt: overrides.createdAt ?? '2026-06-15T10:00:00.000Z',
    startedAt: overrides.startedAt ?? null,
    completedAt: overrides.completedAt ?? null,
    lastDataAt: overrides.lastDataAt ?? null,
    failureReason: overrides.failureReason ?? null,
    exitCode: overrides.exitCode ?? null,
    ...(overrides.usage !== undefined ? { usage: overrides.usage } : {}),
  }
}

function renderSessions() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return renderHook(() => useWorkflowRunSessions('wr-1', fetchSessions), {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  })
}

describe('useWorkflowRunSessions context health', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    sessions = []
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('updates a session matched by logical and physical ids', async () => {
    sessions = [session({})]
    const hook = renderSessions()
    await waitFor(() => expect(hook.result.current.sessions).toHaveLength(1))

    act(() => {
      dispatchAgentEvent('context_health_update', {
        sessionId: 'sess-1', runtimeSessionId: 'runtime-1', healthStatus: 'red',
        contextUsagePercent: 91, contextWindowUsed: 182000, contextWindowSize: 200000,
      })
    })

    await waitFor(() => expect(hook.result.current.sessions[0].usage).toMatchObject({
      healthStatus: 'red', contextUsagePercent: 91, contextWindowUsed: 182000, contextWindowSize: 200000,
    }))
  })

  it('updates a session matched only by runtimeSessionId', async () => {
    sessions = [session({})]
    const hook = renderSessions()
    await waitFor(() => expect(hook.result.current.sessions).toHaveLength(1))

    act(() => {
      dispatchAgentEvent('context_health_update', {
        runtimeSessionId: 'runtime-1', healthStatus: 'yellow', contextUsagePercent: 65,
        contextWindowUsed: 130000, contextWindowSize: 200000,
      })
    })

    await waitFor(() => expect(hook.result.current.sessions[0].usage).toMatchObject({
      healthStatus: 'yellow', contextUsagePercent: 65,
    }))
  })

  it('ignores context health updates for another session', async () => {
    sessions = [session({ usage: { healthStatus: 'green', contextUsagePercent: 30 } })]
    const hook = renderSessions()
    await waitFor(() => expect(hook.result.current.sessions).toHaveLength(1))

    act(() => {
      dispatchAgentEvent('context_health_update', {
        sessionId: 'sess-other', runtimeSessionId: 'runtime-other', healthStatus: 'red',
        contextUsagePercent: 95, contextWindowUsed: 190000, contextWindowSize: 200000,
      })
    })

    await waitFor(() => expect(hook.result.current.sessions[0].usage).toMatchObject({
      healthStatus: 'green', contextUsagePercent: 30,
    }))
  })
})
