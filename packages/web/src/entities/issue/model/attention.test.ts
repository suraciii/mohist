import { describe, expect, it } from 'vitest'
import type { AgentStatus } from '../../agent'
import { deriveAttentionItems, isIssueAttentionItem, type AttentionItem } from '../../agent-ops'
import { IssueHealth, IssueStatus, WorkflowStage, type Issue } from '..'
import { classifyIssueAttention, isIntegrateFailure, issueNeedsOwnerAction } from './attention'
function makeAgentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    running: false,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 1 },
    ...overrides,
  }
}

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

const NO_AGENT = makeAgentStatus()

describe('isIntegrateFailure', () => {
  it('returns true for an Integrate-stage Blocked issue', () => {
    expect(
      isIntegrateFailure(
        makeIssue({
          workflowStage: WorkflowStage.Integrate,
          health: IssueHealth.Blocked,
        }),
      ),
    ).toBe(true)
  })

  it('returns false for a Blocked issue in a non-integrate stage', () => {
    expect(
      isIntegrateFailure(
        makeIssue({
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
        }),
      ),
    ).toBe(false)
  })

  it('returns false for an Integrate-stage Active issue', () => {
    expect(
      isIntegrateFailure(
        makeIssue({
          workflowStage: WorkflowStage.Integrate,
          health: IssueHealth.Active,
        }),
      ),
    ).toBe(false)
  })

  it('returns false for an Integrate-stage issue with no workflowStage (null)', () => {
    expect(
      isIntegrateFailure(
        makeIssue({
          workflowStage: null,
          health: IssueHealth.Blocked,
        }),
      ),
    ).toBe(false)
  })
})

describe('deriveAttentionItems — recoverable interruption rule', () => {
  it('surfaces a recoverable interruption with reason and deadline instead of failure treatment', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 17,
          status: IssueStatus.InProgress,
          workflowStage: WorkflowStage.Build,
          attention: {
            reason: 'recoverable-interrupted',
            state: 'recoverable-interrupted',
            reasonCode: 'runner-lost',
            recoveryDeadlineAt: '2026-08-15T01:15:00Z',
          },
        }),
      ],
      NO_AGENT,
    )

    expect(items).toEqual([
      {
        kind: 'recoverable-interrupted',
        issueNumber: 17,
        label: 'Recoverable interruption',
        detail: 'runner-lost; recovery deadline 2026-08-15T01:15:00Z',
      },
    ])
  })
})

describe('deriveAttentionItems — approval-pending rule', () => {
  it('surfaces an awaiting approval as "Approval needed" with title as detail', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 11,
          title: 'Wait for review',
          approvalState: {
            status: 'awaiting',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
      ],
      NO_AGENT,
    )

    expect(items).toEqual([
      {
        kind: 'approval-needed',
        issueNumber: 11,
        label: 'Approval needed',
        detail: 'Wait for review',
      },
    ])
  })

  it('does not surface an approval with a status other than awaiting', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          approvalState: {
            status: 'pending',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
        makeIssue({
          health: IssueHealth.Active,
          approvalState: {
            status: 'approved',
            requestedAt: '2026-06-18T00:00:00.000Z',
            respondedAt: '2026-06-18T01:00:00.000Z',
          },
        }),
      ],
      NO_AGENT,
    )

    expect(items).toEqual([])
  })
})

describe('deriveAttentionItems — integrate-failure rule', () => {
  it('surfaces an Integrate-stage Blocked issue as "Integration failed"', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 21,
          title: 'Failed merge',
          workflowStage: WorkflowStage.Integrate,
          health: IssueHealth.Blocked,
        }),
      ],
      NO_AGENT,
    )

    expect(items).toEqual([
      {
        kind: 'integration-failed',
        issueNumber: 21,
        label: 'Integration failed',
        detail: 'Failed merge',
      },
    ])
  })
})

describe('deriveAttentionItems — blocked rule with blockedReason fallback', () => {
  it('uses blockedReason as detail when present on a non-integrate Blocked issue', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 41,
          title: 'Stuck on auth bug',
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
          blockedReason: 'Cannot reach auth provider',
        }),
      ],
      NO_AGENT,
    )

    expect(items).toEqual([
      {
        kind: 'blocked',
        issueNumber: 41,
        label: 'Needs action',
        detail: 'Cannot reach auth provider',
      },
    ])
  })

  it('falls back to title when blockedReason is absent on a non-integrate Blocked issue', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 42,
          title: 'Mystery blocker',
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
        }),
      ],
      NO_AGENT,
    )

    expect(items).toEqual([
      {
        kind: 'blocked',
        issueNumber: 42,
        label: 'Needs action',
        detail: 'Mystery blocker',
      },
    ])
  })
})

