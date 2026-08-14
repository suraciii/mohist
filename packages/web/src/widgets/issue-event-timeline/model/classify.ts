import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'
import type { TimelineCategory } from './types'

export interface ClassificationResult {
  category: TimelineCategory
  attention: boolean
}

function isNeedsAttentionDrift(type: string, payload: Record<string, unknown>): boolean {
  return type === 'base_drift_detected' && payload.decision === 'needs-attention'
}

function isAgentResultAttention(type: string): boolean {
  return type === REVERSE_DNS_EVENT_TYPES.AgentTaskResultUnconfirmed
    || type === REVERSE_DNS_EVENT_TYPES.TaskBlocked
    || type === REVERSE_DNS_EVENT_TYPES.StageBlocked
    || type === REVERSE_DNS_EVENT_TYPES.WorkflowRunBlocked
}

export function classifyEvent(type: string, payload: Record<string, unknown> = {}): ClassificationResult {
  const lower = type.toLowerCase()

  if (isAgentResultAttention(type)) {
    return { category: 'attention', attention: true }
  }

  if (
    lower.includes('failed')
    || lower.includes('fail')
    || lower.includes('conflict')
    || lower.includes('error')
    || isNeedsAttentionDrift(type, payload)
  ) {
    return { category: 'failure', attention: true }
  }

  if (lower.includes('completed')) {
    return { category: 'success', attention: false }
  }

  if (
    lower.includes('approval-requested')
    || lower.includes('approval_requested')
    || lower.includes('approval-resolved')
    || lower.includes('approval_resolved')
    || lower.includes('paused')
  ) {
    return {
      category: 'approval',
      attention: lower.includes('approval-requested') || lower.includes('approval_requested') || lower.includes('paused'),
    }
  }

  if (
    lower.startsWith('rebase')
    || lower.startsWith('merge')
    || lower.startsWith('check')
    || lower.startsWith('integration')
    || lower === 'base_drift_detected'
    || lower === 'rebase_opportunity'
  ) {
    return { category: 'integration', attention: false }
  }

  if (
    lower.includes('labels')
    || lower.includes('priority')
    || lower.includes('prerequisite')
    || lower === 'comment_added'
    || type === REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged
    || type === REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged
    || type === REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded
    || type === REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved
  ) {
    return { category: 'metadata', attention: false }
  }

  return { category: 'workflow', attention: false }
}
