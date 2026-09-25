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
        | 'runner-partial-outage'
      label: string
      detail?: string
    }

export function isIssueAttentionItem(item: AttentionItem): item is IssueAttentionItem {
  return 'issueNumber' in item
}

function runnerAttentionItem(summary: RunnerStatusSummary): Exclude<AttentionItem, IssueAttentionItem> {
  switch (summary.fleet.state) {
    case 'no-runners-configured':
      return { kind: 'runner-unavailable', label: 'Runner unavailable', detail: 'No Runner definitions.' }
    case 'capacity-full':
      return { kind: 'runner-capacity-limited', label: 'Runner capacity full', detail: runnerSummaryText(summary) }
    case 'admission-blocked': {
      const hasReason = (reason: string) =>
        summary.fleet.reasons.includes(reason) || summary.rows.some((row) => row.admission.reasonCodes.includes(reason))
      if (
        hasReason('draining') ||
        summary.drainingCount > 0 ||
        summary.rows.some((row) => row.drain?.active === true)
      ) {
        return { kind: 'runner-draining', label: 'Runner draining', detail: runnerSummaryText(summary) }
      }
      if (hasReason('control-disconnected') || summary.disconnectedCount > 0) {
        return { kind: 'runner-disconnected', label: 'Runner disconnected', detail: runnerSummaryText(summary) }
      }
      if (hasReason('presence-offline') || summary.offlineCount > 0) {
        return { kind: 'runner-offline', label: 'Runner offline', detail: runnerSummaryText(summary) }
      }
      return { kind: 'runner-admission-blocked', label: 'Runner admission blocked', detail: runnerSummaryText(summary) }
    }
    case 'availability-unknown':
      return { kind: 'runner-unavailable', label: 'Runner availability unknown', detail: runnerSummaryText(summary) }
    case 'capacity-available':
      // Capacity is available, so this is not a dispatch block. Excluded Runners still
      // need attention: they are unreachable work, not capacity the Project can use.
      return { kind: 'runner-partial-outage', label: 'Some Runners unavailable', detail: runnerSummaryText(summary) }
  }
}

export function deriveAttentionItems(
  issues: Issue[],
  agentStatus: AgentStatus,
  runnerSummary?: RunnerStatusSummary,
): AttentionItem[] {
  const items: AttentionItem[] = []
  const seen = new Set<string>()
  const runnerAffectsActiveWorkflow = issues.some(
    (issue) => issue.status === IssueStatus.InProgress && issue.health !== IssueHealth.Blocked,
  )
  for (const issue of issues) {
    const issueKey = `${issue.projectId}:${issue.number}`
    if (seen.has(issueKey)) continue
    const item = classifyIssueAttention(issue)
    if (item) {
      seen.add(issueKey)
      items.push(item)
    }
  }
  if (runnerSummary && runnerAffectsActiveWorkflow) {
    if (runnerSummary.isError !== true && runnerSummary.isLoading !== true) {
      const state = runnerSummary.fleet.state
      const partialOutage = state === 'capacity-available' && runnerSummary.fleet.excludedGroups.length > 0
      if (state !== 'capacity-available' || partialOutage) items.push(runnerAttentionItem(runnerSummary))
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
