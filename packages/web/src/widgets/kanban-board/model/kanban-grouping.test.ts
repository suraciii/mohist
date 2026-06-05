import { describe, it, expect } from 'vitest'
import { groupIssuesByStage, filterClosedFromDone, getDoneColumnCounts } from './kanban-grouping'
import { IssueStatus, WorkflowStage, IssueHealth, type Issue } from '../../../entities/issue'

function makeIssue(overrides: Partial<Issue> & { id: string }): Issue {
  return {
    number: 1,
    title: 'Test issue',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'p1',
    labels: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
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
      makeIssue({ id: '1', status: IssueStatus.Backlog }),
      makeIssue({ id: '2', status: IssueStatus.Backlog }),
      makeIssue({ id: '3', status: IssueStatus.InProgress, workflowStage: WorkflowStage.Build }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === IssueStatus.Backlog)!.issues).toHaveLength(2)
    expect(cols.find((c) => c.key === IssueStatus.InProgress)!.issues).toHaveLength(1)
  })

  it('groups cancelled issues by their issue stage', () => {
    const issues = [
      makeIssue({ id: '1', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ id: '2', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ id: '3', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
    ]
    const cols = groupIssuesByStage(issues)
    const cancelledCol = cols.find((c) => c.key === IssueStatus.Cancelled)!
    expect(cancelledCol.issues).toHaveLength(3)
    expect(cols.find((c) => c.key === IssueStatus.Done)!.issues).toHaveLength(0)
  })

  it('keeps active and blocked issues in their issue lifecycle stage', () => {
    const issues = [
      makeIssue({ id: '1', status: IssueStatus.InProgress, health: IssueHealth.Blocked, workflowStage: WorkflowStage.Build }),
      makeIssue({ id: '2', status: IssueStatus.InProgress, health: IssueHealth.Active, workflowStage: WorkflowStage.Check }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === IssueStatus.InProgress)!.issues).toHaveLength(2)
    expect(cols.find((c) => c.key === IssueStatus.Done)!.issues).toHaveLength(0)
  })

  it('mixes done, cancelled, and in-progress issues correctly', () => {
    const issues = [
      makeIssue({ id: '1', status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ id: '2', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ id: '3', status: IssueStatus.Done, health: IssueHealth.Done }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === IssueStatus.InProgress)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === IssueStatus.Cancelled)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === IssueStatus.Done)!.issues).toHaveLength(1)
  })
})

describe('filterClosedFromDone', () => {
  const columns = groupIssuesByStage([
    makeIssue({ id: '1', status: IssueStatus.Done, health: IssueHealth.Done }),
    makeIssue({ id: '2', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
    makeIssue({ id: '3', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
  ])

  it('returns columns unchanged when showClosed is true', () => {
    const result = filterClosedFromDone(columns, true)
    expect(result).toBe(columns)
  })

  it('hides cancelled issues when showClosed is false', () => {
    const result = filterClosedFromDone(columns, false)
    const cancelledCol = result.find((c) => c.key === IssueStatus.Cancelled)!
    expect(cancelledCol.issues).toHaveLength(0)
  })

  it('does not affect non-cancelled columns', () => {
    const result = filterClosedFromDone(columns, false)
    for (const col of result) {
      if (col.key !== IssueStatus.Cancelled) {
        const original = columns.find((c) => c.key === col.key)!
        expect(col.issues).toBe(original.issues)
      }
    }
  })
})

describe('getDoneColumnCounts', () => {
  it('returns zeros for empty columns', () => {
    const cols = groupIssuesByStage([])
    const counts = getDoneColumnCounts(cols)
    expect(counts.closedCount).toBe(0)
    expect(counts.doneTotalCount).toBe(0)
  })

  it('counts cancelled and completed issues in terminal columns', () => {
    const cols = groupIssuesByStage([
      makeIssue({ id: '1', status: IssueStatus.Done, health: IssueHealth.Done }),
      makeIssue({ id: '2', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
      makeIssue({ id: '3', status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }),
    ])
    const counts = getDoneColumnCounts(cols)
    expect(counts.closedCount).toBe(2)
    expect(counts.doneTotalCount).toBe(3)
  })

  it('returns zero closed when all issues in Done are completed', () => {
    const cols = groupIssuesByStage([
      makeIssue({ id: '1', status: IssueStatus.Done, health: IssueHealth.Done }),
      makeIssue({ id: '2', status: IssueStatus.Done, health: IssueHealth.Done }),
    ])
    const counts = getDoneColumnCounts(cols)
    expect(counts.closedCount).toBe(0)
    expect(counts.doneTotalCount).toBe(2)
  })
})
