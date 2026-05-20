import type { Issue, AgentStatus } from './types'
import { IssueStatus, Stage } from './types'
import { isFalseDoneIssue } from './delivery-requirement'

export interface AttentionItem {
  issueNumber: number
  issueId: string
  label: string
  detail?: string
}

const INTEGRATE_FAILURE_MERGE_STATES = new Set(['blocked', 'build-failed', 'conflict'])

function isIntegrateFailure(issue: Issue): boolean {
  return (
    issue.stage === Stage.Integrate
    && (
      issue.status === IssueStatus.Blocked
      || issue.status === IssueStatus.Interrupted
      || (
        typeof issue.mergeState === 'string'
        && INTEGRATE_FAILURE_MERGE_STATES.has(issue.mergeState)
      )
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
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Approval needed',
        detail: issue.title,
      })
    } else if (isIntegrateFailure(issue)) {
      seen.add(issue.id)
      items.push({
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Integration failed',
        detail: issue.title,
      })
    } else if (isFalseDoneIssue(issue)) {
      seen.add(issue.id)
      items.push({
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Not merged',
        detail: issue.title,
      })
    } else if (issue.status === IssueStatus.Interrupted) {
      seen.add(issue.id)
      items.push({
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Interrupted',
        detail: issue.title,
      })
    } else if (issue.mergeState === 'blocked') {
      seen.add(issue.id)
      items.push({
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Needs action',
        detail: issue.title,
      })
    } else if (issue.status === IssueStatus.Blocked) {
      seen.add(issue.id)
      items.push({
        issueNumber: issue.number,
        issueId: issue.id,
        label: 'Needs action',
        detail: issue.blockedReason ?? issue.title,
      })
    }
  }

  return items
}

export { deriveAttentionItems }
