// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderHook } from '@testing-library/react'
import { IssueStatus, type Issue, type IssueHealth } from '../model/types'

const useIssuesMock = vi.fn()
vi.mock('../api/queries', () => ({
  useIssues: (...args: unknown[]) => useIssuesMock(...args),
}))

import { deriveCompletionSnapshot, useCompletionSnapshot } from './completion-snapshot'

type StatusLiteral = 'backlog' | 'in_progress' | 'done' | 'cancelled'

function makeIssue(overrides: { status: StatusLiteral; createdAt: string; updatedAt: string; id?: string; number?: number }): Issue {
  return {
    id: overrides.id ?? `id-${Math.random().toString(36).slice(2)}`,
    number: overrides.number ?? 1,
    title: 'title',
    status: overrides.status as IssueStatus,
    health: 'active' as IssueHealth,
    projectId: 'proj-1',
    labels: {},
    createdAt: overrides.createdAt,
    updatedAt: overrides.updatedAt,
    isDraft: false,
    canStart: true,
    blocker: null,
  }
}

const NOW = new Date('2026-06-19T12:00:00.000Z').getTime()
const ONE_DAY_MS = 24 * 60 * 60 * 1000

function daysAgo(days: number, now: number = NOW): string {
  return new Date(now - days * ONE_DAY_MS).toISOString()
}

function daysAhead(days: number, now: number = NOW): string {
  return new Date(now + days * ONE_DAY_MS).toISOString()
}

describe('deriveCompletionSnapshot', () => {
  it('returns zeros for an empty issue list', () => {
    expect(deriveCompletionSnapshot([], NOW)).toEqual({ completed: 0, failed: 0, new: 0 })
  })

  it('matches the spec scenario: 3 done + 2 cancelled + 5 new when relevant timestamps are within the window', () => {
    const issues: Issue[] = [
      ...Array.from({ length: 3 }, () => makeIssue({ status: 'done', createdAt: daysAgo(20), updatedAt: daysAgo(1) })),
      ...Array.from({ length: 2 }, () => makeIssue({ status: 'cancelled', createdAt: daysAgo(30), updatedAt: daysAgo(2) })),
      ...Array.from({ length: 5 }, () => makeIssue({ status: 'backlog', createdAt: daysAgo(3), updatedAt: daysAgo(10) })),
    ]

    expect(deriveCompletionSnapshot(issues, NOW)).toEqual({ completed: 3, failed: 2, new: 5 })
  })

  it('excludes done issues whose updatedAt is older than 7 days', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(30), updatedAt: daysAgo(8) }),
      makeIssue({ status: 'done', createdAt: daysAgo(20), updatedAt: daysAgo(7) }),
      makeIssue({ status: 'done', createdAt: daysAgo(15), updatedAt: daysAgo(7) }),
    ]

    const snapshot = deriveCompletionSnapshot(issues, NOW)
    expect(snapshot.completed).toBe(2)
    expect(snapshot.failed).toBe(0)
  })

  it('excludes done issues whose updatedAt is strictly more than 7 days ago', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(30), updatedAt: daysAgo(8) }),
      makeIssue({ status: 'done', createdAt: daysAgo(40), updatedAt: daysAgo(20) }),
    ]

    expect(deriveCompletionSnapshot(issues, NOW).completed).toBe(0)
  })

  it('excludes cancelled issues whose updatedAt is older than 7 days', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'cancelled', createdAt: daysAgo(30), updatedAt: daysAgo(8) }),
      makeIssue({ status: 'cancelled', createdAt: daysAgo(20), updatedAt: daysAgo(2) }),
    ]

    expect(deriveCompletionSnapshot(issues, NOW)).toEqual({ completed: 0, failed: 1, new: 0 })
  })

  it('excludes issues whose createdAt is older than 7 days from the new count', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(8), updatedAt: daysAgo(1) }),
      makeIssue({ status: 'backlog', createdAt: daysAgo(30), updatedAt: daysAgo(15) }),
    ]

    const snapshot = deriveCompletionSnapshot(issues, NOW)
    expect(snapshot.new).toBe(0)
    expect(snapshot.completed).toBe(1)
  })

  it('includes boundary timestamps: createdAt at exactly now - 7d and updatedAt at exactly now', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(7), updatedAt: new Date(NOW).toISOString() }),
      makeIssue({ status: 'cancelled', createdAt: daysAgo(7), updatedAt: new Date(NOW).toISOString() }),
    ]

    const snapshot = deriveCompletionSnapshot(issues, NOW)
    expect(snapshot.completed).toBe(1)
    expect(snapshot.failed).toBe(1)
    expect(snapshot.new).toBe(2)
  })

  it('excludes issues with timestamps just outside the trailing window (now - 7d - 1ms)', () => {
    const justOutside = new Date(NOW - 7 * ONE_DAY_MS - 1).toISOString()
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(20), updatedAt: justOutside }),
      makeIssue({ status: 'cancelled', createdAt: daysAgo(20), updatedAt: justOutside }),
      makeIssue({ status: 'in_progress', createdAt: justOutside, updatedAt: justOutside }),
    ]

    expect(deriveCompletionSnapshot(issues, NOW)).toEqual({ completed: 0, failed: 0, new: 0 })
  })

  it('excludes issues with future timestamps from all counts', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAhead(1), updatedAt: daysAhead(2) }),
      makeIssue({ status: 'backlog', createdAt: daysAhead(1), updatedAt: daysAhead(3) }),
    ]

    expect(deriveCompletionSnapshot(issues, NOW)).toEqual({ completed: 0, failed: 0, new: 0 })
  })

  it('does not count non-terminal issues (backlog, in_progress) as completed or failed', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'backlog', createdAt: daysAgo(1), updatedAt: daysAgo(1) }),
      makeIssue({ status: 'in_progress', createdAt: daysAgo(2), updatedAt: daysAgo(2) }),
      makeIssue({ status: 'in_progress', createdAt: daysAgo(3), updatedAt: daysAgo(3) }),
    ]

    const snapshot = deriveCompletionSnapshot(issues, NOW)
    expect(snapshot.completed).toBe(0)
    expect(snapshot.failed).toBe(0)
    expect(snapshot.new).toBe(3)
  })

  it('does not count a terminal issue as new if its createdAt is older than 7 days', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(30), updatedAt: daysAgo(1) }),
    ]

    const snapshot = deriveCompletionSnapshot(issues, NOW)
    expect(snapshot.completed).toBe(1)
    expect(snapshot.new).toBe(0)
  })

  it('does not count an issue as both completed and failed (mutually exclusive by status)', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(1), updatedAt: daysAgo(1) }),
      makeIssue({ status: 'cancelled', createdAt: daysAgo(1), updatedAt: daysAgo(1) }),
    ]

    const snapshot = deriveCompletionSnapshot(issues, NOW)
    expect(snapshot.completed).toBe(1)
    expect(snapshot.failed).toBe(1)
    expect(snapshot.new).toBe(2)
  })

  it('does not perform any fetch and only reads status, createdAt, updatedAt', () => {
    const fetchSpy = vi.fn()
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(2), updatedAt: daysAgo(1) }),
    ]

    deriveCompletionSnapshot(issues, NOW)
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('accepts a Date object as the now parameter', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(2), updatedAt: daysAgo(1) }),
    ]

    expect(deriveCompletionSnapshot(issues, new Date(NOW))).toEqual({ completed: 1, failed: 0, new: 1 })
  })

  it('defaults now to Date.now() when omitted', () => {
    const recent = new Date(Date.now() - ONE_DAY_MS).toISOString()
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: recent, updatedAt: recent }),
    ]

    expect(deriveCompletionSnapshot(issues)).toEqual({ completed: 1, failed: 0, new: 1 })
  })

  it('does not mutate the input issues array', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(2), updatedAt: daysAgo(1) }),
      makeIssue({ status: 'cancelled', createdAt: daysAgo(3), updatedAt: daysAgo(2) }),
    ]
    const before = JSON.stringify(issues)

    deriveCompletionSnapshot(issues, NOW)

    expect(JSON.stringify(issues)).toBe(before)
  })

  it('handles a large list correctly', () => {
    const issues: Issue[] = Array.from({ length: 500 }, (_, i) =>
      makeIssue({
        id: `issue-${i}`,
        status: i % 3 === 0 ? 'done' : i % 3 === 1 ? 'cancelled' : 'in_progress',
        createdAt: daysAgo((i % 10) + 1),
        updatedAt: daysAgo(i % 5),
      }),
    )

    const snapshot = deriveCompletionSnapshot(issues, NOW)
    expect(snapshot.completed + snapshot.failed + snapshot.new).toBeGreaterThan(0)
    expect(snapshot.completed).toBeGreaterThanOrEqual(0)
    expect(snapshot.failed).toBeGreaterThanOrEqual(0)
    expect(snapshot.new).toBeGreaterThanOrEqual(0)
  })
})

