import { describe, expect, it } from 'vitest'
import { isCompletedWithoutLocalMergeRequirement, isFalseDoneIssue, issueFalseDoneApplicable, issueRequiresLocalMerge } from './delivery-requirement'
import { IssueStatus, Stage, type Issue } from './types'

function makeIssue(overrides: Partial<Issue>): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test issue',
    stage: Stage.Done,
    status: IssueStatus.Completed,
    projectId: 'project-1',
    labels: [],
    createdAt: '2026-05-20T00:00:00.000Z',
    updatedAt: '2026-05-20T00:00:00.000Z',
    ...overrides,
  }
}

describe('delivery requirement helpers', () => {
  it('defaults missing delivery projection to local merge semantics', () => {
    const issue = makeIssue({ mergeState: null })

    expect(issueRequiresLocalMerge(issue)).toBe(true)
    expect(issueFalseDoneApplicable(issue)).toBe(true)
    expect(isFalseDoneIssue(issue)).toBe(true)
  })

  it('does not flag done workflows that explicitly do not require local merge', () => {
    const issue = makeIssue({
      mergeState: null,
      deliveryRequirement: {
        mode: 'handoff',
        requiresLocalMerge: false,
        requiresRemoteMerge: false,
        falseDoneApplicable: false,
      },
    })

    expect(isFalseDoneIssue(issue)).toBe(false)
    expect(isCompletedWithoutLocalMergeRequirement(issue)).toBe(true)
  })

  it('flags done workflows when the definition requires local merge evidence', () => {
    const issue = makeIssue({
      mergeState: 'conflict',
      deliveryRequirement: {
        mode: 'local-merge',
        requiresLocalMerge: true,
        requiresRemoteMerge: false,
        falseDoneApplicable: true,
      },
    })

    expect(isFalseDoneIssue(issue)).toBe(true)
  })
})
