// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import { renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { IssueStatus, IssueHealth, type Issue } from '../model/types'

const useIssuesMock = vi.fn()
const useArchivedIssuesMock = vi.fn()
const useProjectMock = vi.fn()

vi.mock('../api/queries', () => ({
  useIssues: (...args: unknown[]) => useIssuesMock(...args),
  useArchivedIssues: (...args: unknown[]) => useArchivedIssuesMock(...args),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

import { DIGEST_TOP_N, deriveRecentDigest, useRecentDigest } from './recent-digest'

function makeIssue(overrides: {
  status: 'backlog' | 'in_progress' | 'done' | 'cancelled'
  createdAt: string
  updatedAt: string
  archivedAt?: string
  id?: string
  number?: number
  title?: string
}): Issue {
  return {
    id: overrides.id ?? `id-${Math.random().toString(36).slice(2)}`,
    number: overrides.number ?? 1,
    title: overrides.title ?? 'title',
    status: overrides.status as IssueStatus,
    health: 'active' as IssueHealth,
    projectId: 'proj-1',
    labels: {},
    createdAt: overrides.createdAt,
    updatedAt: overrides.updatedAt,
    archivedAt: overrides.archivedAt,
    isDraft: false,
    canStart: true,
    blocker: null,
  }
}

function makeWrapper() {
  const queryClient = new QueryClient()
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  )
}

const NOW = new Date('2026-06-19T12:00:00.000Z').getTime()
const ONE_DAY_MS = 24 * 60 * 60 * 1000

function hoursAgo(hours: number, now: number = NOW): string {
  return new Date(now - hours * 60 * 60 * 1000).toISOString()
}

function daysAgo(days: number, now: number = NOW): string {
  return new Date(now - days * ONE_DAY_MS).toISOString()
}

describe('deriveRecentDigest', () => {
  it('returns three empty arrays for empty inputs', () => {
    expect(deriveRecentDigest([], [])).toEqual({ completed: [], failed: [], archived: [] })
  })

  it('categorizes active issues into completed (status done) and failed (status cancelled)', () => {
    const completed = makeIssue({
      status: 'done',
      createdAt: daysAgo(2),
      updatedAt: daysAgo(1),
      number: 1,
    })
    const failed = makeIssue({
      status: 'cancelled',
      createdAt: daysAgo(3),
      updatedAt: daysAgo(2),
      number: 2,
    })

    const result = deriveRecentDigest([completed, failed], [])

    expect(result.completed.map((i) => i.number)).toEqual([1])
    expect(result.failed.map((i) => i.number)).toEqual([2])
    expect(result.archived).toEqual([])
  })

  it('does not put backlog or in_progress issues into completed or failed', () => {
    const backlog = makeIssue({
      status: 'backlog',
      createdAt: daysAgo(1),
      updatedAt: daysAgo(1),
      number: 10,
    })
    const inProgress = makeIssue({
      status: 'in_progress',
      createdAt: daysAgo(1),
      updatedAt: daysAgo(1),
      number: 11,
    })

    const result = deriveRecentDigest([backlog, inProgress], [])

    expect(result.completed).toEqual([])
    expect(result.failed).toEqual([])
  })

  it('orders completed by updatedAt desc (most recent first)', () => {
    const oldest = makeIssue({ status: 'done', createdAt: daysAgo(5), updatedAt: daysAgo(4), number: 1 })
    const middle = makeIssue({ status: 'done', createdAt: daysAgo(3), updatedAt: daysAgo(2), number: 2 })
    const newest = makeIssue({ status: 'done', createdAt: daysAgo(2), updatedAt: daysAgo(1), number: 3 })

    const result = deriveRecentDigest([oldest, newest, middle], [])

    expect(result.completed.map((i) => i.number)).toEqual([3, 2, 1])
  })

  it('orders failed by updatedAt desc (most recent first)', () => {
    const oldest = makeIssue({
      status: 'cancelled',
      createdAt: daysAgo(5),
      updatedAt: daysAgo(4),
      number: 1,
    })
    const middle = makeIssue({
      status: 'cancelled',
      createdAt: daysAgo(3),
      updatedAt: daysAgo(2),
      number: 2,
    })
    const newest = makeIssue({
      status: 'cancelled',
      createdAt: daysAgo(2),
      updatedAt: daysAgo(1),
      number: 3,
    })

    const result = deriveRecentDigest([oldest, newest, middle], [])

    expect(result.failed.map((i) => i.number)).toEqual([3, 2, 1])
  })

  it('caps each category at DIGEST_TOP_N (5) most recent rows', () => {
    const issues: Issue[] = Array.from({ length: 8 }, (_, i) =>
      makeIssue({
        status: 'done',
        createdAt: daysAgo(10),
        updatedAt: hoursAgo(8 - i),
        number: 100 + i,
      }),
    )

    const result = deriveRecentDigest(issues, [])

    expect(result.completed).toHaveLength(DIGEST_TOP_N)
    expect(result.completed[0].number).toBe(107)
    expect(result.completed[DIGEST_TOP_N - 1].number).toBe(103)
  })

  it('allows configuring topN via options', () => {
    const issues: Issue[] = Array.from({ length: 6 }, (_, i) =>
      makeIssue({
        status: 'done',
        createdAt: daysAgo(10),
        updatedAt: hoursAgo(6 - i),
        number: 200 + i,
      }),
    )

    const result = deriveRecentDigest(issues, [], { topN: 3 })

    expect(result.completed).toHaveLength(3)
    expect(result.completed.map((i) => i.number)).toEqual([205, 204, 203])
  })

  it('orders archived issues by archivedAt desc (most recent first)', () => {
    const oldest = makeIssue({
      status: 'done',
      createdAt: daysAgo(10),
      updatedAt: daysAgo(9),
      archivedAt: daysAgo(5),
      number: 1,
    })
    const middle = makeIssue({
      status: 'done',
      createdAt: daysAgo(10),
      updatedAt: daysAgo(8),
      archivedAt: daysAgo(3),
      number: 2,
    })
    const newest = makeIssue({
      status: 'done',
      createdAt: daysAgo(10),
      updatedAt: daysAgo(7),
      archivedAt: daysAgo(1),
      number: 3,
    })

    const result = deriveRecentDigest([], [oldest, newest, middle])

    expect(result.archived.map((i) => i.number)).toEqual([3, 2, 1])
  })

  it('caps archived at DIGEST_TOP_N (5) most recent rows', () => {
    const archived: Issue[] = Array.from({ length: 8 }, (_, i) =>
      makeIssue({
        status: 'done',
        createdAt: daysAgo(20),
        updatedAt: daysAgo(15),
        archivedAt: daysAgo(8 - i),
        number: 300 + i,
      }),
    )

    const result = deriveRecentDigest([], archived)

    expect(result.archived).toHaveLength(DIGEST_TOP_N)
    expect(result.archived[0].number).toBe(307)
    expect(result.archived[DIGEST_TOP_N - 1].number).toBe(303)
  })

  it('excludes active issues that have an archivedAt from completed and failed', () => {
    const archived = makeIssue({
      status: 'done',
      createdAt: daysAgo(10),
      updatedAt: daysAgo(1),
      archivedAt: daysAgo(1),
      number: 50,
    })

    const result = deriveRecentDigest([archived], [])

    expect(result.completed).toEqual([])
    expect(result.failed).toEqual([])
  })

  it('aligns completed/failed taxonomy with completion-snapshot (done => completed, cancelled => failed)', () => {
    const done = makeIssue({ status: 'done', createdAt: daysAgo(1), updatedAt: daysAgo(1), number: 1 })
    const cancelled = makeIssue({
      status: 'cancelled',
      createdAt: daysAgo(1),
      updatedAt: daysAgo(1),
      number: 2,
    })

    const result = deriveRecentDigest([done, cancelled], [])

    expect(result.completed.map((i) => i.status)).toEqual(['done'])
    expect(result.failed.map((i) => i.status)).toEqual(['cancelled'])
  })

  it('handles a large mixed list correctly', () => {
    const issues: Issue[] = []
    for (let i = 0; i < 50; i++) {
      issues.push(
        makeIssue({
          status: i % 3 === 0 ? 'done' : i % 3 === 1 ? 'cancelled' : 'backlog',
          createdAt: daysAgo((i % 10) + 1),
          updatedAt: hoursAgo(i),
          number: i + 1,
        }),
      )
    }
    const archived: Issue[] = Array.from({ length: 12 }, (_, i) =>
      makeIssue({
        status: 'done',
        createdAt: daysAgo(20),
        updatedAt: daysAgo(15),
        archivedAt: hoursAgo(i),
        number: 1000 + i,
      }),
    )

    const result = deriveRecentDigest(issues, archived)

    expect(result.completed.length).toBeLessThanOrEqual(DIGEST_TOP_N)
    expect(result.failed.length).toBeLessThanOrEqual(DIGEST_TOP_N)
    expect(result.archived.length).toBeLessThanOrEqual(DIGEST_TOP_N)

    expect(result.completed[0].number).toBeLessThan(result.completed[result.completed.length - 1].number)
    expect(result.failed[0].number).toBeLessThan(result.failed[result.failed.length - 1].number)
    expect(result.archived[0].number).toBeLessThan(result.archived[result.archived.length - 1].number)
  })

  it('does not mutate the input arrays', () => {
    const issues: Issue[] = [
      makeIssue({ status: 'done', createdAt: daysAgo(2), updatedAt: daysAgo(1), number: 1 }),
      makeIssue({ status: 'cancelled', createdAt: daysAgo(3), updatedAt: daysAgo(2), number: 2 }),
    ]
    const archived: Issue[] = [
      makeIssue({
        status: 'done',
        createdAt: daysAgo(10),
        updatedAt: daysAgo(9),
        archivedAt: daysAgo(1),
        number: 3,
      }),
    ]
    const issuesBefore = JSON.stringify(issues)
    const archivedBefore = JSON.stringify(archived)

    deriveRecentDigest(issues, archived)

    expect(JSON.stringify(issues)).toBe(issuesBefore)
    expect(JSON.stringify(archived)).toBe(archivedBefore)
  })
})

describe('useRecentDigest', () => {
  it('returns { completed, failed, archived, isLoading } shape derived from useIssues and useArchivedIssues', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useIssuesMock.mockReturnValue({
      data: [
        makeIssue({ status: 'done', createdAt: daysAgo(2), updatedAt: daysAgo(1), number: 1 }),
        makeIssue({
          status: 'cancelled',
          createdAt: daysAgo(3),
          updatedAt: daysAgo(2),
          number: 2,
        }),
      ],
      isLoading: false,
    })
    useArchivedIssuesMock.mockReturnValue({
      data: [
        makeIssue({
          status: 'done',
          createdAt: daysAgo(10),
          updatedAt: daysAgo(9),
          archivedAt: daysAgo(1),
          number: 3,
        }),
      ],
      isLoading: false,
    })

    const { result } = renderHook(() => useRecentDigest(), { wrapper: makeWrapper() })

    expect(result.current.completed.map((i) => i.number)).toEqual([1])
    expect(result.current.failed.map((i) => i.number)).toEqual([2])
    expect(result.current.archived.map((i) => i.number)).toEqual([3])
    expect(result.current.isLoading).toBe(false)
  })

  it('reports isLoading=true while either query is loading (and project is set)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useIssuesMock.mockReturnValue({ data: undefined, isLoading: true })
    useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

    const { result } = renderHook(() => useRecentDigest(), { wrapper: makeWrapper() })

    expect(result.current.isLoading).toBe(true)
    expect(result.current.completed).toEqual([])
    expect(result.current.failed).toEqual([])
    expect(result.current.archived).toEqual([])
  })

  it('returns empty arrays and isLoading=false when both queries have undefined data', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useIssuesMock.mockReturnValue({ data: undefined, isLoading: false })
    useArchivedIssuesMock.mockReturnValue({ data: undefined, isLoading: false })

    const { result } = renderHook(() => useRecentDigest(), { wrapper: makeWrapper() })

    expect(result.current).toEqual({
      completed: [],
      failed: [],
      archived: [],
      isLoading: false,
    })
  })

  it('calls useIssues and useArchivedIssues with explicit { projectId } from useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-42' })
    useIssuesMock.mockReturnValue({ data: [], isLoading: false })
    useArchivedIssuesMock.mockReturnValue({ data: [], isLoading: false })

    renderHook(() => useRecentDigest(), { wrapper: makeWrapper() })

    expect(useIssuesMock).toHaveBeenLastCalledWith({ projectId: 'proj-42' })
    expect(useArchivedIssuesMock).toHaveBeenLastCalledWith({ projectId: 'proj-42' })
  })

  it('does not call the queries with a projectId when no project is selected', () => {
    useProjectMock.mockReturnValue({ projectId: null })
    useIssuesMock.mockReturnValue({ data: undefined, isLoading: false })
    useArchivedIssuesMock.mockReturnValue({ data: undefined, isLoading: false })

    renderHook(() => useRecentDigest(), { wrapper: makeWrapper() })

    expect(useIssuesMock).toHaveBeenLastCalledWith(undefined)
    expect(useArchivedIssuesMock).toHaveBeenLastCalledWith(undefined)
  })

  it('reports isLoading=false when no project is selected even if a query says isLoading', () => {
    useProjectMock.mockReturnValue({ projectId: null })
    useIssuesMock.mockReturnValue({ data: undefined, isLoading: true })
    useArchivedIssuesMock.mockReturnValue({ data: undefined, isLoading: true })

    const { result } = renderHook(() => useRecentDigest(), { wrapper: makeWrapper() })

    expect(result.current.isLoading).toBe(false)
  })
})
