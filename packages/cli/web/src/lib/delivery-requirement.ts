import { IssueStatus, Stage, type Issue } from './types'

export function issueRequiresLocalMerge(issue: Pick<Issue, 'deliveryRequirement'>): boolean {
  return issue.deliveryRequirement?.requiresLocalMerge ?? true
}

export function issueFalseDoneApplicable(issue: Pick<Issue, 'deliveryRequirement'>): boolean {
  return issue.deliveryRequirement?.falseDoneApplicable ?? issueRequiresLocalMerge(issue)
}

export function isDoneOrCompletedIssue(issue: Pick<Issue, 'stage' | 'status'>): boolean {
  return issue.stage === Stage.Done || issue.status === IssueStatus.Completed
}

export function isFalseDoneIssue(issue: Pick<Issue, 'stage' | 'status' | 'mergeState' | 'deliveryRequirement'>): boolean {
  return isDoneOrCompletedIssue(issue)
    && issueFalseDoneApplicable(issue)
    && issue.mergeState !== 'merged'
}

export function isCompletedWithoutLocalMergeRequirement(
  issue: Pick<Issue, 'stage' | 'status' | 'deliveryRequirement'>,
): boolean {
  return isDoneOrCompletedIssue(issue) && !issueFalseDoneApplicable(issue)
}
