import { IssueHealth, WorkflowStage, type Issue } from './issue'

export type IssueAttentionItem = {
  kind: 'approval-needed' | 'integration-failed' | 'recoverable-interrupted' | 'blocked'
  issueNumber: number
  label: string
  detail?: string
}

function isIntegrateFailure(issue: Issue): boolean {
  return (
    issue.workflowStage === WorkflowStage.Integrate
    && issue.health === IssueHealth.Blocked
  )
}

function classifyIssueAttention(issue: Issue): IssueAttentionItem | null {
  if (issue.attention?.reason === 'recoverable-interrupted') {
    const reason = issue.attention.reasonCode ?? 'runner-lost'
    const deadline = issue.attention.recoveryDeadlineAt
    return {
      kind: 'recoverable-interrupted',
      issueNumber: issue.number,
      label: 'Recoverable interruption',
      detail: `${reason}${deadline ? `; recovery deadline ${deadline}` : ''}`,
    }
  }

  if (issue.approvalState?.status === 'awaiting') {
    return {
      kind: 'approval-needed',
      issueNumber: issue.number,
      label: 'Approval needed',
      detail: issue.title,
    }
  }

  if (isIntegrateFailure(issue)) {
    return {
      kind: 'integration-failed',
      issueNumber: issue.number,
      label: 'Integration failed',
      detail: issue.title,
    }
  }

  if (issue.health === IssueHealth.Blocked) {
    return {
      kind: 'blocked',
      issueNumber: issue.number,
      label: 'Needs action',
      detail: issue.blockedReason ?? issue.title,
    }
  }

  return null
}

function issueNeedsOwnerAction(issue: Issue): boolean {
  return classifyIssueAttention(issue) !== null
}

export { classifyIssueAttention, isIntegrateFailure, issueNeedsOwnerAction }
