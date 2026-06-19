function getValue(payload: Record<string, unknown>, ...keys: string[]): unknown {
  for (const key of keys) {
    if (key in payload) return payload[key]
    const lower = key.toLowerCase()
    for (const k of Object.keys(payload)) {
      if (k.toLowerCase() === lower) return payload[k]
    }
  }
  return undefined
}

function getString(payload: Record<string, unknown>, ...keys: string[]): string | null {
  const value = getValue(payload, ...keys)
  if (typeof value === 'string' && value) return value
  return null
}

function getArray(payload: Record<string, unknown>, key: string): unknown[] {
  const value = getValue(payload, key)
  if (Array.isArray(value)) return value
  return []
}

function getLabelMap(payload: Record<string, unknown>, ...keys: string[]): Record<string, string> | null {
  for (const key of keys) {
    const value = payload[key]
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      return value as Record<string, string>
    }
  }
  return null
}

function formatLabelMap(map: Record<string, string> | null): string {
  if (!map) return ''
  const entries = Object.keys(map)
    .sort()
    .map((k) => `${k}=${map[k]}`)
  return entries.join(', ')
}

export function formatStageName(stage: string | null | undefined): string {
  if (!stage) return ''
  return stage
    .split(/[_-]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1).toLowerCase())
    .join(' ')
}

function prettifyType(type: string): string {
  return type
    .replace(/^com\.mohist\.(workflow|issue)\./, '')
    .replace(/\./g, ' ')
    .replace(/-/g, ' ')
    .split(' ')
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1).toLowerCase())
    .join(' ')
}

