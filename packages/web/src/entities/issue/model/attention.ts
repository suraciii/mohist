import type { AgentStatus } from '../../agent'
import { IssueHealth, WorkflowStage, type Issue } from '..'

export interface AttentionItem {
  kind: 'approval-needed' | 'integration-failed' | 'interrupted' | 'blocked'
  issueNumber: number
  issueId: string
  label: string
  detail?: string
}

function isIntegrateFailure(issue: Issue): boolean {
  return (
    issue.workflowStage === WorkflowStage.Integrate
    && (
      issue.health === IssueHealth.Blocked
      || issue.health === IssueHealth.Interrupted
    )
  )
}

function deriveAttentionItems(issues: Issue[], _agentStatus: AgentStatus): AttentionItem[] {
  const items: AttentionItem[] = []
  const seen = new Set<string>()

  for (const issue of issues) {
    if (seen.has(issue.id)) continue

    if (issue.approvalState?.status === 'awaiting') {
      seen.add(issue.id)
      items.push({
        kind: 'approval-needed',
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Approval needed',
        detail: issue.title,
      })
    } else if (isIntegrateFailure(issue)) {
      seen.add(issue.id)
      items.push({
        kind: 'integration-failed',
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Integration failed',
        detail: issue.title,
      })
    } else if (issue.health === IssueHealth.Interrupted) {
      seen.add(issue.id)
      items.push({
        kind: 'interrupted',
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Interrupted',
        detail: issue.title,
      })
    } else if (issue.health === IssueHealth.Blocked) {
      seen.add(issue.id)
      items.push({
        kind: 'blocked',
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Needs action',
        detail: issue.blockedReason ?? issue.title,
      })
    }
  }

  return items
}

export { deriveAttentionItems, isIntegrateFailure }
