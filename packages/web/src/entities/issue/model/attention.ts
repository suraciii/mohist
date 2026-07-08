import type { AgentStatus } from '../../agent'
import { IssueHealth, WorkflowStage, type Issue } from './issue'

export type AttentionItem =
  | {
      kind: 'approval-needed' | 'integration-failed' | 'interrupted' | 'blocked'
      issueNumber: number
      issueId: string
      label: string
      detail?: string
    }
  | {
      kind: 'runner-unavailable' | 'runner-capacity-limited'
      label: string
      detail?: string
    }

type IssueAttentionItem = Extract<AttentionItem, { issueId: string }>

function isIntegrateFailure(issue: Issue): boolean {
  return (
    issue.workflowStage === WorkflowStage.Integrate
    && (
      issue.health === IssueHealth.Blocked
      || issue.health === IssueHealth.Interrupted
    )
  )
}

function classifyIssueAttention(issue: Issue): IssueAttentionItem | null {
  if (issue.approvalState?.status === 'awaiting') {
    return {
      kind: 'approval-needed',
      issueNumber: issue.number,
      issueId: issue.id,
      label: 'Approval needed',
      detail: issue.title,
    }
  }

  if (isIntegrateFailure(issue)) {
    return {
      kind: 'integration-failed',
      issueNumber: issue.number,
      issueId: issue.id,
      label: 'Integration failed',
      detail: issue.title,
    }
  }

  if (issue.health === IssueHealth.Interrupted) {
    return {
      kind: 'interrupted',
      issueNumber: issue.number,
      issueId: issue.id,
      label: 'Interrupted',
      detail: issue.title,
    }
  }

  if (issue.health === IssueHealth.Blocked) {
    return {
      kind: 'blocked',
      issueNumber: issue.number,
      issueId: issue.id,
      label: 'Needs action',
      detail: issue.blockedReason ?? issue.title,
    }
  }

  return null
}

function issueNeedsOwnerAction(issue: Issue): boolean {
  return classifyIssueAttention(issue) !== null
}

function deriveAttentionItems(issues: Issue[], agentStatus: AgentStatus): AttentionItem[] {
  const items: AttentionItem[] = []
  const seen = new Set<string>()

  for (const issue of issues) {
    if (seen.has(issue.id)) continue

    const item = classifyIssueAttention(issue)
    if (item) {
      seen.add(issue.id)
      items.push(item)
    }
  }

  if (agentStatus.runnerAvailable === false) {
    items.push({
      kind: 'runner-unavailable',
      label: 'Runner unavailable',
      detail: agentStatus.runnerMessage ?? 'No runner is connected.',
    })
  } else {
    const capacity = agentStatus.capacity
    if (capacity.max > 0 && capacity.active >= capacity.max) {
      items.push({
        kind: 'runner-capacity-limited',
        label: 'Runner at capacity',
        detail: `${capacity.active} of ${capacity.max} slots in use`,
      })
    }
  }

  return items
}

export { classifyIssueAttention, deriveAttentionItems, isIntegrateFailure, issueNeedsOwnerAction }
