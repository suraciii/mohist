import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WorkflowRunSession } from '../../../entities/coder-session'
import {
  computeSessionDurationMs,
  getSessionPipelineStage,
  getSessionTotalTokens,
  isTerminalSessionStatus,
  useWorkflowSessionFiltering,
} from './useWorkflowSessionFiltering'

const NOW = new Date('2026-06-15T12:00:00.000Z').getTime()

function session(overrides: Partial<WorkflowRunSession> & { usage?: Partial<NonNullable<WorkflowRunSession['usage']>> }): WorkflowRunSession {
  const { usage: usageOverride, ...rest } = overrides
  return {
    id: rest.id ?? 'session-1',
    workflowRunId: 'wr-1',
    sessionName: rest.sessionName ?? 'plan',
    runtimeSessionId: rest.runtimeSessionId ?? 'runtime-1',
    projectId: 'project-1',
    issueNumber: 42,
    runnerId: 'runner-1',
    status: rest.status ?? 'completed',
    stage: rest.stage ?? 'plan',
    model: rest.model ?? 'configured/model',
    workDir: null,
    processPid: null,
    createdAt: rest.createdAt ?? '2026-06-15T10:00:00.000Z',
    startedAt: rest.startedAt ?? null,
    completedAt: rest.completedAt ?? null,
    lastDataAt: rest.lastDataAt ?? null,
    failureReason: rest.failureReason ?? null,
    exitCode: null,
    usage: usageOverride ?? undefined,
  }
}

describe('getSessionPipelineStage', () => {
  it('returns the matching pipeline stage from metadata', () => {
    expect(getSessionPipelineStage({ stage: 'plan' })).toBe('plan')
    expect(getSessionPipelineStage({ stage: 'build' })).toBe('build')
    expect(getSessionPipelineStage({ stage: 'check' })).toBe('check')
    expect(getSessionPipelineStage({ stage: 'integrate' })).toBe('integrate')
  })

  it('returns null for missing or unknown stages', () => {
    expect(getSessionPipelineStage({ stage: 'manual-fix' })).toBeNull()
    expect(getSessionPipelineStage({ stage: '' })).toBeNull()
    expect(getSessionPipelineStage({ stage: null })).toBeNull()
  })

  it('matches case-insensitively', () => {
    expect(getSessionPipelineStage({ stage: 'PLAN' })).toBe('plan')
  })
})

describe('getSessionTotalTokens', () => {
  it('uses totalTokens when present', () => {
    expect(getSessionTotalTokens({ usage: { totalTokens: 1234 } })).toBe(1234)
  })

  it('falls back to inputTokens + outputTokens when totalTokens is missing', () => {
    expect(getSessionTotalTokens({ usage: { inputTokens: 100, outputTokens: 250 } })).toBe(350)
  })

  it('returns 0 when usage is absent or both inputs are missing', () => {
    expect(getSessionTotalTokens({ usage: undefined })).toBe(0)
    expect(getSessionTotalTokens({ usage: {} })).toBe(0)
  })
})

describe('isTerminalSessionStatus', () => {
  it('returns true for terminal statuses', () => {
    expect(isTerminalSessionStatus('completed')).toBe(true)
    expect(isTerminalSessionStatus('failed')).toBe(true)
    expect(isTerminalSessionStatus('cancelled')).toBe(true)
  })

  it('returns false for live statuses', () => {
    expect(isTerminalSessionStatus('running')).toBe(false)
    expect(isTerminalSessionStatus('active')).toBe(false)
    expect(isTerminalSessionStatus('probing')).toBe(false)
  })
})

describe('computeSessionDurationMs', () => {
  it('measures completed sessions from start to completion', () => {
    const ms = computeSessionDurationMs({
      status: 'completed',
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T10:01:00.000Z',
      completedAt: '2026-06-15T10:06:00.000Z',
    }, NOW)
    expect(ms).toBe(5 * 60_000)
  })

  it('falls back to createdAt when startedAt is null for completed sessions', () => {
    const ms = computeSessionDurationMs({
      status: 'completed',
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: null,
      completedAt: '2026-06-15T10:10:00.000Z',
    }, NOW)
    expect(ms).toBe(10 * 60_000)
  })

  it('measures live sessions from start to current time', () => {
    const ms = computeSessionDurationMs({
      status: 'running',
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T11:30:00.000Z',
      completedAt: null,
    }, NOW)
    expect(ms).toBe(30 * 60_000)
  })

  it('returns zero when timestamps cannot be parsed', () => {
    expect(computeSessionDurationMs({
      status: 'completed',
      createdAt: 'not-a-date',
      startedAt: null,
      completedAt: null,
    }, NOW)).toBe(0)
  })
})