describe('deriveAttentionItems — first match wins', () => {
  it('uses the first matching rule when an issue satisfies multiple rules (approval before blocked)', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 51,
          title: 'Both awaiting and blocked',
          health: IssueHealth.Blocked,
          blockedReason: 'some reason',
          approvalState: {
            status: 'awaiting',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
      ],
      NO_AGENT,
    )

    expect(items).toHaveLength(1)
    expect(items[0]).toMatchObject({
      kind: 'approval-needed',
      label: 'Approval needed',
    })
    expect(items[0]?.detail).toBe('Both awaiting and blocked')
  })

  it('uses the integrate-failure rule (over plain Blocked) when an issue is Integrate-stage Blocked', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 53,
          title: 'Merge blocked',
          workflowStage: WorkflowStage.Integrate,
          health: IssueHealth.Blocked,
          blockedReason: 'conflict on README',
        }),
      ],
      NO_AGENT,
    )

    expect(items).toHaveLength(1)
    expect(items[0]?.label).toBe('Integration failed')
    expect(items[0]?.detail).toBe('Merge blocked')
  })
})

describe('deriveAttentionItems — dedup by project and issue number', () => {
  it('emits a single AttentionItem when the same number appears more than once', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 61,
          title: 'First sighting',
          health: IssueHealth.Blocked,
          blockedReason: 'a',
        }),
        makeIssue({
          number: 61,
          title: 'Second sighting',
          health: IssueHealth.Blocked,
          blockedReason: 'b',
        }),
      ],
      NO_AGENT,
    )

    expect(items).toHaveLength(1)
    expect(items[0]).toMatchObject({ kind: 'blocked' })
  })

  it('emits a single AttentionItem when the same number appears more than once across different rule matches', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 62,
          title: 'Earlier Blocked',
          health: IssueHealth.Blocked,
          blockedReason: 'first',
        }),
        makeIssue({
          number: 62,
          title: 'Later awaiting approval',
          health: IssueHealth.Active,
          approvalState: {
            status: 'awaiting',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
      ],
      NO_AGENT,
    )

    expect(items).toHaveLength(1)
    expect(items[0]).toMatchObject({ kind: 'blocked' })
  })
})

describe('deriveAttentionItems — all-healthy input', () => {
  it('returns an empty array when no issue matches any rule', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 71,
          title: 'Healthy build',
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Active,
        }),
        makeIssue({
          number: 72,
          title: 'Healthy done',
          workflowStage: WorkflowStage.Done,
          health: IssueHealth.Done,
        }),
        makeIssue({
          number: 73,
          title: 'Healthy paused',
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Paused,
        }),
        makeIssue({
          number: 74,
          title: 'Healthy cancelled',
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Cancelled,
        }),
      ],
      NO_AGENT,
    )

    expect(items).toEqual([])
  })

  it('returns an empty array for an empty input list with no runner signal', () => {
    expect(deriveAttentionItems([], NO_AGENT)).toEqual([])
  })
})

describe('deriveAttentionItems — output typing and signature', () => {
  it('returns an AttentionItem[]', () => {
    const items: AttentionItem[] = deriveAttentionItems(
      [
        makeIssue({
          number: 81,
          title: 'Type check',
          health: IssueHealth.Blocked,
          blockedReason: 'reason',
        }),
      ],
      NO_AGENT,
    )

    expect(Array.isArray(items)).toBe(true)
    expect(items[0]?.kind).toBe('blocked')
    expect(items[0]?.label).toBe('Needs action')
  })

  it('adds a runner item only when an active workflow is affected', () => {
    const busyAgent = makeAgentStatus({
      runnerAvailable: false,
      capacity: { active: 0, max: 1 },
    })

    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 82,
          title: 'No longer ignored',
          health: IssueHealth.Blocked,
          blockedReason: 'reason',
        }),
        makeIssue({
          number: 83,
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
        }),
      ],
      busyAgent,
    )

    expect(items).toHaveLength(2)
    expect(items[0]).toMatchObject({ kind: 'blocked' })
    expect(items[1]).toMatchObject({ kind: 'runner-unavailable' })
  })
})

