import { describe, it, expect } from 'vitest'
import {
  groupIssuesByStage,
  filterCancelledFromColumns,
  getCancelledColumnCount,
} from './kanban-grouping'
import { IssueStatus, WorkflowStage, IssueHealth, type Issue } from '../../../entities/issue'

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Test issue',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'p1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

describe('groupIssuesByStage', () => {
  it('returns empty columns when no issues', () => {
    const cols = groupIssuesByStage([])
    expect(cols).toHaveLength(4)
    for (const col of cols) {
      expect(col.issues).toEqual([])
    }
  })

  it('groups active issues by their stage', () => {
    const issues = [
      makeIssue({ number: 1, status: IssueStatus.Backlog }),
      makeIssue({ number: 2, status: IssueStatus.Backlog }),
      makeIssue({ number: 3, status: IssueStatus.InProgress, workflowStage: WorkflowStage.Build }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === IssueStatus.Backlog)!.issues).toHaveLength(2)
    expect(cols.find((c) => c.key === IssueStatus.InProgress)!.issues).toHaveLength(1)
  })

  it('groups cancelled issues by their issue stage', () => {
    const issues = [
      makeIssue({ number: 1, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ number: 2, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ number: 3, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
    ]
    const cols = groupIssuesByStage(issues)
    const cancelledCol = cols.find((c) => c.key === IssueStatus.Cancelled)!
    expect(cancelledCol.issues).toHaveLength(3)
    expect(cols.find((c) => c.key === IssueStatus.Done)!.issues).toHaveLength(0)
  })

  it('keeps active and blocked issues in their issue lifecycle stage', () => {
    const issues = [
      makeIssue({ number: 1, status: IssueStatus.InProgress, health: IssueHealth.Blocked, workflowStage: WorkflowStage.Build }),
      makeIssue({ number: 2, status: IssueStatus.InProgress, health: IssueHealth.Active, workflowStage: WorkflowStage.Check }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === IssueStatus.InProgress)!.issues).toHaveLength(2)
    expect(cols.find((c) => c.key === IssueStatus.Done)!.issues).toHaveLength(0)
  })

  it('mixes done, cancelled, and in-progress issues correctly', () => {
    const issues = [
      makeIssue({ number: 1, status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ number: 2, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ number: 3, status: IssueStatus.Done, health: IssueHealth.Done }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === IssueStatus.InProgress)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === IssueStatus.Cancelled)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === IssueStatus.Done)!.issues).toHaveLength(1)
  })
})

describe('filterCancelledFromColumns', () => {
  const columns = groupIssuesByStage([
    makeIssue({ number: 1, status: IssueStatus.Done, health: IssueHealth.Done }),
    makeIssue({ number: 2, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
    makeIssue({ number: 3, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
  ])

  it('is an identity function when showCancelled is true', () => {
    const result = filterCancelledFromColumns(columns, true)
    expect(result).toBe(columns)
  })

  it('is an identity function when showCancelled is false', () => {
    const result = filterCancelledFromColumns(columns, false)
    expect(result).toBe(columns)
  })

  it('preserves the full Cancelled column issues regardless of showCancelled', () => {
    const result = filterCancelledFromColumns(columns, false)
    const cancelledCol = result.find((c) => c.key === IssueStatus.Cancelled)!
    expect(cancelledCol.issues).toHaveLength(2)
    expect(cancelledCol.issues).toBe(
      columns.find((c) => c.key === IssueStatus.Cancelled)!.issues,
    )
  })

  it('does not affect non-cancelled columns', () => {
    const result = filterCancelledFromColumns(columns, false)
    for (const col of result) {
      if (col.key !== IssueStatus.Cancelled) {
        const original = columns.find((c) => c.key === col.key)!
        expect(col.issues).toBe(original.issues)
      }
    }
  })
})

describe('getCancelledColumnCount', () => {
  it('returns zeros for empty columns', () => {
    const cols = groupIssuesByStage([])
    const counts = getCancelledColumnCount(cols)
    expect(counts.cancelledCount).toBe(0)
    expect(counts.doneTotalCount).toBe(0)
  })

  it('counts cancelled and completed issues in terminal columns', () => {
    const cols = groupIssuesByStage([
      makeIssue({ number: 1, status: IssueStatus.Done, health: IssueHealth.Done }),
      makeIssue({ number: 2, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ number: 3, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
    ])
    const counts = getCancelledColumnCount(cols)
    expect(counts.cancelledCount).toBe(2)
    expect(counts.doneTotalCount).toBe(3)
  })

  it('returns zero cancelled when all issues in Done are completed', () => {
    const cols = groupIssuesByStage([
      makeIssue({ number: 1, status: IssueStatus.Done, health: IssueHealth.Done }),
      makeIssue({ number: 2, status: IssueStatus.Done, health: IssueHealth.Done }),
    ])
    const counts = getCancelledColumnCount(cols)
    expect(counts.cancelledCount).toBe(0)
    expect(counts.doneTotalCount).toBe(2)
  })

  it('reflects the real Cancelled column size even when showCancelled toggle would hide them', () => {
    const cols = groupIssuesByStage([
      makeIssue({ number: 1, status: IssueStatus.Done, health: IssueHealth.Done }),
      makeIssue({ number: 2, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ number: 3, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ number: 4, status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
    ])
    const filtered = filterCancelledFromColumns(cols, false)
    const counts = getCancelledColumnCount(filtered)
    expect(counts.cancelledCount).toBe(3)
    expect(counts.doneTotalCount).toBe(4)
  })
})
