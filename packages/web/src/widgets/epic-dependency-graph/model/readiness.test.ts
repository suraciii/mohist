import { describe, it, expect } from 'vitest'
import { deriveReadiness, readinessLabel, readinessTone, statusColors } from './readiness'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue/@x/types'
import type { LinkedIssue } from '../../../entities/epic/model/types'

function makeLinkedIssue(overrides: Partial<LinkedIssue> = {}): LinkedIssue {
  return {
    number: 1,
    title: 'Default issue',
    status: IssueStatus.Backlog,
    stage: WorkflowStage.Plan,
    health: IssueHealth.Active,
    priority: 'p2',
    canStart: false,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    ...overrides,
  }
}

describe('deriveReadiness', () => {
  it('returns "done" when status is done', () => {
    const result = deriveReadiness(makeLinkedIssue({ status: IssueStatus.Done }))
    expect(result.readiness).toBe('done')
    expect(result.waitingForIssueNumber).toBeNull()
  })

  it('returns "in-progress" when status is in_progress', () => {
    const result = deriveReadiness(makeLinkedIssue({ status: IssueStatus.InProgress }))
    expect(result.readiness).toBe('in-progress')
    expect(result.waitingForIssueNumber).toBeNull()
  })

  it('returns "waiting" when blocker.kind is waiting-for and exposes the blocking #N', () => {
    const result = deriveReadiness(makeLinkedIssue({
      status: IssueStatus.Backlog,
      canStart: false,
      startBlocker: { kind: 'waiting-for', issue: { number: 42, title: 'Blocker' } },
    }))
    expect(result.readiness).toBe('waiting')
    expect(result.waitingForIssueNumber).toBe(42)
  })

  it('returns "can-start" when canStart is true and status is backlog', () => {
    const result = deriveReadiness(makeLinkedIssue({
      status: IssueStatus.Backlog,
      canStart: true,
      startBlocker: null,
    }))
    expect(result.readiness).toBe('can-start')
    expect(result.waitingForIssueNumber).toBeNull()
  })

  it('prefers "done" over waiting when status is done', () => {
    const result = deriveReadiness(makeLinkedIssue({
      status: IssueStatus.Done,
      canStart: false,
      startBlocker: { kind: 'waiting-for', issue: { number: 7, title: 'Done yet waiting' } },
    }))
    expect(result.readiness).toBe('done')
  })

  it('prefers "in-progress" over waiting when status is in_progress (a stale blocker does not downgrade)', () => {
    const result = deriveReadiness(makeLinkedIssue({
      status: IssueStatus.InProgress,
      canStart: false,
      startBlocker: { kind: 'waiting-for', issue: { number: 7, title: 'Stale blocker' } },
    }))
    expect(result.readiness).toBe('in-progress')
    expect(result.waitingForIssueNumber).toBeNull()
  })

  it('treats a draft blocker (non waiting-for) as waiting without a blocking issue number', () => {
    const result = deriveReadiness(makeLinkedIssue({
      status: IssueStatus.Backlog,
      canStart: false,
      startBlocker: { kind: 'draft' },
    }))
    expect(result.readiness).toBe('waiting')
    expect(result.waitingForIssueNumber).toBeNull()
  })

  it('treats a null blocker with canStart false and backlog status as waiting without a blocking issue number', () => {
    const result = deriveReadiness(makeLinkedIssue({
      status: IssueStatus.Backlog,
      canStart: false,
      startBlocker: null,
    }))
    expect(result.readiness).toBe('waiting')
    expect(result.waitingForIssueNumber).toBeNull()
  })

  it('treats a cancelled issue as waiting rather than in-progress', () => {
    const result = deriveReadiness(makeLinkedIssue({
      status: IssueStatus.Cancelled,
      canStart: false,
      startBlocker: null,
    }))
    expect(result.readiness).toBe('waiting')
    expect(result.waitingForIssueNumber).toBeNull()
  })

  it('returns exactly one of the four readiness states for every input', () => {
    const cases: LinkedIssue[] = [
      makeLinkedIssue({ status: IssueStatus.Done, canStart: false }),
      makeLinkedIssue({ status: IssueStatus.InProgress, canStart: false }),
      makeLinkedIssue({ status: IssueStatus.Cancelled, canStart: false }),
      makeLinkedIssue({ status: IssueStatus.Backlog, canStart: true }),
      makeLinkedIssue({ status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 1, title: 'X' } } }),
      makeLinkedIssue({ status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } }),
      makeLinkedIssue({ status: IssueStatus.Backlog, canStart: false, startBlocker: null }),
    ]
    const valid = new Set(['can-start', 'waiting', 'in-progress', 'done'])
    for (const c of cases) {
      const r = deriveReadiness(c)
      expect(valid.has(r.readiness)).toBe(true)
    }
  })
})

describe('readinessLabel', () => {
  it('returns the human-readable label for each readiness state', () => {
    expect(readinessLabel('can-start')).toBe('Can start')
    expect(readinessLabel('waiting')).toBe('Waiting')
    expect(readinessLabel('in-progress')).toBe('In progress')
    expect(readinessLabel('done')).toBe('Done')
  })
})

describe('readinessTone', () => {
  it('returns a non-empty color string for every readiness state', () => {
    expect(readinessTone('can-start')).toMatch(/^#[0-9a-fA-F]{6}$/)
    expect(readinessTone('waiting')).toMatch(/^#[0-9a-fA-F]{6}$/)
    expect(readinessTone('in-progress')).toMatch(/^#[0-9a-fA-F]{6}$/)
    expect(readinessTone('done')).toMatch(/^#[0-9a-fA-F]{6}$/)
  })
})

describe('statusColors', () => {
  it('returns a token for backlog', () => {
    const c = statusColors(IssueStatus.Backlog)
    expect(c.background).toBeTruthy()
    expect(c.border).toBeTruthy()
    expect(c.text).toBeTruthy()
    expect(c.accent).toBeTruthy()
  })

  it('returns a token for in_progress', () => {
    const c = statusColors(IssueStatus.InProgress)
    expect(c.background).toBeTruthy()
    expect(c.border).toBeTruthy()
    expect(c.text).toBeTruthy()
    expect(c.accent).toBeTruthy()
  })

  it('returns a token for done', () => {
    const c = statusColors(IssueStatus.Done)
    expect(c.background).toBeTruthy()
    expect(c.border).toBeTruthy()
    expect(c.text).toBeTruthy()
    expect(c.accent).toBeTruthy()
  })

  it('returns a token for cancelled', () => {
    const c = statusColors(IssueStatus.Cancelled)
    expect(c.background).toBeTruthy()
    expect(c.border).toBeTruthy()
    expect(c.text).toBeTruthy()
    expect(c.accent).toBeTruthy()
  })

  it('returns distinct tokens for backlog vs done (status drives distinct colors)', () => {
    expect(statusColors(IssueStatus.Backlog).accent).not.toBe(statusColors(IssueStatus.Done).accent)
    expect(statusColors(IssueStatus.Backlog).background).not.toBe(statusColors(IssueStatus.InProgress).background)
  })
})
