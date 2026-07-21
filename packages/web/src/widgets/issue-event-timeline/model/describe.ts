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

export type TaskTitleResolver = (stage: string, taskId: string) => string | null

export function describeEvent(
  type: string,
  payload: Record<string, unknown> = {},
  resolveTaskTitle?: TaskTitleResolver,
): string {
  const fromStage = formatStageName(getString(payload, 'from'))
  const toStage = formatStageName(getString(payload, 'to'))
  const stage = formatStageName(getString(payload, 'stage'))
  const labels = formatLabelMap(getLabelMap(payload, 'labels', 'newLabels'))
  const oldLabels = formatLabelMap(getLabelMap(payload, 'oldLabels'))
  const priority = getString(payload, 'priority') ?? ''
  const prerequisiteId = getString(payload, 'prerequisiteId') ?? ''
  const error = getString(payload, 'error') ?? ''
  const taskId = getString(payload, 'taskId')
  const taskStage = getString(payload, 'stage')
  const taskSubject = taskId
    ? (taskStage ? resolveTaskTitle?.(taskStage, taskId) : null) ?? taskId
    : null
  const artifactPath = getString(payload, 'path')

  switch (type) {
    case 'com.mohist.workflow.stage.started':
    case 'com.mohist.workflow.stage.completed':
    case 'com.mohist.workflow.stage.failed':
      if (fromStage && toStage) return `Stage moved from ${fromStage} to ${toStage}`
      if (toStage) return `Stage moved to ${toStage}`
      if (stage) return `Stage ${stage}`
      return 'Stage changed'

    case 'com.mohist.workflow.stage.approval-requested':
      return stage ? `Approval requested for ${stage}` : 'Approval requested'

    case 'com.mohist.workflow.stage.approval-resolved':
      return stage ? `Approval resolved for ${stage}` : 'Approval resolved'

    case 'com.mohist.workflow.run.started':
      return 'Run started'

    case 'com.mohist.workflow.run.resumed':
      return 'Run resumed'

    case 'com.mohist.workflow.run.paused':
      return 'Run paused'

    case 'com.mohist.workflow.run.stopped':
      return 'Run stopped'

    case 'com.mohist.workflow.run.completed':
      return 'Run completed'

    case 'com.mohist.workflow.run.failed':
      return error ? `Run failed: ${error}` : 'Run failed'

    case 'com.mohist.workflow.run.retrying':
      return 'Run retrying'

    case 'com.mohist.workflow.run.rerunning':
      return 'Run rerunning'

    case 'com.mohist.workflow.task.started':
      return taskSubject ? `${taskSubject} started` : 'Task started'

    case 'com.mohist.workflow.task.completed':
      return taskSubject ? `${taskSubject} completed` : 'Task completed'

    case 'com.mohist.workflow.task.failed':
      return taskSubject ? `${taskSubject} failed` : 'Task failed'

    case 'com.mohist.workflow.artifact.recorded':
      return artifactPath ? `${artifactPath} recorded` : 'Artifact recorded'

    case 'com.mohist.issue.created':
      return 'Issue created'

    case 'com.mohist.issue.cancelled':
      return 'Issue cancelled'

    case 'com.mohist.issue.archived':
      return 'Issue archived'

    case 'com.mohist.issue.unarchived':
      return 'Issue unarchived'

    case 'com.mohist.issue.reopened':
      return 'Issue reopened'

    case 'com.mohist.issue.work-started':
      return 'Work started'

    case 'com.mohist.issue.completed':
      return 'Issue completed'

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

    case 'com.mohist.agent-session.runtime-bound':
    case 'com.mohist.agent-session.usage-recorded':
    case 'com.mohist.agent-session.model-changed':
      return prettifyType(type)

    default:
      return prettifyType(type)
  }
}
