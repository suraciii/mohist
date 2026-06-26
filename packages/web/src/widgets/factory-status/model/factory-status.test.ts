// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentStatus } from '../../../entities/agent'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { deriveFactoryStatus, isTodayLocal } from './factory-status'

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Default issue title',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'project-1',
    labels: {},
    createdAt: '2026-06-18T00:00:00.000Z',
    updatedAt: '2026-06-18T00:00:00.000Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function makeAgentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    running: false,
    issueId: null,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 1 },
    ...overrides,
  }
}

describe('isTodayLocal', () => {
  const now = new Date('2026-06-26T12:00:00.000Z')

  beforeEach(() => {
    vi.setSystemTime(now)
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('returns true for a timestamp on the same local calendar day', () => {
    expect(isTodayLocal(now.toISOString())).toBe(true)
  })

  it('returns false for a timestamp on the previous local calendar day', () => {
    expect(isTodayLocal(new Date(now.getTime() - 24 * 60 * 60 * 1000).toISOString())).toBe(false)
  })

  it('returns false for a timestamp on the next local calendar day', () => {
    expect(isTodayLocal(new Date(now.getTime() + 24 * 60 * 60 * 1000).toISOString())).toBe(false)
  })
})

describe('deriveFactoryStatus', () => {
  const now = new Date('2026-06-26T12:00:00.000Z')
  const todayIso = now.toISOString()
  const yesterdayIso = new Date(now.getTime() - 24 * 60 * 60 * 1000).toISOString()

  beforeEach(() => {
    vi.setSystemTime(now)
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('returns zero counts and unavailable runner when inputs are undefined', () => {
    expect(deriveFactoryStatus(undefined, undefined)).toEqual({
      runnerAvailable: false,
      inFlight: 0,
      awaitingApproval: 0,
      shippedToday: 0,
      todayCost: undefined,
    })
  })

  it('treats runnerAvailable===true as available and any other value as unavailable', () => {
    expect(deriveFactoryStatus([], makeAgentStatus({ runnerAvailable: true })).runnerAvailable).toBe(true)
    expect(deriveFactoryStatus([], makeAgentStatus({ runnerAvailable: false })).runnerAvailable).toBe(false)
    expect(deriveFactoryStatus([], makeAgentStatus({ runnerAvailable: undefined })).runnerAvailable).toBe(false)
  })

  it('counts in-flight issues using the spec rule', () => {
    const issues: Issue[] = [
      makeIssue({ id: 'a', status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ id: 'b', status: IssueStatus.InProgress, health: IssueHealth.Done }),
      makeIssue({ id: 'c', status: IssueStatus.InProgress, health: IssueHealth.Cancelled }),
      makeIssue({ id: 'd', status: IssueStatus.Done, health: IssueHealth.Done }),
      makeIssue({ id: 'e', status: IssueStatus.InProgress, health: IssueHealth.Blocked }),
    ]

    expect(deriveFactoryStatus(issues, makeAgentStatus()).inFlight).toBe(2)
  })

  it('counts awaiting-approval issues', () => {
    const issues: Issue[] = [
      makeIssue({ id: 'a', approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ id: 'b', approvalState: { status: 'approved', requestedAt: todayIso, respondedAt: todayIso } }),
      makeIssue({ id: 'c' }),
    ]

    expect(deriveFactoryStatus(issues, makeAgentStatus()).awaitingApproval).toBe(1)
  })

  it('counts only done issues updated today as shippedToday', () => {
    const issues: Issue[] = [
      makeIssue({ id: 'today', status: IssueStatus.Done, health: IssueHealth.Done, updatedAt: todayIso }),
      makeIssue({ id: 'yesterday', status: IssueStatus.Done, health: IssueHealth.Done, updatedAt: yesterdayIso }),
      makeIssue({ id: 'in-progress', status: IssueStatus.InProgress, health: IssueHealth.Active, updatedAt: todayIso }),
    ]

    expect(deriveFactoryStatus(issues, makeAgentStatus()).shippedToday).toBe(1)
  })

  it('returns all fields together for a mixed input', () => {
    const issues: Issue[] = [
      makeIssue({ id: 'run-1', status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ id: 'run-2', status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ id: 'approve-1', approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ id: 'approve-2', approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ id: 'ship-1', status: IssueStatus.Done, health: IssueHealth.Done, updatedAt: todayIso }),
      makeIssue({ id: 'ship-2', status: IssueStatus.Done, health: IssueHealth.Done, updatedAt: todayIso }),
      makeIssue({ id: 'ship-old', status: IssueStatus.Done, health: IssueHealth.Done, updatedAt: yesterdayIso }),
    ]

    expect(deriveFactoryStatus(issues, makeAgentStatus({ runnerAvailable: true }))).toEqual({
      runnerAvailable: true,
      inFlight: 2,
      awaitingApproval: 2,
      shippedToday: 2,
      todayCost: undefined,
    })
  })

  it('leaves todayCost undefined', () => {
    expect(deriveFactoryStatus([], makeAgentStatus()).todayCost).toBeUndefined()
  })
})
