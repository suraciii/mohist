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
    // New activity model: sessions carry `activity` (idle/active/unknown).
    // `status` is retained on the wire but is no longer used as a terminal
    // classifier — a session never enters a terminal state.
    activity: rest.activity ?? 'idle',
    status: rest.status,
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
  // Issue 484: sessions never enter a terminal state — execution ending only
  // returns the session to `idle`. The activity model keeps this helper as a
  // hard-coded `false` so any legacy call sites collapse to "still alive".
  it('always returns false regardless of the status/activity value', () => {
    expect(isTerminalSessionStatus('completed')).toBe(false)
    expect(isTerminalSessionStatus('failed')).toBe(false)
    expect(isTerminalSessionStatus('cancelled')).toBe(false)
    expect(isTerminalSessionStatus('running')).toBe(false)
    expect(isTerminalSessionStatus('active')).toBe(false)
    expect(isTerminalSessionStatus('probing')).toBe(false)
    expect(isTerminalSessionStatus('idle')).toBe(false)
    expect(isTerminalSessionStatus('unknown')).toBe(false)
  })
})

describe('computeSessionDurationMs', () => {
  // Issue 484: sessions are never terminal, so the duration is always measured
  // from the session start (startedAt, falling back to createdAt) to the current
  // time. completedAt is no longer used as a measurement boundary.
  it('measures sessions from start to current time (idle activity)', () => {
    const ms = computeSessionDurationMs({
      activity: 'idle',
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T10:01:00.000Z',
      completedAt: '2026-06-15T10:06:00.000Z',
    }, NOW)
    // NOW (12:00) - startedAt (10:01) = 119 min
    expect(ms).toBe(119 * 60_000)
  })

  it('falls back to createdAt when startedAt is null', () => {
    const ms = computeSessionDurationMs({
      activity: 'idle',
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: null,
      completedAt: '2026-06-15T10:10:00.000Z',
    }, NOW)
    // NOW (12:00) - createdAt (10:00) = 120 min
    expect(ms).toBe(120 * 60_000)
  })

  it('measures active sessions from start to current time identically', () => {
    const ms = computeSessionDurationMs({
      activity: 'active',
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T11:30:00.000Z',
      completedAt: null,
    }, NOW)
    expect(ms).toBe(30 * 60_000)
  })

  it('treats unknown activity the same as idle/active (never terminal)', () => {
    const ms = computeSessionDurationMs({
      activity: 'unknown',
      createdAt: '2026-06-15T10:00:00.000Z',
      startedAt: '2026-06-15T11:30:00.000Z',
      completedAt: null,
    }, NOW)
    expect(ms).toBe(30 * 60_000)
  })

  it('returns zero when timestamps cannot be parsed', () => {
    expect(computeSessionDurationMs({
      activity: 'idle',
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
        // Issue 484: execution ended → activity back to `idle`.
        activity: 'idle',
        createdAt: '2026-06-15T08:00:00.000Z',
        startedAt: '2026-06-15T08:01:00.000Z',
        completedAt: '2026-06-15T08:06:00.000Z',
        usage: { totalTokens: 10_000 },
      }),
      session({
        id: 's-build-failed',
        sessionName: 'compile-assets-1',
        stage: 'build',
        // Issue 484: failure that can't be resolved leaves the session in the
        // `unknown` activity (unconfirmable), which the UI treats as failed.
        activity: 'unknown',
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
        // Issue 484: a live execution is `active` (not the legacy `running`).
        activity: 'active',
        createdAt: '2026-06-15T11:00:00.000Z',
        startedAt: '2026-06-15T11:30:00.000Z',
        completedAt: null,
        usage: { inputTokens: 700, outputTokens: 300 },
      }),
      session({
        id: 's-integrate-completed',
        sessionName: 'ship-pr-1',
        stage: 'integrate',
        activity: 'idle',
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

  it('exposes idle/active/unknown in availableStatuses even when no active session exists', () => {
    // Issue 484: filtering is now by `activity` (idle/active/unknown), and the
    // activity catalogue always carries the full idle/active/unknown set so the
    // filter UI stays stable across data refreshes.
    const onlyIdle = buildSessions().filter((s) => s.activity === 'idle' || s.activity === 'unknown')
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(onlyIdle, { nowMs: NOW }),
    )

    expect(result.current.availableStatuses).toEqual(
      expect.arrayContaining(['idle', 'active', 'unknown']),
    )
  })

  it('lists pipeline stages in pipeline order whenever any session maps to them', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    expect(result.current.availableStages).toEqual(['plan', 'build', 'check', 'integrate'])
  })

  it('filters sessions by activity (unknown hides non-unknown sessions)', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('unknown')
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
      result.current.setStatusFilter('idle')
      result.current.setStageFilter('check')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-check-1'])

    act(() => {
      result.current.setStatusFilter('idle')
      result.current.setStageFilter('integrate')
    })

    expect(result.current.sessions.map((s) => s.id)).toEqual(['s-integrate-completed'])

    act(() => {
      result.current.setStatusFilter('unknown')
      result.current.setStageFilter('integrate')
    })

    expect(result.current.sessions).toEqual([])
  })

  it('clearing the status filter keeps the stage filter active and vice versa', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('idle')
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

  it('sorts by duration measured from each session start to the current time (sessions are never terminal)', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setSortKey('duration')
    })

    // Issue 484: a session never reaches a terminal state, so the duration is
    // always measured from the session start (startedAt) to NOW (12:00),
    // regardless of completedAt/activity. Descending by duration:
    //  - check-1:             NOW (12:00) - 08:01 = 239 min
    //  - build-failed:        NOW (12:00) - 09:05 = 175 min
    //  - integrate-completed: NOW (12:00) - 10:01 = 119 min
    //  - plan-running:        NOW (12:00) - 11:30 = 30 min
    expect(result.current.sessions.map((s) => s.id)).toEqual([
      's-check-1',
      's-build-failed',
      's-integrate-completed',
      's-plan-running',
    ])
  })

  it('applies the sort only to the filtered subset', () => {
    const { result } = renderHook(() =>
      useWorkflowSessionFiltering(buildSessions(), { nowMs: NOW }),
    )

    act(() => {
      result.current.setStatusFilter('idle')
      result.current.setSortKey('tokens')
    })

    // Only idle sessions, sorted by tokens desc.
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
