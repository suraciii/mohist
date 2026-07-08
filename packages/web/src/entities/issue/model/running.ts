import { IssueHealth, IssueStatus, type Issue } from './issue'

export function isRunningIssue(issue: Issue): boolean {
  return (
    issue.status === IssueStatus.InProgress
    && issue.health !== IssueHealth.Done
    && issue.health !== IssueHealth.Cancelled
  )
}
