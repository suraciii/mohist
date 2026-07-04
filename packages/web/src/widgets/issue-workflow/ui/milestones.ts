import type { TaskLogLine } from '../../../entities/issue/model/task-log'
import type { WorkflowRunSession } from '../../../entities/coder-session/model/types'

export type TaskLogMilestoneKind = 'model-bound' | 'session-ended'

export interface TaskLogMilestone {
  kind: TaskLogMilestoneKind
  timestamp: string
  label: string
  detail: string
  failed?: boolean
}

export type TimelineRow = TaskLogLine | TaskLogMilestone

export function isTaskLogMilestone(row: TimelineRow): row is TaskLogMilestone {
  return !('seq' in row)
}

interface AgentTaskIdentity {
  origin?: { uses?: string } | null
  sessionName?: string | null
  classification?: string | null
}

export function isAcpAgentTask(input: AgentTaskIdentity | null | undefined): boolean {
  if (!input) return false
  const uses = input.origin?.uses
  const sessionName = input.sessionName
  const classification = input.classification
  if (typeof sessionName !== 'string') return false
  if (sessionName.trim().length === 0) return false
  if (typeof classification !== 'string') return false
  if (classification.trim().length === 0) return false
  return uses === 'mohist/acp-agent'
}

function readResolvedModel(session: WorkflowRunSession): string | null {
  const resolved = session.eventSummary?.resolvedModel
  if (typeof resolved === 'string' && resolved.trim().length > 0) return resolved
  if (typeof session.model === 'string' && session.model.trim().length > 0) return session.model
  return null
}

export function deriveMilestones(session: WorkflowRunSession | null | undefined): TaskLogMilestone[] {
  if (!session) return []
  const out: TaskLogMilestone[] = []

  const resolvedModel = readResolvedModel(session)
  if (resolvedModel) {
    const anchor = session.startedAt ?? session.createdAt ?? null
    if (anchor) {
      out.push({
        kind: 'model-bound',
        timestamp: anchor,
        label: 'Model bound',
        detail: resolvedModel,
      })
    }
  }

  if (session.completedAt) {
    const failed = typeof session.failureReason === 'string' && session.failureReason.length > 0
    const status = typeof session.status === 'string' && session.status.length > 0 ? session.status : 'unknown'
    const detail = failed ? `${status}\n${session.failureReason}` : status
    out.push({
      kind: 'session-ended',
      timestamp: session.completedAt,
      label: 'Session ended',
      detail,
      ...(failed ? { failed: true } : {}),
    })
  }

  return out
}

function compareMilestonesByTimestamp(a: TaskLogMilestone, b: TaskLogMilestone): number {
  if (a.timestamp < b.timestamp) return -1
  if (a.timestamp > b.timestamp) return 1
  return 0
}

export function mergeTimelineRows(lines: TaskLogLine[], milestones: TaskLogMilestone[]): TimelineRow[] {
  const sortedLines = lines.slice().sort((a, b) => a.seq - b.seq)
  const sortedMilestones = milestones.slice().sort(compareMilestonesByTimestamp)
  const rows: TimelineRow[] = []
  let milestoneIndex = 0

  for (const line of sortedLines) {
    while (
      milestoneIndex < sortedMilestones.length &&
      sortedMilestones[milestoneIndex].timestamp < line.timestamp
    ) {
      rows.push(sortedMilestones[milestoneIndex])
      milestoneIndex += 1
    }
    rows.push(line)
  }

  while (milestoneIndex < sortedMilestones.length) {
    rows.push(sortedMilestones[milestoneIndex])
    milestoneIndex += 1
  }

  return rows
}

export function serializeMilestoneForExport(milestone: TaskLogMilestone): string {
  return `${milestone.timestamp} [session] ${milestone.label}: ${milestone.detail}`
}