export function describeEvent(type: string, payload: Record<string, unknown> = {}): string {
  const fromStage = formatStageName(getString(payload, 'from'))
  const toStage = formatStageName(getString(payload, 'to'))
  const stage = formatStageName(getString(payload, 'stage'))
  const labels = formatLabelMap(getLabelMap(payload, 'labels', 'newLabels'))
  const oldLabels = formatLabelMap(getLabelMap(payload, 'oldLabels'))
  const priority = getString(payload, 'priority') ?? ''
  const prerequisiteId = getString(payload, 'prerequisiteId') ?? ''
  const conflicts = getArray(payload, 'conflicts')
  const reason = getString(payload, 'reason') ?? ''
  const error = getString(payload, 'error') ?? ''
  const checkName = getString(payload, 'checkName') ?? ''
  const verdict = getString(payload, 'verdict') ?? ''
  const step = getString(payload, 'step') ?? ''
  const failingStep = getString(payload, 'failingStep') ?? ''
  const decision = getString(payload, 'decision') ?? ''
  const baseBranch = getString(payload, 'baseBranch') ?? ''
  const attentionReason = getString(payload, 'reason') ?? ''
  const body = getString(payload, 'body') ?? ''

  switch (type) {
    case 'com.mohist.workflow.stage.started':
    case 'com.mohist.workflow.stage.completed':
    case 'com.mohist.workflow.stage.failed':
    case 'stage_changed':
      if (fromStage && toStage) return `Stage moved from ${fromStage} to ${toStage}`
      if (toStage) return `Stage moved to ${toStage}`
      if (stage) return `Stage ${stage}`
      return 'Stage changed'

    case 'com.mohist.workflow.stage.approval-requested':
    case 'approval_requested':
      return stage ? `Approval requested for ${stage}` : 'Approval requested'

    case 'com.mohist.workflow.stage.approval-resolved':
      return stage ? `Approval resolved for ${stage}` : 'Approval resolved'

    case 'com.mohist.workflow.run.started':
    case 'agent_started':
      return 'Run started'

    case 'com.mohist.workflow.run.resumed':
      return 'Run resumed'

    case 'agent_completed':
      return 'Agent completed'

    case 'com.mohist.workflow.run.paused':
    case 'agent_paused':
      return 'Run paused'

    case 'com.mohist.workflow.run.stopped':
      return 'Run stopped'

    case 'com.mohist.workflow.run.completed':
      return 'Run completed'

    case 'com.mohist.workflow.run.failed':
    case 'agent_error':
      return error ? `Run failed: ${error}` : 'Run failed'

    case 'com.mohist.workflow.run.retrying':
      return 'Run retrying'

    case 'com.mohist.workflow.run.rerunning':
      return 'Run rerunning'

    case 'com.mohist.issue.created':
      return 'Issue created'

    case 'com.mohist.issue.closed':
      return 'Issue closed'

    case 'com.mohist.issue.archived':
      return 'Issue archived'

    case 'com.mohist.issue.unarchived':
      return 'Issue unarchived'

    case 'com.mohist.issue.reopened':
      return 'Issue reopened'

    case 'com.mohist.issue.work-started':
      return 'Work started'

    case 'com.mohist.issue.work-completed':
      return 'Work completed'

    case 'com.mohist.issue.labels-changed':
      if (labels && oldLabels) {
        return `Issue labels changed from ${oldLabels} to ${labels}`
      }
      return labels ? `Issue labeled ${labels}` : 'Issue labels changed'

    case 'com.mohist.issue.priority-changed':
      return priority ? `Issue priority set to ${priority}` : 'Issue priority changed'

    case 'com.mohist.issue.prerequisite-added':
      return prerequisiteId ? `Prerequisite #${prerequisiteId} added` : 'Prerequisite added'

    case 'com.mohist.issue.prerequisite-removed':
      return prerequisiteId ? `Prerequisite #${prerequisiteId} removed` : 'Prerequisite removed'

    case 'comment_added':
      return body ? `Comment added: ${body.slice(0, 80)}${body.length > 80 ? '...' : ''}` : 'Comment added'

    case 'merge_queued':
      return 'Merge queued'

    case 'merge_started':
      return 'Merge started'

    case 'merge_completed':
      return 'Merge completed'

    case 'merge_failed':
      return reason ? `Merge failed: ${reason}` : 'Merge failed'

    case 'rebase_started':
      return 'Rebase started'

    case 'rebase_progress':
      return step ? `Rebase ${step}` : 'Rebase in progress'

    case 'rebase_completed':
      return 'Rebase completed'

    case 'rebase_conflict':
      return conflicts.length > 0
        ? `Rebase conflict detected on ${conflicts.length} file${conflicts.length === 1 ? '' : 's'}`
        : 'Rebase conflict detected'

    case 'agent_conflict_resolution_started':
      return 'Conflict resolution started'

    case 'agent_conflict_resolution_completed':
      return 'Conflict resolution completed'

    case 'agent_conflict_resolution_failed':
      return error ? `Conflict resolution failed: ${error}` : 'Conflict resolution failed'

    case 'check_started':
      return 'Check started'

    case 'check_update':
      if (checkName && verdict) return `${checkName}: ${verdict}`
      if (checkName && reason) return `${checkName}: ${reason}`
      if (checkName) return `${checkName} updated`
      return 'Check updated'

    case 'check_suite_status_changed':
      return 'Check suite status changed'

    case 'integration_started':
      return 'Integration started'

    case 'integration_step_updated':
      return step ? `Integration step ${step} updated` : 'Integration step updated'

    case 'integration_completed':
      return 'Integration completed'

    case 'integration_failed':
      return failingStep ? `Integration failed at ${failingStep}` : 'Integration failed'

    case 'base_drift_detected':
      if (decision === 'needs-attention') return 'Base drift needs attention'
      if (baseBranch) return `Base drift detected on ${baseBranch}`
      return 'Base drift detected'

    case 'rebase_opportunity':
      return decision ? `Rebase opportunity: ${decision}` : 'Rebase opportunity detected'

    case 'user_attention_requested':
      return attentionReason ? `Attention requested: ${attentionReason}` : 'Attention requested'

    case 'agent_blocked':
      return reason ? `Blocked: ${reason}` : 'Agent blocked'

    case 'stage_task_update':
      return 'Task updated'

    case 'com.mohist.agent-session.runtime-bound':
    case 'com.mohist.agent-session.usage-recorded':
    case 'com.mohist.agent-session.model-changed':
      return prettifyType(type)

    default:
      return prettifyType(type)
  }
}