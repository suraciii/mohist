import { IssueHealth, IssueStatus } from '../../../entities/issue'

export interface IssueOnlyStatusInput {
  status: IssueStatus
  health: IssueHealth
  isDraft: boolean
  isArchived: boolean
  childSummary: { count: number; doneCount: number; blockedCount: number } | null
}

export interface IssueOnlyStatusContext {
  label: string
  headline: string
  rationale: string
  nextAction: string
}

const COMPOSITE_PROGRESS_BY_STATUS: Record<IssueStatus, (summary: { count: number; doneCount: number; blockedCount: number }) => string> = {
  [IssueStatus.Backlog]: (summary) => {
    if (summary.count === 0) return 'Grouping of child issues without a workflow run yet.'
    return `Backlog of ${summary.count} ${summary.count === 1 ? 'child issue' : 'child issues'}.`
  },
  [IssueStatus.InProgress]: (summary) => {
    if (summary.count === 0) return 'No child issues attached yet.'
    if (summary.doneCount === summary.count) return `All ${summary.count} child ${summary.count === 1 ? 'issue is' : 'issues are'} done.`
    if (summary.blockedCount > 0) {
      return `${summary.doneCount} of ${summary.count} child ${summary.count === 1 ? 'issue' : 'issues'} done; ${summary.blockedCount} blocked.`
    }
    return `${summary.doneCount} of ${summary.count} child ${summary.count === 1 ? 'issue' : 'issues'} done.`
  },
  [IssueStatus.Done]: (summary) => `All ${summary.count} child ${summary.count === 1 ? 'issue' : 'issues'} done.`,
  [IssueStatus.Cancelled]: (_summary) => 'This composite issue is cancelled.',
}

export function deriveIssueOnlyStatus(input: IssueOnlyStatusInput): IssueOnlyStatusContext {
  const { status, health, isDraft, isArchived, childSummary } = input

  if (isDraft) {
    return {
      label: 'Draft',
      headline: 'Draft — not ready to start',
      rationale: 'This issue is still a draft and cannot run.',
      nextAction: 'Mark the issue ready to start a workflow.',
    }
  }

  if (isArchived) {
    return {
      label: 'Archived',
      headline: 'Archived',
      rationale: 'This issue is archived. Workflow history is preserved for reference.',
      nextAction: 'Open the archived list to find another issue.',
    }
  }

  if (status === IssueStatus.Cancelled) {
    return {
      label: 'Cancelled',
      headline: 'Cancelled',
      rationale: 'This composite issue is cancelled and no longer actionable.',
      nextAction: 'Open a child issue to continue work.',
    }
  }

  if (status === IssueStatus.Done || health === IssueHealth.Done) {
    return {
      label: 'Done',
      headline: 'Done',
      rationale: 'This composite issue is complete.',
      nextAction: 'No further action required.',
    }
  }

  if (status === IssueStatus.Backlog) {
    return {
      label: 'Backlog',
      headline: 'Backlog — no workflow yet',
      rationale: childSummary
        ? COMPOSITE_PROGRESS_BY_STATUS[IssueStatus.Backlog](childSummary)
        : 'Composite issue waiting in backlog.',
      nextAction: 'Mark the issue ready to start a workflow.',
    }
  }

  const summary = childSummary ?? { count: 0, doneCount: 0, blockedCount: 0 }
  const progressLabel = COMPOSITE_PROGRESS_BY_STATUS[IssueStatus.InProgress](summary)

  return {
    label: health === IssueHealth.Blocked ? 'Blocked' : 'In Progress',
    headline: health === IssueHealth.Blocked
      ? `Blocked — ${progressLabel}`
      : `In progress — ${progressLabel}`,
    rationale: health === IssueHealth.Blocked
      ? 'At least one child issue is blocked.'
      : progressLabel,
    nextAction: summary.blockedCount > 0
      ? 'Open a blocked child issue to unblock progress.'
      : 'Open a child issue to continue work.',
  }
}

export type { IssueStatus as CompositeIssueStatus } from '../../../entities/issue'