describe('deriveAttentionItems — runner-unavailable rule', () => {
  it('does not emit runner-unavailable without an affected workflow', () => {
    const items = deriveAttentionItems(
      [],
      makeAgentStatus({
        runnerAvailable: false,
        runnerMessage: 'Embedded runner is offline',
      }),
    )

    expect(items).toEqual([])
  })

  it('falls back to "No runner is connected." when runnerMessage is null', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
        }),
      ],
      makeAgentStatus({
        runnerAvailable: false,
        runnerMessage: null,
      }),
    )

    expect(items).toEqual([
      {
        kind: 'runner-unavailable',
        label: 'Runner unavailable',
        detail: 'No runner is connected.',
      },
    ])
  })

  it('falls back to "No runner is connected." when runnerMessage is undefined', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
        }),
      ],
      makeAgentStatus({
        runnerAvailable: false,
      }),
    )

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
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 11,
          status: IssueStatus.InProgress,
          approvalState: {
            status: 'awaiting',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
      ],
      makeAgentStatus({ runnerAvailable: false, runnerMessage: 'down' }),
    )

    expect(items).toHaveLength(2)
    expect(items[0]).toMatchObject({ kind: 'approval-needed' })
    expect(items[1]).toMatchObject({ kind: 'runner-unavailable' })
  })
})

describe('deriveAttentionItems — runner-capacity-limited rule', () => {
  it('emits runner-capacity-limited when max > 0 and active >= max', () => {
    const items = deriveAttentionItems(
      [],
      makeAgentStatus({
        runnerAvailable: true,
        capacity: { active: 4, max: 4 },
      }),
    )

    expect(items).toEqual([
      {
        kind: 'runner-capacity-limited',
        label: 'Runner at capacity',
        detail: '4 of 4 slots in use',
      },
    ])
  })

  it('emits runner-capacity-limited when active > max (overflow)', () => {
    const items = deriveAttentionItems(
      [],
      makeAgentStatus({
        runnerAvailable: true,
        capacity: { active: 5, max: 4 },
      }),
    )

    expect(items[0]).toMatchObject({ kind: 'runner-capacity-limited' })
  })

  it('does NOT emit runner-capacity-limited when active < max', () => {
    const items = deriveAttentionItems(
      [],
      makeAgentStatus({
        runnerAvailable: true,
        capacity: { active: 2, max: 4 },
      }),
    )

    expect(items).toEqual([])
  })

  it('does NOT emit runner-capacity-limited when max === 0', () => {
    const items = deriveAttentionItems(
      [],
      makeAgentStatus({
        runnerAvailable: true,
        capacity: { active: 0, max: 0 },
      }),
    )

    expect(items).toEqual([])
  })

  it('does NOT emit runner-capacity-limited when max > 0 but active === 0', () => {
    const items = deriveAttentionItems(
      [],
      makeAgentStatus({
        runnerAvailable: true,
        capacity: { active: 0, max: 2 },
      }),
    )

    expect(items).toEqual([])
  })

  it('emits runner-capacity-limited after issue items, both present', () => {
    const items = deriveAttentionItems(
      [
        makeIssue({
          number: 20,
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
          blockedReason: 'r',
        }),
      ],
      makeAgentStatus({
        runnerAvailable: true,
        capacity: { active: 8, max: 8 },
      }),
    )

    expect(items).toHaveLength(2)
    expect(items[0]).toMatchObject({ kind: 'blocked' })
    expect(items[1]).toMatchObject({ kind: 'runner-capacity-limited' })
  })
})

describe('deriveAttentionItems — runner-unavailable suppresses capacity-limited', () => {
  it('emits no infrastructure warning when no workflow is affected', () => {
    const items = deriveAttentionItems(
      [],
      makeAgentStatus({
        runnerAvailable: false,
        capacity: { active: 4, max: 4 },
      }),
    )

    expect(items).toEqual([])
  })
})

describe('deriveAttentionItems — union is exhaustive', () => {
  it('the kind set is exactly the union of issue and runner kinds (no others)', () => {
    const issueKinds = new Set(['approval-needed', 'integration-failed', 'recoverable-interrupted', 'blocked'])
    const runnerKinds = new Set(['runner-unavailable', 'runner-capacity-limited'])

    const samples: Array<{ input: AttentionItem[]; expected: Set<string> }> = [
      { input: [], expected: new Set() },
      {
        input: [
          {
            kind: 'approval-needed',
            issueNumber: 1,
            label: 'A',
            detail: 'd',
          },
        ],
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
      'recoverable-interrupted',
      'runner-capacity-limited',
      'runner-unavailable',
    ])
  })
})

describe('isIssueAttentionItem', () => {
  it('narrows issue attention items and excludes runner attention items', () => {
    const issueItem: AttentionItem = {
      kind: 'approval-needed',
      issueNumber: 1,
      label: 'Approval needed',
    }
    const runnerItem: AttentionItem = {
      kind: 'runner-capacity-limited',
      label: 'Runner at capacity',
    }

    expect(isIssueAttentionItem(issueItem)).toBe(true)
    expect(isIssueAttentionItem(runnerItem)).toBe(false)
  })
})

