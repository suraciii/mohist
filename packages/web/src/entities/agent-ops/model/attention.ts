import type { AgentStatus } from '../../agent/@x/status'
import { IssueHealth, IssueStatus, type Issue } from '../../issue/@x/types'
import {
  classifyIssueAttention,
  type IssueAttentionItem,
} from '../../issue/@x/attention'

export type AttentionItem = IssueAttentionItem | {
  kind: 'runner-unavailable' | 'runner-capacity-limited'
  label: string
  detail?: string
}

export function isIssueAttentionItem(item: AttentionItem): item is IssueAttentionItem {
  return 'issueNumber' in item
}

export function deriveAttentionItems(issues: Issue[], agentStatus: AgentStatus): AttentionItem[] {
  const items: AttentionItem[] = []
  const seen = new Set<string>()
  for (const issue of issues) {
    const issueKey = `${issue.projectId}:${issue.number}`
    if (seen.has(issueKey)) continue
    const item = classifyIssueAttention(issue)
    if (item) {
      seen.add(issueKey)
      items.push(item)
    }
  }
  const runnerAffectsActiveWorkflow = issues.some(
    (issue) => issue.status === IssueStatus.InProgress && issue.health === IssueHealth.Active,
  )
  if (agentStatus.runnerAvailable === false && runnerAffectsActiveWorkflow) {
    items.push({ kind: 'runner-unavailable', label: 'Runner unavailable', detail: agentStatus.runnerMessage ?? 'No runner is connected.' })
  } else if (agentStatus.runnerAvailable !== false) {
    const capacity = agentStatus.capacity
    if (capacity.max > 0 && capacity.active >= capacity.max) {
      items.push({ kind: 'runner-capacity-limited', label: 'Runner at capacity', detail: `${capacity.active} of ${capacity.max} slots in use` })
    }
  }
  return items
}
