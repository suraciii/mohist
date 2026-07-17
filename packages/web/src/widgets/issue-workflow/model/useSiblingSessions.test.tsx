import { beforeEach, describe, expect, it } from 'vitest'
import { renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import type { WorkflowRunSession } from '../../../entities/coder-session'
import { useSiblingSessions } from './useSiblingSessions'

let sessionsData: WorkflowRunSession[] = []

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
  }
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
}

function renderSiblingHook(workflowRunId: string | null | undefined, currentKey?: string | null) {
  const queryClient = createQueryClient()
  if (workflowRunId) {
    queryClient.setQueryData(['workflow-runs', workflowRunId, 'sessions'], sessionsData)
  }
  const hook = renderHook(
    () => useSiblingSessions(workflowRunId, { currentKey: currentKey ?? null }),
    {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      ),
    },
  )
  return { ...hook, queryClient }
}

describe('useSiblingSessions', () => {
  beforeEach(() => {
    sessionsData = []
  })

  it('returns an empty sibling set when the workflow run has no sessions', () => {
    const { result } = renderSiblingHook('wr-empty')

    expect(result.current.sessions).toEqual([])
    expect(result.current.currentIndex).toBe(-1)
    expect(result.current.previous).toBeNull()
    expect(result.current.next).toBeNull()
    expect(result.current.hasPrevious).toBe(false)
    expect(result.current.hasNext).toBe(false)
  })

  it('sorts siblings by createdAt ascending and exposes them in the canonical order', () => {
    sessionsData = [
        session({ id: 's-build', sessionName: 'build', createdAt: '2026-06-15T12:00:00.000Z' }),
        session({ id: 's-plan', sessionName: 'plan', createdAt: '2026-06-15T08:00:00.000Z' }),
        session({ id: 's-check', sessionName: 'check', createdAt: '2026-06-15T10:00:00.000Z' }),
      ]

    const { result } = renderSiblingHook('wr-1', 'check')

    expect(result.current.sessions.map((s) => s.sessionName)).toEqual(['plan', 'check', 'build'])
    expect(result.current.currentIndex).toBe(1)
  })

  it('falls back to sessionName alphabetical order when two sessions share a createdAt', () => {
    sessionsData = [
        session({ id: 's-zeta', sessionName: 'zeta', createdAt: '2026-06-15T10:00:00.000Z' }),
        session({ id: 's-alpha', sessionName: 'alpha', createdAt: '2026-06-15T10:00:00.000Z' }),
      ]

    const { result } = renderSiblingHook('wr-1', 'alpha')

    expect(result.current.sessions.map((s) => s.sessionName)).toEqual(['alpha', 'zeta'])
  })

  it('locates the current session by sessionName and exposes previous + next siblings', () => {
    sessionsData = [
        session({ id: 's-plan', sessionName: 'plan', createdAt: '2026-06-15T08:00:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', createdAt: '2026-06-15T10:00:00.000Z' }),
        session({ id: 's-check', sessionName: 'check', createdAt: '2026-06-15T12:00:00.000Z' }),
      ]

    const { result } = renderSiblingHook('wr-1', 'build')

    expect(result.current.currentIndex).toBe(1)
    expect(result.current.previous?.sessionName).toBe('plan')
    expect(result.current.previous?.id).toBe('s-plan')
    expect(result.current.next?.sessionName).toBe('check')
    expect(result.current.next?.id).toBe('s-check')
    expect(result.current.hasPrevious).toBe(true)
    expect(result.current.hasNext).toBe(true)
  })

  it('returns no previous sibling when the current session is the first in createdAt order', () => {
    sessionsData = [
        session({ id: 's-plan', sessionName: 'plan', createdAt: '2026-06-15T08:00:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', createdAt: '2026-06-15T10:00:00.000Z' }),
        session({ id: 's-check', sessionName: 'check', createdAt: '2026-06-15T12:00:00.000Z' }),
      ]

    const { result } = renderSiblingHook('wr-1', 'plan')

    expect(result.current.currentIndex).toBe(0)
    expect(result.current.previous).toBeNull()
    expect(result.current.next?.sessionName).toBe('build')
    expect(result.current.hasPrevious).toBe(false)
    expect(result.current.hasNext).toBe(true)
  })

  it('returns no next sibling when the current session is the last in createdAt order', () => {
    sessionsData = [
        session({ id: 's-plan', sessionName: 'plan', createdAt: '2026-06-15T08:00:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', createdAt: '2026-06-15T10:00:00.000Z' }),
        session({ id: 's-check', sessionName: 'check', createdAt: '2026-06-15T12:00:00.000Z' }),
      ]

    const { result } = renderSiblingHook('wr-1', 'check')

    expect(result.current.currentIndex).toBe(2)
    expect(result.current.previous?.sessionName).toBe('build')
    expect(result.current.next).toBeNull()
    expect(result.current.hasPrevious).toBe(true)
    expect(result.current.hasNext).toBe(false)
  })

  it('exposes no previous or next when only one session is in the workflow run', () => {
    sessionsData = [session({ id: 's-only', sessionName: 'only', createdAt: '2026-06-15T10:00:00.000Z' })]

    const { result } = renderSiblingHook('wr-1', 'only')

    expect(result.current.currentIndex).toBe(0)
    expect(result.current.previous).toBeNull()
    expect(result.current.next).toBeNull()
    expect(result.current.hasPrevious).toBe(false)
    expect(result.current.hasNext).toBe(false)
  })

  it('falls back to id-based lookup when sessionName does not match the current key', () => {
    sessionsData = [
        session({ id: 's-plan', sessionName: 'plan', createdAt: '2026-06-15T08:00:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', createdAt: '2026-06-15T10:00:00.000Z' }),
      ]

    const { result } = renderSiblingHook('wr-1', 's-plan')

    expect(result.current.currentIndex).toBe(0)
    expect(result.current.previous).toBeNull()
    expect(result.current.next?.sessionName).toBe('build')
  })

  it('returns currentIndex = -1 when the current key matches no sibling', () => {
    sessionsData = [
        session({ id: 's-plan', sessionName: 'plan', createdAt: '2026-06-15T08:00:00.000Z' }),
        session({ id: 's-build', sessionName: 'build', createdAt: '2026-06-15T10:00:00.000Z' }),
      ]

    const { result } = renderSiblingHook('wr-1', 'not-in-this-run')

    expect(result.current.currentIndex).toBe(-1)
    expect(result.current.previous).toBeNull()
    expect(result.current.next).toBeNull()
    expect(result.current.hasPrevious).toBe(false)
    expect(result.current.hasNext).toBe(false)
  })

  it('treats null workflowRunId as an empty sibling set without fetching data', () => {
    const { result, queryClient } = renderSiblingHook(null, 'plan')

    expect(result.current.sessions).toEqual([])
    expect(result.current.currentIndex).toBe(-1)
    expect(queryClient.getQueryState(['workflow-runs', null, 'sessions'])?.fetchStatus).toBe('idle')
  })

  it('uses the workflowRunId-specific session collection', () => {
    sessionsData = [session({ id: 'specific-session', workflowRunId: 'wr-specific-123' })]

    const { result } = renderSiblingHook('wr-specific-123', 'plan')

    expect(result.current.sessions.map((item) => item.id)).toEqual(['specific-session'])
  })
})
