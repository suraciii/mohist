import { describe, expect, it } from 'vitest'
import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'
import {
  decideReverseDnsOutcome,
  isMergePayload,
  isRebasePayload,
  readOutcome,
} from './reverse-dns-outcome'

describe('reverse-dns-outcome: readOutcome', () => {
  it('returns null when the payload has no outcome-bearing field', () => {
    expect(readOutcome({})).toBeNull()
  })

  it('reads outcome from the .outcome field when present', () => {
    expect(readOutcome({ outcome: 'merge_completed' })).toBe('merge_completed')
  })

  it('falls back through result / kind / operation / reason', () => {
    expect(readOutcome({ result: 'r1' })).toBe('r1')
    expect(readOutcome({ kind: 'k1' })).toBe('k1')
    expect(readOutcome({ operation: 'o1' })).toBe('o1')
    expect(readOutcome({ reason: 'r2' })).toBe('r2')
  })

  it('returns null when the outcome field is not a string', () => {
    expect(readOutcome({ outcome: 42 })).toBeNull()
    expect(readOutcome({ outcome: { nested: true } })).toBeNull()
  })
})

describe('reverse-dns-outcome: isRebasePayload', () => {
  it('matches outcome strings that include "rebase"', () => {
    expect(isRebasePayload({ outcome: 'rebase_completed' })).toBe(true)
    expect(isRebasePayload({ outcome: 'rebase_conflict' })).toBe(true)
    expect(isRebasePayload({ outcome: 'rebase_aborted' })).toBe(true)
  })

  it('matches payloads that contain a .rebased or .conflicts key as a structural marker', () => {
    expect(isRebasePayload({ rebased: true })).toBe(true)
    expect(isRebasePayload({ conflicts: ['a.ts'] })).toBe(true)
  })

  it('does NOT match payloads that lack any rebase signal', () => {
    expect(isRebasePayload({ outcome: 'merged' })).toBe(false)
    expect(isRebasePayload({})).toBe(false)
  })
})

describe('reverse-dns-outcome: isMergePayload', () => {
  it('matches outcome strings that include "merge"', () => {
    expect(isMergePayload({ outcome: 'merge_completed' })).toBe(true)
    expect(isMergePayload({ outcome: 'merge_failed' })).toBe(true)
  })

  it('does NOT match payloads that lack a merge signal', () => {
    expect(isMergePayload({ outcome: 'rebase_completed' })).toBe(false)
    expect(isMergePayload({})).toBe(false)
  })
})

describe('reverse-dns-outcome: decideReverseDnsOutcome (no-match fallthrough)', () => {
  it('returns { handled: false } when the parsed payload has no issueNumber', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, { outcome: 'merge_completed' })
    expect(outcome).toEqual({ handled: false })
  })

  it('returns { handled: false } for a non-issue-event that carries a rebase payload', () => {
    // e.g. an arbitrary StageStarted that happens to look like a rebase should
    // not be claimed by the decider — only the "outcome of integration
    // work" eventName values are claimable.
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.StageStarted, {
      issueNumber: 9,
      outcome: 'rebase_completed',
    })
    expect(outcome).toEqual({ handled: false })
  })

  it('returns { handled: false } for IssueCompleted that is neither rebase nor merge', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 5,
      outcome: 'something_else',
    })
    expect(outcome).toEqual({ handled: false })
  })

  it('returns { handled: false } for WorkflowRunFailed that is neither rebase nor merge', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
      issueNumber: 7,
      outcome: 'something_else',
    })
    expect(outcome).toEqual({ handled: false })
  })
})

describe('reverse-dns-outcome: decideReverseDnsOutcome (rebase-completed arm)', () => {
  it('returns handled=true with rebaseConflict=null (clear) and rebase_completed event for IssueCompleted + rebase payload', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 7,
      outcome: 'rebase_completed',
      rebased: true,
    })
    expect(outcome).toEqual({
      handled: true,
      rebaseConflict: null,
      rebaseEvent: { type: 'rebase_completed', issueNumber: 7, rebased: true },
    })
  })

  it('defaults rebased to true when the .rebased field is missing or non-boolean', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 7,
      outcome: 'rebase_completed',
    })
    expect(outcome).toMatchObject({
      handled: true,
      rebaseConflict: null,
      rebaseEvent: { type: 'rebase_completed', issueNumber: 7, rebased: true },
    })

    const outcomeNonBool = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 7,
      outcome: 'rebase_completed',
      rebased: 'yes',
    })
    expect(outcomeNonBool).toMatchObject({
      handled: true,
      rebaseEvent: { type: 'rebase_completed', issueNumber: 7, rebased: true },
    })
  })

  it('does NOT include a toast on the rebase-completed arm (clearing is silent)', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 7,
      outcome: 'rebase_completed',
    })
    expect(outcome.handled).toBe(true)
    if (outcome.handled) {
      expect(outcome.toast).toBeUndefined()
    }
  })
})