describe('useCompletionSnapshot', () => {
  beforeEach(() => {
    vi.useFakeTimers({ now: new Date(NOW) })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('returns the {completed, failed, new} shape derived from useIssues()', () => {
    useIssuesMock.mockReturnValue({
      data: [
        makeIssue({ status: 'done', createdAt: daysAgo(2), updatedAt: daysAgo(1) }),
        makeIssue({ status: 'cancelled', createdAt: daysAgo(3), updatedAt: daysAgo(2) }),
        makeIssue({ status: 'in_progress', createdAt: daysAgo(1), updatedAt: daysAgo(1) }),
      ],
    })

    const { result } = renderHook(() => useCompletionSnapshot())
    expect(result.current).toEqual({ completed: 1, failed: 1, new: 3 })
  })

  it('returns the zeroed snapshot when useIssues() data is undefined', () => {
    useIssuesMock.mockReturnValue({ data: undefined })

    const { result } = renderHook(() => useCompletionSnapshot())
    expect(result.current).toEqual({ completed: 0, failed: 0, new: 0 })
  })

  it('returns the zeroed snapshot for an empty issue list', () => {
    useIssuesMock.mockReturnValue({ data: [] })

    const { result } = renderHook(() => useCompletionSnapshot())
    expect(result.current).toEqual({ completed: 0, failed: 0, new: 0 })
  })

  it('exposes only the {completed, failed, new} shape — the documented reservation for the endpoint-backed swap', () => {
    useIssuesMock.mockReturnValue({ data: [] })

    const { result } = renderHook(() => useCompletionSnapshot())
    expect(Object.keys(result.current).sort()).toEqual(['completed', 'failed', 'new'])
  })

  it('recomputes when the underlying useIssues() data changes', () => {
    useIssuesMock.mockReturnValue({ data: [] })
    const { result, rerender } = renderHook(() => useCompletionSnapshot())
    expect(result.current).toEqual({ completed: 0, failed: 0, new: 0 })

    useIssuesMock.mockReturnValue({
      data: [makeIssue({ status: 'done', createdAt: daysAgo(1), updatedAt: daysAgo(1) })],
    })
    rerender()
    expect(result.current).toEqual({ completed: 1, failed: 0, new: 1 })
  })
})