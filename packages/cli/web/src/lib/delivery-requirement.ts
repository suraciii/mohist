import { IssueStatus, Stage, type Issue } from './types'

export function issueRequiresLocalMerge(issue: Pick<Issue, 'deliveryRequirement'>): boolean {
  return issue.deliveryRequirement?.requiresLocalMerge ?? true
}

export function issueFalseDoneApplicable(issue: Pick<Issue, 'deliveryRequirement'>): boolean {
  return issue.deliveryRequirement?.falseDoneApplicable ?? issueRequiresLocalMerge(issue)
}

export function isFalseDoneIssue(issue: Pick<Issue, 'stage' | 'status' | 'mergeState' | 'deliveryRequirement'>): boolean {
  const isDoneOrCompleted = issue.stage === Stage.Done || issue.status === IssueStatus.Completed
  return isDoneOrCompleted
    && issueFalseDoneApplicable(issue)
    && issue.mergeState !== 'merged'
}
