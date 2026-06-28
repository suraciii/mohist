import { describe, it, expect } from 'vitest'
import { EpicStatus, type EpicWithProgress } from '../../../entities/epic'
import { IssueHealth } from '../../../entities/issue'
import { groupActiveEpics } from './groupActiveEpics'

function makeEpic(overrides: Partial<EpicWithProgress> & { id: string }): EpicWithProgress {
  return {
    number: null,
    title: `Epic ${overrides.id}`,
    description: '',
    priority: 'p1',
    status: EpicStatus.Idle,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    progress: {
      deliveredCount: 0,
      totalIssueCount: 0,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      nextIssueReason: null,
      readyToMarkDone: false,
    },
    ...overrides,
  }
}

const runningOnly = makeEpic({
  id: 'running-only',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 1,
    blockedIssues: [],
    activeIssues: [{ id: 'i1', number: 1, title: 'In flight', health: IssueHealth.Active }],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

const runningWithNextAndReason = makeEpic({
  id: 'running-both',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 2,
    blockedIssues: [],
    activeIssues: [{ id: 'i1', number: 1, title: 'In flight', health: IssueHealth.Active }],
    nextIssue: { id: 'i2', number: 2, title: 'Queued next' },
    nextIssueReason: 'Waiting for #1 to complete',
    readyToMarkDone: false,
  },
})

const readyToStart = makeEpic({
  id: 'ready',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 1,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: { id: 'i1', number: 1, title: 'Start me' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

const readyToStartAlsoFlagged = makeEpic({
  id: 'ready-flagged',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 1,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: { id: 'i1', number: 1, title: 'Start me' },
    nextIssueReason: 'some leftover reason',
    readyToMarkDone: false,
  },
})

const waitingBlocked = makeEpic({
  id: 'waiting',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 1,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: 'Draft blocked on review',
    readyToMarkDone: false,
  },
})

const idleReadyToMarkDone = makeEpic({
  id: 'idle-ready',
  progress: {
    deliveredCount: 3,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: true,
  },
})

const idleEmpty = makeEpic({
  id: 'idle-empty',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 0,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

describe('groupActiveEpics', () => {
  it('returns four empty buckets for an empty input', () => {
    const groups = groupActiveEpics([])
    expect(groups).toEqual({
      running: [],
      readyToStart: [],
      waitingBlocked: [],
      idleEmpty: [],
    })
  })

  it('places an epic with non-empty activeIssues into running regardless of other fields', () => {
    const groups = groupActiveEpics([runningOnly])
    expect(groups.running).toEqual([runningOnly])
    expect(groups.readyToStart).toEqual([])
    expect(groups.waitingBlocked).toEqual([])
    expect(groups.idleEmpty).toEqual([])
  })

  it('places an epic with non-null nextIssue and no activeIssues into readyToStart', () => {
    const groups = groupActiveEpics([readyToStart])
    expect(groups.running).toEqual([])
    expect(groups.readyToStart).toEqual([readyToStart])
    expect(groups.waitingBlocked).toEqual([])
    expect(groups.idleEmpty).toEqual([])
  })

  it('keeps an epic with non-null nextIssue in readyToStart even when nextIssueReason is also non-null', () => {
    const groups = groupActiveEpics([readyToStartAlsoFlagged])
    expect(groups.running).toEqual([])
    expect(groups.readyToStart).toEqual([readyToStartAlsoFlagged])
    expect(groups.waitingBlocked).toEqual([])
    expect(groups.idleEmpty).toEqual([])
  })

  it('places an epic with null nextIssue and non-null nextIssueReason into waitingBlocked', () => {
    const groups = groupActiveEpics([waitingBlocked])
    expect(groups.running).toEqual([])
    expect(groups.readyToStart).toEqual([])
    expect(groups.waitingBlocked).toEqual([waitingBlocked])
    expect(groups.idleEmpty).toEqual([])
  })

  it('places an epic with no active issue and no nextIssueReason into idleEmpty', () => {
    const groups = groupActiveEpics([idleEmpty])
    expect(groups.running).toEqual([])
    expect(groups.readyToStart).toEqual([])
    expect(groups.waitingBlocked).toEqual([])
    expect(groups.idleEmpty).toEqual([idleEmpty])
  })

  it('places an idle epic with readyToMarkDone=true into idleEmpty', () => {
    const groups = groupActiveEpics([idleReadyToMarkDone])
    expect(groups.running).toEqual([])
    expect(groups.readyToStart).toEqual([])
    expect(groups.waitingBlocked).toEqual([])
    expect(groups.idleEmpty).toEqual([idleReadyToMarkDone])
  })

  it('lets activeIssues beat nextIssueReason so an epic with both lands in running, not waitingBlocked', () => {
    const groups = groupActiveEpics([runningWithNextAndReason])
    expect(groups.running).toEqual([runningWithNextAndReason])
    expect(groups.waitingBlocked).toEqual([])
    expect(groups.readyToStart).toEqual([])
    expect(groups.idleEmpty).toEqual([])
  })

  it('partitions every active epic into exactly one bucket — no epic appears twice and none are dropped', () => {
    const all: EpicWithProgress[] = [
      runningOnly,
      runningWithNextAndReason,
      readyToStart,
      readyToStartAlsoFlagged,
      waitingBlocked,
      idleReadyToMarkDone,
      idleEmpty,
    ]

    const groups = groupActiveEpics(all)
    const total =
      groups.running.length +
      groups.readyToStart.length +
      groups.waitingBlocked.length +
      groups.idleEmpty.length

    expect(total).toBe(all.length)

    const seen = new Set<string>()
    for (const epic of groups.running) {
      expect(seen.has(epic.id)).toBe(false)
      seen.add(epic.id)
    }
    for (const epic of groups.readyToStart) {
      expect(seen.has(epic.id)).toBe(false)
      seen.add(epic.id)
    }
    for (const epic of groups.waitingBlocked) {
      expect(seen.has(epic.id)).toBe(false)
      seen.add(epic.id)
    }
    for (const epic of groups.idleEmpty) {
      expect(seen.has(epic.id)).toBe(false)
      seen.add(epic.id)
    }
    expect(seen.size).toBe(all.length)
  })

  it('preserves input order within each bucket', () => {
    const all: EpicWithProgress[] = [
      readyToStart,
      idleEmpty,
      runningOnly,
      waitingBlocked,
      idleReadyToMarkDone,
    ]

    const groups = groupActiveEpics(all)
    expect(groups.readyToStart.map(e => e.id)).toEqual(['ready'])
    expect(groups.idleEmpty.map(e => e.id)).toEqual(['idle-empty', 'idle-ready'])
    expect(groups.running.map(e => e.id)).toEqual(['running-only'])
    expect(groups.waitingBlocked.map(e => e.id)).toEqual(['waiting'])
  })

  it('does not mutate the input array or the epic references', () => {
    const all: EpicWithProgress[] = [runningWithNextAndReason, readyToStart, waitingBlocked, idleEmpty]
    const snapshot = all.map(e => ({ id: e.id, progress: { ...e.progress, activeIssues: [...e.progress.activeIssues] } }))

    groupActiveEpics(all)

    expect(all.length).toBe(4)
    for (let i = 0; i < all.length; i++) {
      expect(all[i].id).toBe(snapshot[i].id)
      expect(all[i].progress.activeIssues.length).toBe(snapshot[i].progress.activeIssues.length)
    }
  })
})