describe('issueNeedsOwnerAction', () => {
  it('returns true for an awaiting-approval issue', () => {
    expect(
      issueNeedsOwnerAction(
        makeIssue({
          approvalState: {
            status: 'awaiting',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
      ),
    ).toBe(true)
  })

  it('returns true for an Integrate-stage Blocked issue (integration-failed)', () => {
    expect(
      issueNeedsOwnerAction(
        makeIssue({
          workflowStage: WorkflowStage.Integrate,
          health: IssueHealth.Blocked,
        }),
      ),
    ).toBe(true)
  })

  it('returns true for a non-integrate Blocked issue', () => {
    expect(
      issueNeedsOwnerAction(
        makeIssue({
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
          blockedReason: 'stuck',
        }),
      ),
    ).toBe(true)
  })

  it('returns false for a healthy in-progress issue', () => {
    expect(
      issueNeedsOwnerAction(
        makeIssue({
          status: IssueStatus.InProgress,
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Active,
        }),
      ),
    ).toBe(false)
  })

  it('returns false for a paused issue', () => {
    expect(
      issueNeedsOwnerAction(
        makeIssue({
          status: IssueStatus.InProgress,
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Paused,
        }),
      ),
    ).toBe(false)
  })

  it('returns false for a pending or approved approval-state issue', () => {
    expect(
      issueNeedsOwnerAction(
        makeIssue({
          approvalState: {
            status: 'pending',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
      ),
    ).toBe(false)

    expect(
      issueNeedsOwnerAction(
        makeIssue({
          approvalState: {
            status: 'approved',
            requestedAt: '2026-06-18T00:00:00.000Z',
            respondedAt: '2026-06-18T01:00:00.000Z',
          },
        }),
      ),
    ).toBe(false)
  })

  it('returns true for an issue that is both awaiting approval and blocked (the cue must agree with attention classification)', () => {
    expect(
      issueNeedsOwnerAction(
        makeIssue({
          status: IssueStatus.InProgress,
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
          blockedReason: 'merge lock',
          approvalState: {
            status: 'awaiting',
            requestedAt: '2026-06-18T00:00:00.000Z',
          },
        }),
      ),
    ).toBe(true)
  })

  it('uses the same issue classifier that feeds deriveAttentionItems', () => {
    const issue = makeIssue({
      number: 104,
      title: 'Classifier source',
      workflowStage: WorkflowStage.Build,
      health: IssueHealth.Blocked,
      blockedReason: 'blocked by owner decision',
    })

    const classified = classifyIssueAttention(issue)
    expect(classified).toEqual({
      kind: 'blocked',
      issueNumber: 104,
      label: 'Needs action',
      detail: 'blocked by owner decision',
    })
    expect(issueNeedsOwnerAction(issue)).toBe(true)
    expect(deriveAttentionItems([issue], NO_AGENT)).toEqual([classified])
  })

  it('stays in lock-step with deriveAttentionItems — every issue producing an issue kind returns true here', () => {
    const samples: Issue[] = [
      makeIssue({
        number: 100,
        title: 'awaiting',
        approvalState: {
          status: 'awaiting',
          requestedAt: '2026-06-18T00:00:00.000Z',
        },
      }),
      makeIssue({
        number: 101,
        title: 'integrate-blocked',
        workflowStage: WorkflowStage.Integrate,
        health: IssueHealth.Blocked,
      }),
      makeIssue({
        number: 102,
        title: 'build-blocked-without-reason',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
      }),
      makeIssue({
        number: 103,
        title: 'build-blocked',
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Blocked,
        blockedReason: 'reason',
      }),
    ]
    const runnerOnlyAgent = makeAgentStatus({ runnerAvailable: true })
    const attentionItems = deriveAttentionItems(samples, runnerOnlyAgent)
    const attentionIssueNumbers = new Set(attentionItems.filter(isIssueAttentionItem).map((item) => item.issueNumber))

    for (const sample of samples) {
      const flagged = issueNeedsOwnerAction(sample)
      const producesItem = attentionIssueNumbers.has(sample.number)
      expect({ flagged, producesItem }).toEqual({ flagged: true, producesItem: true })
    }
  })

  it('stays in lock-step with deriveAttentionItems — healthy issues flag false and produce no item', () => {
    const healthy: Issue[] = [
      makeIssue({
        number: 200,
        status: IssueStatus.InProgress,
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
      makeIssue({
        number: 201,
        status: IssueStatus.InProgress,
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Paused,
      }),
    ]

    for (const sample of healthy) {
      expect(issueNeedsOwnerAction(sample)).toBe(false)
    }

    const items = deriveAttentionItems(healthy, makeAgentStatus({ runnerAvailable: true }))
    expect(items).toEqual([])
  })
})
