import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue/@x/types'
import { EpicStatus } from '../../../entities/epic/model/types'
import type { LinkedIssue } from '../../../entities/epic/model/types'
import {
  advancementCopy,
  ADVANCEMENT_STATE_KINDS,
  deriveAdvancementState,
  type AdvancementState,
} from './advancement'

function makeLinkedIssue(overrides: Partial<LinkedIssue> = {}): LinkedIssue {
  return {
    number: 1,
    title: 'Default issue',
    status: IssueStatus.Backlog,
    stage: WorkflowStage.Plan,
    health: IssueHealth.Active,
    priority: 'p2',
    canStart: true,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    ...overrides,
  }
}

describe('deriveAdvancementState', () => {
  describe('returns waiting-for-in-progress when any linked issue is in progress', () => {
    it('identifies the in-progress issue and exposes its number for nav', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({ number: 7, status: IssueStatus.InProgress, stage: WorkflowStage.Build }),
          makeLinkedIssue({ number: 8, status: IssueStatus.Backlog, canStart: true, startBlocker: null }),
        ],
      })
      expect(state).toEqual({ kind: 'waiting-for-in-progress', issueNumber: 7, })
    })

    it('prefers in-progress over startable backlog when both exist', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({ number: 7, status: IssueStatus.Backlog, canStart: true, startBlocker: null }),
          makeLinkedIssue({ number: 11, status: IssueStatus.InProgress, stage: WorkflowStage.Build }),
        ],
      })
      expect(state).toEqual({ kind: 'waiting-for-in-progress', issueNumber: 11, })
    })

    it('prefers in-progress over draft blocker for the candidate', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({ number: 7, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } }),
          makeLinkedIssue({ number: 11, status: IssueStatus.InProgress, stage: WorkflowStage.Build }),
        ],
      })
      expect(state).toEqual({ kind: 'waiting-for-in-progress', issueNumber: 11, })
    })
  })

  describe('returns nothing-pending when every linked issue is delivered', () => {
    it('treats all-done as nothing-pending', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({ number: 7, status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, canStart: false }),
          makeLinkedIssue({ number: 8, status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, canStart: false }),
        ],
      })
      expect(state).toEqual({ kind: 'nothing-pending' })
    })

    it('does not describe all-cancelled issues as delivered', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({ number: 7, status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
        ],
      })
      expect(state).toEqual({ kind: 'idle-no-next', reason: 'no startable issue' })
    })

    it('does not collapse mixed done and cancelled issues into delivered wording', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({ number: 7, status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, canStart: false }),
          makeLinkedIssue({ number: 8, status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
        ],
      })
      expect(state).toEqual({ kind: 'idle-no-next', reason: 'no startable issue' })
    })

    it('returns nothing-pending for an empty linked-issues list', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [],
      })
      expect(state).toEqual({ kind: 'nothing-pending' })
    })
  })

  describe('returns draft-blocker when the candidate is undelivered and in draft', () => {
    it('identifies the draft candidate number', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({ number: 14, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } }),
        ],
      })
      expect(state).toEqual({ kind: 'draft-blocker', issueNumber: 14, })
    })
  })

  describe('returns external-prerequisite-blocker when the candidate has external prerequisites', () => {
    it('exposes the candidate number and the prerequisite numbers for nav', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({
            number: 21,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: null,
            externalPrerequisites: [
              { number: 100, title: 'Upstream A', stage: 'build', status: 'done' },
              { number: 200, title: 'Upstream B', stage: 'plan', status: 'backlog' },
            ],
          }),
        ],
      })
      expect(state).toEqual({
        kind: 'external-prerequisite-blocker',
        issueNumber: 21,
        prerequisiteNumbers: [100, 200],
      })
    })

    it('handles a single external prerequisite (length=1)', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({
            number: 5,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: null,
            externalPrerequisites: [{ number: 77, title: 'Only Prereq', stage: 'plan', status: 'backlog' }],
          }),
        ],
      })
      expect(state).toEqual({
        kind: 'external-prerequisite-blocker',
        issueNumber: 5,
        prerequisiteNumbers: [77],
      })
    })
  })

  describe('returns has-next when a candidate is startable (backlog + canStart + no blocker)', () => {
    it('identifies the startable candidate number', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({ number: 42, status: IssueStatus.Backlog, canStart: true, startBlocker: null }),
        ],
      })
      expect(state).toEqual({ kind: 'has-next', issueNumber: 42, })
    })

    it('does not let external prerequisite metadata override a startable candidate', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({
            number: 42,
            status: IssueStatus.Backlog,
            canStart: true,
            startBlocker: null,
            externalPrerequisites: [{ number: 77, title: 'Historical prerequisite', stage: 'done', status: 'done' }],
          }),
        ],
      })

      expect(state).toEqual({ kind: 'has-next', issueNumber: 42, })
    })

    it('uses priority rank then issue number instead of linked order for the display candidate', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({ number: 1, priority: 'p4', status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } }),
          makeLinkedIssue({ number: 3, priority: 'p1', status: IssueStatus.Backlog, canStart: true, startBlocker: null }),
        ],
      })
      expect(state).toEqual({ kind: 'has-next', issueNumber: 3, })
    })

    it('breaks equal priority ties by issue number instead of linked order', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({ number: 20, priority: 'p1', status: IssueStatus.Backlog, canStart: true, startBlocker: null }),
          makeLinkedIssue({ number: 10, priority: 'p1', status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } }),
        ],
      })
      expect(state).toEqual({ kind: 'draft-blocker', issueNumber: 10, })
    })
  })

  describe('returns running-but-idle for a running epic with no in-progress and no specific blocker', () => {
    it('returns running-but-idle when the only candidate has a waiting-for blocker (not draft or external)', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } },
          }),
        ],
      })
      expect(state).toEqual({ kind: 'running-but-idle' })
    })

    it('returns running-but-idle when the only candidate is in backlog with no blocker but canStart=false', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: null,
          }),
        ],
      })
      expect(state).toEqual({ kind: 'running-but-idle' })
    })
  })

  describe('specific blockers take priority over running-but-idle / idle-no-next', () => {
    it('a draft candidate on a running epic returns draft-blocker (not running-but-idle)', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: { kind: 'draft' },
          }),
        ],
      })
      expect(state).toEqual({ kind: 'draft-blocker', issueNumber: 12, })
    })

    it('an external-prereq candidate on a running epic returns external-prerequisite-blocker (not running-but-idle)', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: null,
            externalPrerequisites: [{ number: 99, title: 'X', stage: 'plan', status: 'backlog' }],
          }),
        ],
      })
      expect(state).toEqual({
        kind: 'external-prerequisite-blocker',
        issueNumber: 12,
        prerequisiteNumbers: [99],
      })
    })

    it('a draft candidate on an idle epic returns draft-blocker (not idle-no-next)', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: { kind: 'draft' },
          }),
        ],
      })
      expect(state).toEqual({ kind: 'draft-blocker', issueNumber: 12, })
    })
  })

  describe('returns idle-no-next for an idle epic with no startable candidate', () => {
    it('carries a derived reason when the candidate has a waiting-for blocker', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } },
          }),
        ],
      })
      expect(state.kind).toBe('idle-no-next')
      if (state.kind === 'idle-no-next') {
        expect(state.reason).toContain('waiting for #99')
      }
    })

    it('uses a generic reason when the candidate is non-startable with no recognized blocker', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: null,
          }),
        ],
      })
      expect(state.kind).toBe('idle-no-next')
      if (state.kind === 'idle-no-next') {
        expect(state.reason.length).toBeGreaterThan(0)
      }
    })
  })

  describe('priority of checks: never by parsing nextIssueReason', () => {
    it('uses only linkedIssues + epicStatus, ignores any nextIssueReason-like input', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({ number: 1, status: IssueStatus.InProgress, stage: WorkflowStage.Build }),
        ],
      })
      expect(state.kind).toBe('waiting-for-in-progress')
    })

    it('prefers draft-blocker over external-prerequisite-blocker when both apply', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Idle,
        linkedIssues: [
          makeLinkedIssue({
            number: 12,
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: { kind: 'draft' },
            externalPrerequisites: [{ number: 99, title: 'X', stage: 'plan', status: 'backlog' }],
          }),
        ],
      })
      expect(state).toEqual({ kind: 'draft-blocker', issueNumber: 12, })
    })

    it('does not let an older lower-priority draft contradict a higher-priority startable next issue', () => {
      const state = deriveAdvancementState({
        epicStatus: EpicStatus.Running,
        linkedIssues: [
          makeLinkedIssue({ number: 4, priority: 'p3', status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } }),
          makeLinkedIssue({ number: 9, priority: 'p0', status: IssueStatus.Backlog, canStart: true, startBlocker: null }),
        ],
      })
      expect(state).toEqual({ kind: 'has-next', issueNumber: 9, })
    })
  })

  describe('always returns one of the canonical state kinds', () => {
    it('produces only known kinds across a matrix of inputs', () => {
      const known = new Set(ADVANCEMENT_STATE_KINDS)
      const linkedIssues: LinkedIssue[] = [
        makeLinkedIssue({ number: 1, status: IssueStatus.InProgress, stage: WorkflowStage.Build }),
        makeLinkedIssue({ number: 2, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } }),
        makeLinkedIssue({
          number: 3,
          status: IssueStatus.Backlog,
          canStart: false,
          startBlocker: null,
          externalPrerequisites: [{ number: 9, title: 'X', stage: 'plan', status: 'backlog' }],
        }),
        makeLinkedIssue({ number: 4, status: IssueStatus.Backlog, canStart: true, startBlocker: null }),
        makeLinkedIssue({ number: 5, status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, canStart: false }),
      ]
      const statuses = [EpicStatus.Idle, EpicStatus.Running, EpicStatus.Paused, EpicStatus.Done, EpicStatus.Closed]
      for (const status of statuses) {
        const state = deriveAdvancementState({ epicStatus: status, linkedIssues })
        expect(known.has(state.kind)).toBe(true)
      }
    })
  })
})

