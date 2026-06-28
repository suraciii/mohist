import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus } from '../../../entities/issue/@x/types'
import type { LinkedIssue } from '../../../entities/epic/model/types'
import { deriveStartBlockerReason } from './startBlockerReason'

function makeIssue(
  overrides: Partial<Pick<LinkedIssue, 'startBlocker' | 'health' | 'status'>> = {
    startBlocker: null,
    health: IssueHealth.Active,
    status: IssueStatus.Backlog,
  },
) {
  return {
    startBlocker: null,
    health: IssueHealth.Active,
    status: IssueStatus.Backlog,
    ...overrides,
  }
}

describe('deriveStartBlockerReason', () => {
  it('returns "Another issue is in progress" when any sibling is in progress, regardless of blocker', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: null, health: IssueHealth.Active }),
        hasInProgress: true,
      }),
    ).toBe('Another issue is in progress')

    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: { kind: 'draft' }, health: IssueHealth.Active }),
        hasInProgress: true,
      }),
    ).toBe('Another issue is in progress')

    expect(
      deriveStartBlockerReason({
        issue: makeIssue({
          startBlocker: { kind: 'waiting-for', issue: { number: 7, title: 'X' } },
          health: IssueHealth.Active,
        }),
        hasInProgress: true,
      }),
    ).toBe('Another issue is in progress')
  })

  it('does not call the current in-progress issue another in-progress issue', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: null, health: IssueHealth.Active, status: IssueStatus.InProgress }),
        hasInProgress: true,
      }),
    ).toBe('Not startable')
  })

  it('returns "Waiting for #N" when the issue has a waiting-for blocker and no sibling is running', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({
          startBlocker: { kind: 'waiting-for', issue: { number: 42, title: 'Upstream' } },
          health: IssueHealth.Active,
        }),
        hasInProgress: false,
      }),
    ).toBe('Waiting for #42')
  })

  it('prefers waiting-for reason over health=blocked when both apply', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({
          startBlocker: { kind: 'waiting-for', issue: { number: 1, title: 'X' } },
          health: IssueHealth.Blocked,
        }),
        hasInProgress: false,
      }),
    ).toBe('Waiting for #1')
  })

  it('returns "Still a draft" when the issue has a draft blocker and no sibling is running', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: { kind: 'draft' }, health: IssueHealth.Active }),
        hasInProgress: false,
      }),
    ).toBe('Still a draft')
  })

  it('returns "Blocked" when health is blocked but the issue has no recognized blocker', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: null, health: IssueHealth.Blocked }),
        hasInProgress: false,
      }),
    ).toBe('Blocked')
  })

  it('returns "Not startable" as the fallback when no specific blocker matches', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: null, health: IssueHealth.Active }),
        hasInProgress: false,
      }),
    ).toBe('Not startable')

    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: null, health: IssueHealth.Paused }),
        hasInProgress: false,
      }),
    ).toBe('Not startable')

    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: null, health: IssueHealth.Done }),
        hasInProgress: false,
      }),
    ).toBe('Not startable')
  })

  it('returns "Still a draft" when blocker is draft even if health is also blocked', () => {
    expect(
      deriveStartBlockerReason({
        issue: makeIssue({ startBlocker: { kind: 'draft' }, health: IssueHealth.Blocked }),
        hasInProgress: false,
      }),
    ).toBe('Still a draft')
  })
})
