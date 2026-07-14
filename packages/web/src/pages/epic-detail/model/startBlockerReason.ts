import { IssueHealth, IssueStatus } from '../../../entities/issue'
import type { LinkedIssue } from '../../../entities/epic'

export interface StartBlockerReasonContext {
  issue: Pick<LinkedIssue, 'startBlocker' | 'health' | 'status'>
  hasInProgress: boolean
}

export type StartBlockerReason =
  | 'Another issue is in progress'
  | `Waiting for #${number}`
  | 'Still a draft'
  | 'Blocked'
  | 'Not startable'

export function deriveStartBlockerReason(context: StartBlockerReasonContext): StartBlockerReason {
  const { issue, hasInProgress } = context

  if (hasInProgress && issue.status !== IssueStatus.InProgress) {
    return 'Another issue is in progress'
  }

  const blocker = issue.startBlocker
  if (blocker) {
    if (blocker.kind === 'draft') {
      return 'Still a draft'
    }
    if (blocker.kind === 'waiting-for') {
      return `Waiting for #${blocker.issue.number}` as StartBlockerReason
    }
  }

  if (issue.health === IssueHealth.Blocked) {
    return 'Blocked'
  }

  return 'Not startable'
}