describe('advancementCopy', () => {
  it('returns copy and linkNumbers for waiting-for-in-progress', () => {
    const copy = advancementCopy({ kind: 'waiting-for-in-progress', issueNumber: 7, })
    expect(copy.text).toContain('Waiting for #7')
    expect(copy.linkNumbers).toEqual([7])
  })

  it('returns copy and linkNumbers for draft-blocker', () => {
    const copy = advancementCopy({ kind: 'draft-blocker', issueNumber: 14, })
    expect(copy.text).toContain('still a draft')
    expect(copy.text).toContain('#14')
    expect(copy.linkNumbers).toEqual([14])
  })

  it('returns copy and linkNumbers for external-prerequisite-blocker with plural suffix', () => {
    const copy = advancementCopy({
      kind: 'external-prerequisite-blocker',
      issueNumber: 21,
      prerequisiteNumbers: [100, 200],
    })
    expect(copy.text).toContain('external issues')
    expect(copy.text).toContain('#100')
    expect(copy.text).toContain('#200')
    expect(copy.linkNumbers).toEqual([100, 200])
  })

  it('returns copy and linkNumbers for external-prerequisite-blocker with singular suffix', () => {
    const copy = advancementCopy({
      kind: 'external-prerequisite-blocker',
      issueNumber: 21,
      prerequisiteNumbers: [100],
    })
    expect(copy.text).toContain('external issue ')
    expect(copy.text).not.toContain('external issues ')
    expect(copy.linkNumbers).toEqual([100])
  })

  it('returns copy without links for running-but-idle', () => {
    const copy = advancementCopy({ kind: 'running-but-idle' })
    expect(copy.text).toContain('Running')
    expect(copy.linkNumbers).toEqual([])
  })

  it('returns copy that mentions the reason for idle-no-next', () => {
    const copy = advancementCopy({ kind: 'idle-no-next', reason: 'waiting for #99' })
    expect(copy.text).toContain('No startable next issue')
    expect(copy.text).toContain('waiting for #99')
    expect(copy.linkNumbers).toEqual([])
  })

  it('returns copy and linkNumbers for has-next', () => {
    const copy = advancementCopy({ kind: 'has-next', issueNumber: 42, })
    expect(copy.text).toContain('#42')
    expect(copy.linkNumbers).toEqual([42])
  })

  it('returns copy without links for nothing-pending', () => {
    const copy = advancementCopy({ kind: 'nothing-pending' })
    expect(copy.text).toBe('No pending startable linked issues')
    expect(copy.text).not.toContain('delivered')
    expect(copy.linkNumbers).toEqual([])
  })

  it('never returns an empty text for any state', () => {
    const cases: AdvancementState[] = [
      { kind: 'running-but-idle' },
      { kind: 'waiting-for-in-progress', issueNumber: 1, },
      { kind: 'draft-blocker', issueNumber: 2, },
      { kind: 'external-prerequisite-blocker', issueNumber: 3, prerequisiteNumbers: [4] },
      { kind: 'idle-no-next', reason: 'x' },
      { kind: 'has-next', issueNumber: 5, },
      { kind: 'nothing-pending' },
    ]
    for (const state of cases) {
      expect(advancementCopy(state).text.length).toBeGreaterThan(0)
    }
  })
})