describe('reverse-dns-outcome: decideReverseDnsOutcome (merge-success arm)', () => {
  it('returns handled=true with toast.success message for IssueCompleted + merge payload', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 13,
      outcome: 'merge_completed',
    })
    expect(outcome).toEqual({
      handled: true,
      toast: { tone: 'success', message: 'Issue #13 merged successfully' },
    })
  })

  it('does NOT dispatch a rebase event on the merge-success arm', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 13,
      outcome: 'merge_completed',
    })
    if (outcome.handled) {
      expect(outcome.rebaseEvent).toBeUndefined()
      expect(outcome.rebaseConflict).toBeUndefined()
    } else {
      throw new Error('expected handled: true')
    }
  })
})

describe('reverse-dns-outcome: decideReverseDnsOutcome (rebase-conflict arm)', () => {
  it('returns handled=true with state, rebase_conflict event, error toast for WorkflowRunFailed + rebase payload', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
      issueNumber: 21,
      outcome: 'rebase_conflict',
      conflicts: ['src/a.ts', 'src/b.ts'],
      error: 'CONFLICT (content): Merge conflict in src/a.ts',
    })
    expect(outcome).toEqual({
      handled: true,
      rebaseConflict: {
        issueNumber: 21,
        conflicts: ['src/a.ts', 'src/b.ts'],
        status: 'failed',
        error: 'CONFLICT (content): Merge conflict in src/a.ts',
      },
      rebaseEvent: {
        type: 'rebase_conflict',
        issueNumber: 21,
        conflicts: ['src/a.ts', 'src/b.ts'],
        status: 'failed',
        error: 'CONFLICT (content): Merge conflict in src/a.ts',
      },
      toast: { tone: 'error', message: 'Rebase conflict on Issue #21' },
    })
  })

  it('treats StageFailed + rebase payload as a parallel arm to WorkflowRunFailed', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.StageFailed, {
      issueNumber: 33,
      outcome: 'rebase_aborted',
      conflicts: [],
    })
    expect(outcome).toMatchObject({
      handled: true,
      rebaseConflict: { issueNumber: 33, conflicts: [], status: 'failed' },
      rebaseEvent: { type: 'rebase_conflict', issueNumber: 33, conflicts: [], status: 'failed' },
      toast: { tone: 'error', message: 'Rebase conflict on Issue #33' },
    })
  })

  it('filters .conflicts entries to strings only', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
      issueNumber: 21,
      outcome: 'rebase_conflict',
      conflicts: ['a.ts', 42, null, 'b.ts'],
    })
    if (outcome.handled && outcome.rebaseConflict) {
      expect(outcome.rebaseConflict.conflicts).toEqual(['a.ts', 'b.ts'])
    } else {
      throw new Error('expected handled: true with rebaseConflict')
    }
  })

  it('omits .error when the field is not a string', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
      issueNumber: 21,
      outcome: 'rebase_conflict',
      conflicts: ['a.ts'],
      error: 42,
    })
    if (outcome.handled && outcome.rebaseConflict) {
      expect(outcome.rebaseConflict.error).toBeUndefined()
    } else {
      throw new Error('expected handled: true with rebaseConflict')
    }
  })
})

describe('reverse-dns-outcome: decideReverseDnsOutcome (merge-failure arm)', () => {
  it('returns handled=true with toast.error message for WorkflowRunFailed + merge payload', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
      issueNumber: 99,
      outcome: 'merge_failed',
    })
    expect(outcome).toEqual({
      handled: true,
      toast: { tone: 'error', message: 'Merge failed for Issue #99' },
    })
  })

  it('does NOT dispatch a rebase event on the merge-failure arm (intentional)', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
      issueNumber: 99,
      outcome: 'merge_failed',
    })
    if (outcome.handled) {
      expect(outcome.rebaseEvent).toBeUndefined()
      expect(outcome.rebaseConflict).toBeUndefined()
    } else {
      throw new Error('expected handled: true')
    }
  })

  it('does NOT fire on a non-failure IssueCompleted + merge (no merge-failure arm exists there)', () => {
    // IssueCompleted + merge is merge-SUCCESS, not merge-failure. The
    // success arm maps to { handled: true, toast: success }, not the
    // failure toast. This pins the distinct messages.
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
      issueNumber: 13,
      outcome: 'merge_completed',
    })
    if (outcome.handled && outcome.toast) {
      expect(outcome.toast.tone).toBe('success')
      expect(outcome.toast.message).toBe('Issue #13 merged successfully')
    } else {
      throw new Error('expected handled: true with success toast')
    }
  })
})

describe('reverse-dns-outcome: decideReverseDnsOutcome (independence of sinks)', () => {
  it('returns all reverse-DNS side-effect slots atomically', () => {
    const outcome = decideReverseDnsOutcome(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
      issueNumber: 21,
      outcome: 'rebase_conflict',
      conflicts: ['x.ts'],
      error: 'boom',
    })
    expect(outcome.handled).toBe(true)
    if (outcome.handled) {
      expect(outcome.rebaseConflict).toBeDefined()
      expect(outcome.rebaseEvent).toBeDefined()
      expect(outcome.toast).toBeDefined()
    }
  })
})
