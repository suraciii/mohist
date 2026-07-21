import type { TaskLogLine } from '../../../entities/issue'
import type { WorkflowRunSession } from '../../../entities/coder-session'

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

/**
 * Returns true when the task is an inline-agent task for the web
 * milestone classifier. Agent-job tasks are not routed through this
 * classifier.
 */
export function isInlineAgentTask(input: AgentTaskIdentity | null | undefined): boolean {
  if (!input) return false
  const uses = input.origin?.uses
  const sessionName = input.sessionName
  if (typeof sessionName !== 'string') return false
  if (sessionName.trim().length === 0) return false
  return uses === 'mohist/opencode' || uses === 'mohist/pi'
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

function compareTimelineRows(
  a: { row: TimelineRow; index: number },
  b: { row: TimelineRow; index: number },
): number {
  if (a.row.timestamp < b.row.timestamp) return -1
  if (a.row.timestamp > b.row.timestamp) return 1

  if (!isTaskLogMilestone(a.row) && !isTaskLogMilestone(b.row)) return a.row.seq - b.row.seq
  const aIsMilestone = isTaskLogMilestone(a.row)
  const bIsMilestone = isTaskLogMilestone(b.row)
  if (aIsMilestone !== bIsMilestone) return aIsMilestone ? 1 : -1
  return a.index - b.index
}

function compareOpsBySeq(a: TaskLogLine, b: TaskLogLine): number {
  if (a.seq < b.seq) return -1
  if (a.seq > b.seq) return 1
  return 0
}

export function mergeTimelineRows(lines: TaskLogLine[], milestones: TaskLogMilestone[]): TimelineRow[] {
  if (milestones.length === 0) return lines.slice().sort(compareOpsBySeq)

  return ([...lines, ...milestones] as TimelineRow[])
    .map((row, index) => ({ row, index }))
    .sort(compareTimelineRows)
    .map((entry) => entry.row)
}

export function serializeMilestoneForExport(milestone: TaskLogMilestone): string {
  return `${milestone.timestamp} [session] ${milestone.label}: ${milestone.detail}`
}
