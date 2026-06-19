import { describe, expect, it } from 'vitest'
import type { AgentStatus } from '../../agent'
import { IssueHealth, IssueStatus, WorkflowStage, type Issue } from '..'
import { deriveAttentionItems, isIntegrateFailure, type AttentionItem } from './attention'

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

const NO_AGENT = makeAgentStatus()

describe('isIntegrateFailure', () => {
  it('returns true for an Integrate-stage Blocked issue', () => {
    expect(isIntegrateFailure(makeIssue({
      id: 'i-1',
      workflowStage: WorkflowStage.Integrate,
      health: IssueHealth.Blocked,
    }))).toBe(true)
  })

  it('returns true for an Integrate-stage Interrupted issue', () => {
    expect(isIntegrateFailure(makeIssue({
      id: 'i-2',
      workflowStage: WorkflowStage.Integrate,
      health: IssueHealth.Interrupted,
    }))).toBe(true)
  })

  it('returns false for a Blocked issue in a non-integrate stage', () => {
    expect(isIntegrateFailure(makeIssue({
      id: 'i-3',
      workflowStage: WorkflowStage.Build,
      health: IssueHealth.Blocked,
    }))).toBe(false)
  })

  it('returns false for an Interrupted issue in a non-integrate stage', () => {
    expect(isIntegrateFailure(makeIssue({
      id: 'i-4',
      workflowStage: WorkflowStage.Check,
      health: IssueHealth.Interrupted,
    }))).toBe(false)
  })

  it('returns false for an Integrate-stage Active issue', () => {
    expect(isIntegrateFailure(makeIssue({
      id: 'i-5',
      workflowStage: WorkflowStage.Integrate,
      health: IssueHealth.Active,
    }))).toBe(false)
  })

  it('returns false for an Integrate-stage issue with no workflowStage (null)', () => {
    expect(isIntegrateFailure(makeIssue({
      id: 'i-6',
      workflowStage: null,
      health: IssueHealth.Blocked,
    }))).toBe(false)
  })
})

describe('deriveAttentionItems — approval-pending rule', () => {
  it('surfaces an awaiting approval as "Approval needed" with title as detail', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'await-1',
        number: 11,
        title: 'Wait for review',
        approvalState: {
          status: 'awaiting',
          requestedAt: '2026-06-18T00:00:00.000Z',
        },
      }),
    ], NO_AGENT)

    expect(items).toEqual([{
      issueNumber: 11,
      issueId: 'await-1',
      label: 'Approval needed',
      detail: 'Wait for review',
    }])
  })

  it('does not surface an approval with a status other than awaiting', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'pending-1',
        approvalState: {
          status: 'pending',
          requestedAt: '2026-06-18T00:00:00.000Z',
        },
      }),
      makeIssue({
        id: 'approved-1',
        health: IssueHealth.Active,
        approvalState: {
          status: 'approved',
          requestedAt: '2026-06-18T00:00:00.000Z',
          respondedAt: '2026-06-18T01:00:00.000Z',
        },
      }),
    ], NO_AGENT)

    expect(items).toEqual([])
  })
})

describe('deriveAttentionItems — integrate-failure rule', () => {
  it('surfaces an Integrate-stage Blocked issue as "Integration failed"', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'int-block-1',
        number: 21,
        title: 'Failed merge',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
    ], NO_AGENT)

    expect(items).toEqual([{
      issueNumber: 21,
      issueId: 'int-block-1',
      label: 'Integration failed',
      detail: 'Failed merge',
    }])
  })

  it('surfaces an Integrate-stage Interrupted issue as "Integration failed"', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'int-int-1',
        number: 22,
        title: 'Merge aborted',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Interrupted,
      }),
    ], NO_AGENT)

    expect(items).toEqual([{
      issueNumber: 22,
      issueId: 'int-int-1',
      label: 'Integration failed',
      detail: 'Merge aborted',
    }])
  })
})

describe('deriveAttentionItems — interrupted rule (non-integrate)', () => {
  it('surfaces a non-integrate Interrupted issue as "Interrupted"', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'int-build-1',
        number: 31,
        title: 'Build halted',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Interrupted,
      }),
    ], NO_AGENT)

    expect(items).toEqual([{
      issueNumber: 31,
      issueId: 'int-build-1',
      label: 'Interrupted',
      detail: 'Build halted',
    }])
  })
})

describe('deriveAttentionItems — blocked rule with blockedReason fallback', () => {
  it('uses blockedReason as detail when present on a non-integrate Blocked issue', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'blk-1',
        number: 41,
        title: 'Stuck on auth bug',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'Cannot reach auth provider',
      }),
    ], NO_AGENT)

    expect(items).toEqual([{
      issueNumber: 41,
      issueId: 'blk-1',
      label: 'Needs action',
      detail: 'Cannot reach auth provider',
    }])
  })

  it('falls back to title when blockedReason is absent on a non-integrate Blocked issue', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'blk-2',
        number: 42,
        title: 'Mystery blocker',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
      }),
    ], NO_AGENT)

    expect(items).toEqual([{
      issueNumber: 42,
      issueId: 'blk-2',
      label: 'Needs action',
      detail: 'Mystery blocker',
    }])
  })
})

