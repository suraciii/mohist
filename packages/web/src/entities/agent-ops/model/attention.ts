import type { AgentStatus } from '../../agent/@x/status'
import { runnerSummaryText, type RunnerStatusSummary } from '../../runner/@x/agent-ops'
import { IssueHealth, IssueStatus, type Issue } from '../../issue/@x/types'
import { classifyIssueAttention, type IssueAttentionItem } from '../../issue/@x/attention'

export type AttentionItem =
  | IssueAttentionItem
  | {
      kind:
        | 'runner-unavailable'
        | 'runner-offline'
        | 'runner-disconnected'
        | 'runner-admission-blocked'
        | 'runner-draining'
        | 'runner-capacity-limited'
      label: string
      detail?: string
    }

export function isIssueAttentionItem(item: AttentionItem): item is IssueAttentionItem {
  return 'issueNumber' in item
}

function runnerAttentionItem(summary: RunnerStatusSummary): Exclude<AttentionItem, IssueAttentionItem> {
  if (summary.rows.length === 0) {
    return { kind: 'runner-unavailable', label: 'Runner unavailable', detail: 'No Runner definitions.' }
  }
  if (summary.offlineCount > 0) {
    return { kind: 'runner-offline', label: 'Runner offline', detail: runnerSummaryText(summary) }
  }
  if (summary.disconnectedCount > 0) {
    return { kind: 'runner-disconnected', label: 'Runner control disconnected', detail: runnerSummaryText(summary) }
  }
  if (summary.blockedCount > 0) {
    return { kind: 'runner-admission-blocked', label: 'Runner admission blocked', detail: runnerSummaryText(summary) }
  }
  if (summary.drainingCount > 0) {
    return { kind: 'runner-draining', label: 'Runner draining', detail: runnerSummaryText(summary) }
  }
  if (summary.fullCount > 0) {
    return { kind: 'runner-capacity-limited', label: 'Runner capacity full', detail: runnerSummaryText(summary) }
  }
  return { kind: 'runner-admission-blocked', label: 'Runner admission blocked', detail: runnerSummaryText(summary) }
}

export function deriveAttentionItems(
  issues: Issue[],
  agentStatus: AgentStatus,
  runnerSummary?: RunnerStatusSummary,
): AttentionItem[] {
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
  if (runnerSummary && runnerAffectsActiveWorkflow) {
    if (runnerSummary.isError !== true && runnerSummary.isLoading !== true) {
      const item = runnerAttentionItem(runnerSummary)
      if (runnerSummary.blockedCount > 0 || runnerSummary.rows.length === 0) items.push(item)
    }
  } else if (agentStatus.runnerAvailable === false && runnerAffectsActiveWorkflow) {
    items.push({
      kind: 'runner-unavailable',
      label: 'Runner unavailable',
      detail: agentStatus.runnerMessage ?? 'No runner is connected.',
    })
  } else if (agentStatus.runnerAvailable !== false) {
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
