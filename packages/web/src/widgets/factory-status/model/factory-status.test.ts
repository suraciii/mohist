import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentCostMetricDto } from '../../../entities/agent'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { deriveFactoryStatus, isTodayLocal } from './factory-status'

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
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

function makeTodayCost(overrides: Partial<AgentCostMetricDto> = {}): AgentCostMetricDto {
  return {
    amount: 1.25,
    currency: 'USD',
    sampleCount: 1,
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

  it('returns zero counts when inputs are undefined', () => {
    expect(deriveFactoryStatus(undefined)).toEqual({
      inFlight: 0,
      awaitingApproval: 0,
      shippedToday: 0,
      todayCost: undefined,
    })
  })

  it('counts in-flight issues using the spec rule', () => {
    const issues: Issue[] = [
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Done }),
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Cancelled }),
      makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done }),
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Blocked }),
    ]

    expect(deriveFactoryStatus(issues).inFlight).toBe(2)
  })

  it('counts awaiting-approval issues', () => {
    const issues: Issue[] = [
      makeIssue({ approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ approvalState: { status: 'approved', requestedAt: todayIso, respondedAt: todayIso } }),
      makeIssue({}),
    ]

    expect(deriveFactoryStatus(issues).awaitingApproval).toBe(1)
  })

  it('counts only done issues completed today as shippedToday', () => {
    const issues: Issue[] = [
      makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
      makeIssue({
        status: IssueStatus.Done,
        health: IssueHealth.Done,
        completedAt: yesterdayIso,
        updatedAt: yesterdayIso,
      }),
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active, updatedAt: todayIso }),
    ]

    expect(deriveFactoryStatus(issues).shippedToday).toBe(1)
  })

  it('does not count a done issue without completedAt as shippedToday', () => {
    expect(
      deriveFactoryStatus([makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done, updatedAt: todayIso })])
        .shippedToday,
    ).toBe(0)
  })

  it('returns all issue and cost fields together for a mixed input', () => {
    const issues: Issue[] = [
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
      makeIssue({ approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ approvalState: { status: 'awaiting', requestedAt: todayIso } }),
      makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
      makeIssue({ status: IssueStatus.Done, health: IssueHealth.Done, completedAt: todayIso, updatedAt: todayIso }),
      makeIssue({
        status: IssueStatus.Done,
        health: IssueHealth.Done,
        completedAt: yesterdayIso,
        updatedAt: yesterdayIso,
      }),
    ]

    expect(deriveFactoryStatus(issues)).toEqual({
      inFlight: 2,
      awaitingApproval: 2,
      shippedToday: 2,
      todayCost: undefined,
    })
  })

  it('threads a populated todayCost metric through without collapsing sampleCount', () => {
    const metric = makeTodayCost({ amount: 4.2, currency: 'USD', sampleCount: 7 })
    expect(deriveFactoryStatus([], metric).todayCost).toEqual({ amount: 4.2, currency: 'USD', sampleCount: 7 })
  })

  it('preserves a real zero todayCost', () => {
    const metric = makeTodayCost({ amount: 0, currency: 'USD', sampleCount: 3 })
    expect(deriveFactoryStatus([], metric).todayCost).toEqual({ amount: 0, currency: 'USD', sampleCount: 3 })
  })

  it('keeps an empty todayCost metric distinct from a real zero', () => {
    const metric = makeTodayCost({ amount: null, currency: null, sampleCount: 0 })
    expect(deriveFactoryStatus([], metric).todayCost).toEqual({ amount: null, currency: null, sampleCount: 0 })
  })
})