describe('useWorkflowSessionFiltering', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(NOW)
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  function buildSessions(): WorkflowRunSession[] {
    return [
      session({
        id: 's-check-1',
        sessionName: 'review-repair-1',
        stage: 'check',
        status: 'completed',
        createdAt: '2026-06-15T08:00:00.000Z',
        startedAt: '2026-06-15T08:01:00.000Z',
        completedAt: '2026-06-15T08:06:00.000Z',
        usage: { totalTokens: 10_000 },
      }),
      session({
        id: 's-build-failed',
        sessionName: 'compile-assets-1',
        stage: 'build',
        status: 'failed',
        createdAt: '2026-06-15T09:00:00.000Z',
        startedAt: '2026-06-15T09:05:00.000Z',
        completedAt: '2026-06-15T09:10:00.000Z',
        failureReason: 'probe timed out',
        usage: { totalTokens: 4_000 },
      }),
      session({
        id: 's-plan-running',
        sessionName: 'proposal-draft-1',
        stage: 'plan',
        status: 'running',
        createdAt: '2026-06-15T11:00:00.000Z',
        startedAt: '2026-06-15T11:30:00.000Z',
        completedAt: null,
        usage: { inputTokens: 700, outputTokens: 300 },
      }),
      session({
        id: 's-integrate-completed',
        sessionName: 'ship-pr-1',
        stage: 'integrate',
        status: 'completed',
        createdAt: '2026-06-15T10:00:00.000Z',
        startedAt: '2026-06-15T10:01:00.000Z',
        completedAt: '2026-06-15T10:02:00.000Z',
        usage: { totalTokens: 25_000 },
      }),
    ]
  }

  it('returns sessions sorted by createdAt ascending by default', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    expect(result.current.sessions.map((s) => s.id)).toEqual([
      's-check-1',
      's-build-failed',
      's-integrate-completed',
      's-plan-running',
    ])
    expect(result.current.sortKey).toBe('createdAt')
  })

  it('exposes running/completed/failed in availableStatuses even when no running session exists', () => {
    const onlyCompleted = buildSessions().filter((s) => s.status === 'completed' || s.status === 'failed')
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(onlyCompleted, { nowMs: NOW }),
    )

    expect(result.current.availableStatuses).toEqual(
      expect.arrayContaining(['completed', 'failed', 'running']),
    )
  })

  it('lists pipeline stages in pipeline order whenever any session maps to them', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    expect(result.current.availableStages).toEqual(['plan', 'build', 'check', 'integrate'])
  })

  it('filters sessions by status (failed hides non-failed sessions)', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('failed')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-build-failed'])
  })

  it('filters sessions by stage (build hides non-build sessions)', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStageFilter('build')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-build-failed'])
  })

  it('combines status and stage filters with AND', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('completed')
      result.current.setStageFilter('check')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-check-1'])

    act(() => {
      result.current.setStatusFilter('completed')
      result.current.setStageFilter('integrate')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-integrate-completed'])

    act(() => {
      result.current.setStatusFilter('failed')
      result.current.setStageFilter('integrate')
    })

    expect(result.current.sessions).toEqual([])
  })

  it('clearing the status filter keeps the stage filter active and vice versa', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('completed')
      result.current.setStageFilter('check')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-check-1'])

    act(() => {
      result.current.setStatusFilter(null)
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-check-1'])
    expect(result.current.statusFilter).toBeNull()
    expect(result.current.stageFilter).toBe('check')

    act(() => {
      result.current.setStageFilter(null)
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual([
      's-check-1',
      's-build-failed',
      's-integrate-completed',
      's-plan-running',
    ])
  })

  it('resetFilters clears both filters at once but keeps the sort selection', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('completed')
      result.current.setStageFilter('build')
      result.current.setSortKey('tokens')
    })

    act(() => {
      result.current.resetFilters()
    })

    expect(result.current.statusFilter).toBeNull()
    expect(result.current.stageFilter).toBeNull()
    expect(result.current.sortKey).toBe('tokens')
  })

  it('sorts by tokens using the totalTokens fallback (inputTokens + outputTokens)', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setSortKey('tokens')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual([
      's-integrate-completed', // 25_000
      's-check-1',             // 10_000
      's-build-failed',        // 4_000
      's-plan-running',        // 700 + 300 = 1000
    ])
  })

  it('sorts by duration using completedAt for completed sessions and now for live sessions', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setSortKey('duration')
    })

    // Durations (descending):
    //  - plan-running:        NOW (12:00) - 11:30 = 30 min (live, measured to current time)
    //  - check-1:             08:06 - 08:01        = 5 min  (completed)
    //  - build-failed:        09:10 - 09:05        = 5 min  (completed, ties with check)
    //  - integrate-completed: 10:02 - 10:01        = 1 min  (completed)
    expect(result.current.sessions.map((s) => s.id)).toEqual([
      's-plan-running',
      's-check-1',
      's-build-failed',
      's-integrate-completed',
    ])
  })

  it('applies the sort only to the filtered subset', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('completed')
      result.current.setSortKey('tokens')
    })

    // Only completed sessions, sorted by tokens desc.
    expect(result.current.sessions.map((s) => s.id)).toEqual([
      's-integrate-completed',
      's-check-1',
    ])
  })

  it('tracks the total session count even when filters hide entries', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    expect(result.current.totalCount).toBe(4)

    act(() => {
      result.current.setStageFilter('build')
    })

    expect(result.current.totalCount).toBe(4)
    expect(result.current.sessions).toHaveLength(1)
  })
})
