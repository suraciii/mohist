import { IssueHealth, WorkflowStage } from '../../../entities/issue'
import { statusLabel } from '../../../entities/issue/lib/status-badge'
import { getStageColors } from '../../../widgets/kanban-board/model/stage-colors'
import { formatPriority, getPriorityStyle } from '../../../shared/lib/label-colors'
import { WORKFLOW_STAGE_LABELS, stageToIssueStatus } from '../model/format'

export function PriorityChip({ priority }: { priority: string | null | undefined }) {
  if (!priority) return null
  const style = getPriorityStyle(priority)
  return (
    <span
      data-testid="priority-chip"
      className="inline-flex items-center rounded px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-wide"
      style={{ backgroundColor: style.bg, color: style.text }}
    >
      {formatPriority(priority)}
    </span>
  )
}

export function WorkflowStagePill({ stage }: { stage: WorkflowStage | undefined }) {
  if (!stage) return null
  const colors = getStageColors(stageToIssueStatus(stage))
  return (
    <span
      data-testid="workflow-stage-pill"
      className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold"
      style={{ backgroundColor: `${colors.accent}1a`, color: colors.accent }}
    >
      <span
        className="inline-block h-1.5 w-1.5 rounded-full"
        style={{ backgroundColor: colors.accent }}
      />
      {WORKFLOW_STAGE_LABELS[stage]}
    </span>
  )
}

export function HealthPill({ health }: { health: IssueHealth }) {
  const colorMap: Record<IssueHealth, { dot: string; bg: string; text: string }> = {
    [IssueHealth.Active]: { dot: '#22c55e', bg: '#dcfce7', text: '#15803d' },
    [IssueHealth.Paused]: { dot: '#eab308', bg: '#fef9c3', text: '#a16207' },
    [IssueHealth.Blocked]: { dot: '#ef4444', bg: '#fee2e2', text: '#b91c1c' },
    [IssueHealth.Interrupted]: { dot: '#f97316', bg: '#ffedd5', text: '#c2410c' },
    [IssueHealth.Cancelled]: { dot: '#9ca3af', bg: '#f3f4f6', text: '#6b7280' },
    [IssueHealth.Done]: { dot: '#22c55e', bg: '#dcfce7', text: '#15803d' },
  }
  const c = colorMap[health] ?? colorMap[IssueHealth.Active]
  return (
    <span
      data-testid="health-pill"
      className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold"
      style={{ backgroundColor: c.bg, color: c.text }}
    >
      <span
        className="inline-block h-1.5 w-1.5 rounded-full"
        style={{ backgroundColor: c.dot }}
      />
      {statusLabel(health)}
    </span>
  )
}

export function DraftPill() {
  return (
    <span
      data-testid="draft-pill"
      className="inline-flex items-center gap-1 rounded-full bg-muted text-muted-foreground px-2 py-0.5 text-[10px] font-semibold"
      title="This issue is still a draft and cannot be started yet"
    >
      <span className="inline-block h-1.5 w-1.5 rounded-full bg-muted-foreground/60" />
      Draft
    </span>
  )
}

export function ArchivedPill({ archivedAt }: { archivedAt: string | null | undefined }) {
  return (
    <span
      data-testid="archived-pill"
      data-archived-at={archivedAt ?? ''}
      className="inline-flex items-center gap-1 rounded-full bg-slate-100 text-slate-700 px-2 py-0.5 text-[10px] font-semibold"
      title="Archived — preserved execution history is still readable below"
    >
      <span className="inline-block h-1.5 w-1.5 rounded-full bg-slate-500" />
      Archived
    </span>
  )
}
