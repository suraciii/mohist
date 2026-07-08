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
    expect(items[0]).toMatchObject({ kind: 'blocked', issueId: 'dup-1' })
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
    expect(items[0]).toMatchObject({ kind: 'blocked', issueId: 'dup-2' })
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

  it('returns an empty array for an empty input list with no runner signal', () => {
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

  it('consumes the AgentStatus parameter (now produces runner items)', () => {
    const busyAgent = makeAgentStatus({
      runnerAvailable: false,
      capacity: { active: 0, max: 1 },
    })

    const items = deriveAttentionItems([
      makeIssue({
        id: 'typed-2',
        number: 82,
        title: 'No longer ignored',
        health: IssueHealth.Blocked,
        blockedReason: 'reason',
      }),
    ], busyAgent)

    expect(items).toHaveLength(2)
    expect(items[0]).toMatchObject({ kind: 'blocked', issueId: 'typed-2' })
    expect(items[1]).toMatchObject({ kind: 'runner-unavailable' })
  })
})

describe('deriveAttentionItems — runner-unavailable rule', () => {
  it('emits runner-unavailable when runnerAvailable is false, even with an empty issue list', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: false,
      runnerMessage: 'Embedded runner is offline',
    }))

    expect(items).toEqual([{
      kind: 'runner-unavailable',
      label: 'Runner unavailable',
      detail: 'Embedded runner is offline',
    }])
  })

  it('falls back to "No runner is connected." when runnerMessage is null', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: false,
      runnerMessage: null,
    }))

    expect(items).toEqual([{
      kind: 'runner-unavailable',
      label: 'Runner unavailable',
      detail: 'No runner is connected.',
    }])
  })

  it('falls back to "No runner is connected." when runnerMessage is undefined', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: false,
    }))

    expect(items[0]).toMatchObject({
      kind: 'runner-unavailable',
      detail: 'No runner is connected.',
    })
  })

  it('does NOT emit runner-unavailable when runnerAvailable is true', () => {
    const items = deriveAttentionItems([], makeAgentStatus({ runnerAvailable: true }))
    expect(items).toEqual([])
  })

  it('does NOT emit runner-unavailable when runnerAvailable is undefined (treated as runner-up)', () => {
    const items = deriveAttentionItems([], makeAgentStatus({}))
    expect(items).toEqual([])
  })

  it('emits runner-unavailable after issue items, both present', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'await-1',
        number: 11,
        approvalState: {
          status: 'awaiting',
          requestedAt: '2026-06-18T00:00:00.000Z',
        },
      }),
    ], makeAgentStatus({ runnerAvailable: false, runnerMessage: 'down' }))

    expect(items).toHaveLength(2)
    expect(items[0]).toMatchObject({ kind: 'approval-needed' })
    expect(items[1]).toMatchObject({ kind: 'runner-unavailable' })
  })
})

describe('deriveAttentionItems — runner-capacity-limited rule', () => {
  it('emits runner-capacity-limited when max > 0 and active >= max', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 4, max: 4 },
    }))

    expect(items).toEqual([{
      kind: 'runner-capacity-limited',
      label: 'Runner at capacity',
      detail: '4 of 4 slots in use',
    }])
  })

  it('emits runner-capacity-limited when active > max (overflow)', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 5, max: 4 },
    }))

    expect(items[0]).toMatchObject({ kind: 'runner-capacity-limited' })
  })

  it('does NOT emit runner-capacity-limited when active < max', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 2, max: 4 },
    }))

    expect(items).toEqual([])
  })

  it('does NOT emit runner-capacity-limited when max === 0', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 0, max: 0 },
    }))

    expect(items).toEqual([])
  })

  it('does NOT emit runner-capacity-limited when max > 0 but active === 0', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 0, max: 2 },
    }))

    expect(items).toEqual([])
  })

  it('emits runner-capacity-limited after issue items, both present', () => {
    const items = deriveAttentionItems([
      makeIssue({
        id: 'blocked-1',
        number: 20,
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'r',
      }),
    ], makeAgentStatus({
      runnerAvailable: true,
      capacity: { active: 8, max: 8 },
    }))

    expect(items).toHaveLength(2)
    expect(items[0]).toMatchObject({ kind: 'blocked' })
    expect(items[1]).toMatchObject({ kind: 'runner-capacity-limited' })
  })
})

describe('deriveAttentionItems — runner-unavailable suppresses capacity-limited', () => {
  it('emits only runner-unavailable when runnerAvailable is false even if at capacity', () => {
    const items = deriveAttentionItems([], makeAgentStatus({
      runnerAvailable: false,
      capacity: { active: 4, max: 4 },
    }))

    expect(items).toHaveLength(1)
    expect(items[0]?.kind).toBe('runner-unavailable')
  })
})

describe('deriveAttentionItems — union is exhaustive', () => {
  it('the kind set is exactly the union of issue and runner kinds (no others)', () => {
    const issueKinds = new Set([
      'approval-needed',
      'integration-failed',
      'interrupted',
      'blocked',
    ])
    const runnerKinds = new Set([
      'runner-unavailable',
      'runner-capacity-limited',
    ])

    const samples: Array<{ input: AttentionItem[]; expected: Set<string> }> = [
      { input: [], expected: new Set() },
      {
        input: [{
          kind: 'approval-needed',
          issueId: 'a',
          issueNumber: 1,
          label: 'A',
          detail: 'd',
        }],
        expected: new Set(['approval-needed']),
      },
      {
        input: [{ kind: 'runner-unavailable', label: 'X' }],
        expected: new Set(['runner-unavailable']),
      },
      {
        input: [{ kind: 'runner-capacity-limited', label: 'X' }],
        expected: new Set(['runner-capacity-limited']),
      },
    ]

    for (const sample of samples) {
      const seen = new Set(sample.input.map((i) => i.kind))
      for (const k of seen) {
        const inAny = issueKinds.has(k) || runnerKinds.has(k)
        expect(inAny).toBe(true)
      }
      expect([...seen].sort()).toEqual([...sample.expected].sort())
    }

    expect([...issueKinds, ...runnerKinds].sort()).toEqual([
      'approval-needed',
      'blocked',
      'integration-failed',
      'interrupted',
      'runner-capacity-limited',
      'runner-unavailable',
    ])
  })
})