describe('deriveAttentionItems — first match wins', () => {
  it('uses the first matching rule when an issue satisfies multiple rules (approval before blocked)', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'multi-1',
        number: 51,
        title: 'Both awaiting and blocked',
        health: IssueHealth.Blocked,
        blockedReason: 'some reason',
        approvalState: {
          status: 'awaiting',
          requestedAt: '2026-06-18T00:00:00.000Z',
        },
      }),
    ], NO_AGENT)

    expect(items).toHaveLength(1)
    expect(items[0]).toMatchObject({
      issueId: 'multi-1',
      label: 'Approval needed',
    })
    expect(items[0]?.detail).toBe('Both awaiting and blocked')
  })

  it('uses the integrate-failure rule (over plain Interrupted) when an issue is Integrate-stage Interrupted', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'multi-2',
        number: 52,
        title: 'Merge interrupted',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Interrupted,
      }),
    ], NO_AGENT)

    expect(items).toHaveLength(1)
    expect(items[0]?.label).toBe('Integration failed')
  })

  it('uses the integrate-failure rule (over plain Blocked) when an issue is Integrate-stage Blocked', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'multi-3',
        number: 53,
        title: 'Merge blocked',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
        blockedReason: 'conflict on README',
      }),
    ], NO_AGENT)

    expect(items).toHaveLength(1)
    expect(items[0]?.label).toBe('Integration failed')
    expect(items[0]?.detail).toBe('Merge blocked')
  })
})

describe('deriveAttentionItems — dedup by issue id', () => {
  it('emits a single AttentionItem when the same id appears more than once', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'dup-1',
        number: 61,
        title: 'First sighting',
        health: IssueHealth.Blocked,
        blockedReason: 'a',
      }),
      makeIssue({
        id: 'dup-1',
        number: 61,
        title: 'Second sighting',
        health: IssueHealth.Blocked,
        blockedReason: 'b',
      }),
    ], NO_AGENT)

    expect(items).toHaveLength(1)
    expect(items[0]?.issueId).toBe('dup-1')
  })

  it('emits a single AttentionItem when the same id appears more than once across different rule matches', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'dup-2',
        number: 62,
        title: 'Earlier Blocked',
        health: IssueHealth.Blocked,
        blockedReason: 'first',
      }),
      makeIssue({
        id: 'dup-2',
        number: 62,
        title: 'Later awaiting approval',
        health: IssueHealth.Active,
        approvalState: {
          status: 'awaiting',
          requestedAt: '2026-06-18T00:00:00.000Z',
        },
      }),
    ], NO_AGENT)

    expect(items).toHaveLength(1)
    expect(items[0]?.issueId).toBe('dup-2')
  })
})

describe('deriveAttentionItems — all-healthy input', () => {
  it('returns an empty array when no issue matches any rule', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'healthy-1',
        number: 71,
        title: 'Healthy build',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
      makeIssue({
        id: 'healthy-2',
        number: 72,
        title: 'Healthy done',
        workflowStage: WorkflowStage.Done,
        health: IssueHealth.Done,
      }),
      makeIssue({
        id: 'healthy-3',
        number: 73,
        title: 'Healthy paused',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Paused,
      }),
      makeIssue({
        id: 'healthy-4',
        number: 74,
        title: 'Healthy cancelled',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Cancelled,
      }),
    ], NO_AGENT)

    expect(items).toEqual([])
  })

  it('returns an empty array for an empty input list', () => {
    expect(deriveAttentionItems([], NO_AGENT)).toEqual([])
  })
})

describe('deriveAttentionItems — output typing and signature', () => {
  it('returns an AttentionItem[]', () => {
    const items: AttentionItem[] = deriveAttentionItems([
      makeIssue({
        id: 'typed-1',
        number: 81,
        title: 'Type check',
        health: IssueHealth.Blocked,
        blockedReason: 'reason',
      }),
    ], NO_AGENT)

    expect(Array.isArray(items)).toBe(true)
    expect(items[0]?.label).toBe('Needs action')
  })

  it('preserves the AgentStatus parameter (the rule currently ignores it)', () => {
    const busyAgent = makeAgentStatus({
      running: true,
      issueId: 'typed-1',
      issueNumber: 81,
    })

    const items = deriveAttentionItems([
      makeIssue({
        id: 'typed-2',
        number: 82,
        title: 'Should still surface regardless of agent status',
        health: IssueHealth.Blocked,
        blockedReason: 'reason',
      }),
    ], busyAgent)

    expect(items).toHaveLength(1)
    expect(items[0]?.label).toBe('Needs action')
  })
})