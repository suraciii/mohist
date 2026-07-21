import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus } from '../../../entities/issue'
import { deriveIssueOnlyStatus } from './issueDecisionContext'

describe('deriveIssueOnlyStatus', () => {
  it('returns a draft status when the issue is still a draft', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      isDraft: true,
      isArchived: false,
      childSummary: null,
    })

    expect(context.label).toBe('Draft')
    expect(context.headline).toMatch(/Draft/)
    expect(context.rationale).toMatch(/draft/i)
    expect(context.nextAction).toMatch(/mark the issue ready/i)
  })

  it('returns an archived status when the issue is archived', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.Done,
      health: IssueHealth.Done,
      isDraft: false,
      isArchived: true,
      childSummary: { count: 3, doneCount: 3, blockedCount: 0 },
    })

    expect(context.label).toBe('Archived')
    expect(context.headline).toBe('Archived')
    expect(context.rationale).toMatch(/archived/i)
  })

  it('returns a backlog status without a child summary when no children exist', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      isDraft: false,
      isArchived: false,
      childSummary: null,
    })

    expect(context.label).toBe('Backlog')
    expect(context.headline).toMatch(/Backlog/)
    expect(context.rationale).toMatch(/composite issue waiting in backlog/i)
  })

  it('returns a backlog status with a child summary when children exist', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      isDraft: false,
      isArchived: false,
      childSummary: { count: 4, doneCount: 0, blockedCount: 0 },
    })

    expect(context.headline).toMatch(/Backlog/)
    expect(context.rationale).toMatch(/Backlog of 4 child issues/i)
  })

  it('returns an in-progress status that summarises the child progress', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      isDraft: false,
      isArchived: false,
      childSummary: { count: 4, doneCount: 1, blockedCount: 0 },
    })

    expect(context.label).toBe('In Progress')
    expect(context.headline).toMatch(/In progress/i)
    expect(context.rationale).toMatch(/1 of 4 child issues done/i)
    expect(context.nextAction).toMatch(/open a child issue/i)
  })

  it('returns a blocked status when any child is blocked', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.InProgress,
      health: IssueHealth.Blocked,
      isDraft: false,
      isArchived: false,
      childSummary: { count: 3, doneCount: 1, blockedCount: 1 },
    })

    expect(context.label).toBe('Blocked')
    expect(context.headline).toMatch(/Blocked/i)
    expect(context.rationale).toMatch(/child issue is blocked/i)
    expect(context.nextAction).toMatch(/open a blocked child issue/i)
  })

  it('returns a done status when the parent issue is done', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.Done,
      health: IssueHealth.Done,
      isDraft: false,
      isArchived: false,
      childSummary: { count: 3, doneCount: 3, blockedCount: 0 },
    })

    expect(context.label).toBe('Done')
    expect(context.headline).toBe('Done')
    expect(context.nextAction).toMatch(/no further action/i)
  })

  it('returns a cancelled status when the parent is cancelled', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.Cancelled,
      health: IssueHealth.Cancelled,
      isDraft: false,
      isArchived: false,
      childSummary: null,
    })

    expect(context.label).toBe('Cancelled')
    expect(context.headline).toBe('Cancelled')
    expect(context.rationale).toMatch(/no longer actionable/i)
  })

  it('handles an empty child summary for an in-progress parent', () => {
    const context = deriveIssueOnlyStatus({
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      isDraft: false,
      isArchived: false,
      childSummary: { count: 0, doneCount: 0, blockedCount: 0 },
    })

    expect(context.headline).toMatch(/In progress/i)
    expect(context.rationale).toMatch(/no child issues attached yet/i)
  })
})
