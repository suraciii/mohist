import { IssueHealth, IssueStatus } from '../../issue/@x/types'
import type { LinkedIssue } from './types'

export function canInlineStartRow(issue: LinkedIssue, hasInProgressSibling = false): boolean {
  if (hasInProgressSibling) return false
  if (!issue.canStart) return false
  if (issue.status === IssueStatus.InProgress) return false
  if (issue.status === IssueStatus.Done) return false
  if (issue.status === IssueStatus.Cancelled) return false
  if (issue.health === IssueHealth.Blocked) return false
  return true
}