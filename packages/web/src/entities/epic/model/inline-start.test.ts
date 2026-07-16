import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../issue/@x/types'
import type { LinkedIssue } from './types'
import { canInlineStartRow } from './inline-start'

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

describe('canInlineStartRow', () => {
  it('returns true for a startable backlog issue with no blocker', () => {
    expect(canInlineStartRow(makeLinkedIssue())).toBe(true)
  })

  it('returns true for a startable backlog issue with a draft blocker (startable=true means no blocker)', () => {
    expect(canInlineStartRow(makeLinkedIssue({ startBlocker: { kind: 'draft' } }))).toBe(true)
  })

  it('returns false when canStart is false (not startable by read model)', () => {
    expect(canInlineStartRow(makeLinkedIssue({ canStart: false }))).toBe(false)
  })

  it('returns false when status is in_progress even if canStart is true', () => {
    expect(canInlineStartRow(makeLinkedIssue({ status: IssueStatus.InProgress }))).toBe(false)
  })

  it('returns false when status is done even if canStart is true', () => {
    expect(canInlineStartRow(makeLinkedIssue({ status: IssueStatus.Done }))).toBe(false)
  })

  it('returns false when status is cancelled even if canStart is true', () => {
    expect(canInlineStartRow(makeLinkedIssue({ status: IssueStatus.Cancelled }))).toBe(false)
  })

  it('returns false when health is blocked even if canStart is true and status is backlog', () => {
    expect(canInlineStartRow(makeLinkedIssue({ health: IssueHealth.Blocked }))).toBe(false)
  })

  it('returns false when both canStart is false and status is in_progress', () => {
    expect(canInlineStartRow(makeLinkedIssue({
      canStart: false,
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
    }))).toBe(false)
  })

  it('treats canStart as the authoritative gating signal (false always wins)', () => {
    expect(canInlineStartRow(makeLinkedIssue({
      canStart: false,
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
    }))).toBe(false)
  })
})
