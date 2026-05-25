import { describe, it, expect } from 'vitest'
import { groupIssuesByStage, filterClosedFromDone, getDoneColumnCounts } from './kanban-grouping'
import type { Issue } from './types'
import { Stage, IssueStatus } from './types'

function makeIssue(overrides: Partial<Issue> & { id: string }): Issue {
  return {
    number: 1,
    title: 'Test issue',
    stage: Stage.Backlog,
    status: IssueStatus.Active,
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
    expect(cols).toHaveLength(6)
    for (const col of cols) {
      expect(col.issues).toEqual([])
    }
  })

  it('groups active issues by their stage', () => {
    const issues = [
      makeIssue({ id: '1', stage: Stage.Backlog }),
      makeIssue({ id: '2', stage: Stage.Plan }),
      makeIssue({ id: '3', stage: Stage.Build }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === Stage.Backlog)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === Stage.Plan)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === Stage.Build)!.issues).toHaveLength(1)
  })

  it('routes closed issues to Done column regardless of original stage', () => {
    const issues = [
      makeIssue({ id: '1', stage: Stage.Backlog, status: IssueStatus.Closed }),
      makeIssue({ id: '2', stage: Stage.Plan, status: IssueStatus.Closed }),
      makeIssue({ id: '3', stage: Stage.Build, status: IssueStatus.Closed }),
    ]
    const cols = groupIssuesByStage(issues)
    const doneCol = cols.find((c) => c.key === Stage.Done)!
    expect(doneCol.issues).toHaveLength(3)
    expect(cols.find((c) => c.key === Stage.Backlog)!.issues).toHaveLength(0)
    expect(cols.find((c) => c.key === Stage.Plan)!.issues).toHaveLength(0)
    expect(cols.find((c) => c.key === Stage.Build)!.issues).toHaveLength(0)
  })

  it('keeps non-closed issues in original stage', () => {
    const issues = [
      makeIssue({ id: '1', stage: Stage.Build, status: IssueStatus.Blocked }),
      makeIssue({ id: '2', stage: Stage.Check, status: IssueStatus.Active }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === Stage.Build)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === Stage.Check)!.issues).toHaveLength(1)
    expect(cols.find((c) => c.key === Stage.Done)!.issues).toHaveLength(0)
  })

  it('mixes closed and non-closed issues correctly', () => {
    const issues = [
      makeIssue({ id: '1', stage: Stage.Build, status: IssueStatus.Active }),
      makeIssue({ id: '2', stage: Stage.Build, status: IssueStatus.Closed }),
      makeIssue({ id: '3', stage: Stage.Done, status: IssueStatus.Completed }),
    ]
    const cols = groupIssuesByStage(issues)
    expect(cols.find((c) => c.key === Stage.Build)!.issues).toHaveLength(1)
    const doneCol = cols.find((c) => c.key === Stage.Done)!
    expect(doneCol.issues).toHaveLength(2)
  })
})

describe('filterClosedFromDone', () => {
  const columns = groupIssuesByStage([
    makeIssue({ id: '1', stage: Stage.Done, status: IssueStatus.Completed }),
    makeIssue({ id: '2', stage: Stage.Build, status: IssueStatus.Closed }),
    makeIssue({ id: '3', stage: Stage.Done, status: IssueStatus.Closed }),
  ])

  it('returns columns unchanged when showClosed is true', () => {
    const result = filterClosedFromDone(columns, true)
    expect(result).toBe(columns)
  })

  it('removes closed issues from Done column when showClosed is false', () => {
    const result = filterClosedFromDone(columns, false)
    const doneCol = result.find((c) => c.key === Stage.Done)!
    expect(doneCol.issues).toHaveLength(1)
    expect(doneCol.issues[0].id).toBe('1')
  })

  it('does not affect non-Done columns', () => {
    const result = filterClosedFromDone(columns, false)
    for (const col of result) {
      if (col.key !== Stage.Done) {
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

  it('counts closed and total issues in Done column', () => {
    const cols = groupIssuesByStage([
      makeIssue({ id: '1', stage: Stage.Done, status: IssueStatus.Completed }),
      makeIssue({ id: '2', stage: Stage.Build, status: IssueStatus.Closed }),
      makeIssue({ id: '3', stage: Stage.Plan, status: IssueStatus.Closed }),
    ])
    const counts = getDoneColumnCounts(cols)
    expect(counts.closedCount).toBe(2)
    expect(counts.doneTotalCount).toBe(3)
  })

  it('returns zero closed when all issues in Done are completed', () => {
    const cols = groupIssuesByStage([
      makeIssue({ id: '1', stage: Stage.Done, status: IssueStatus.Completed }),
      makeIssue({ id: '2', stage: Stage.Done, status: IssueStatus.Completed }),
    ])
    const counts = getDoneColumnCounts(cols)
    expect(counts.closedCount).toBe(0)
    expect(counts.doneTotalCount).toBe(2)
  })
})
