import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus, type Issue } from './issue'
import { isRunningIssue } from './running'

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Issue title',
    status: IssueStatus.InProgress,
    health: IssueHealth.Active,
    projectId: 'project-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

describe('isRunningIssue', () => {
  it('treats in-progress active, paused, and blocked issues as running', () => {
    expect(isRunningIssue(makeIssue({ health: IssueHealth.Active }))).toBe(true)
    expect(isRunningIssue(makeIssue({ health: IssueHealth.Paused }))).toBe(true)
    expect(isRunningIssue(makeIssue({ health: IssueHealth.Blocked }))).toBe(true)
  })

  it('excludes done, cancelled, and non-in-progress issues', () => {
    expect(isRunningIssue(makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done }))).toBe(false)
    expect(isRunningIssue(makeIssue({ status: IssueStatus.Cancelled, health: IssueHealth.Cancelled }))).toBe(false)
    expect(isRunningIssue(makeIssue({ status: IssueStatus.Backlog, health: IssueHealth.Active }))).toBe(false)
  })
})
