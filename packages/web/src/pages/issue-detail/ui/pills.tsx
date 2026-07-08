import type { RuntimeSummary } from '../../../widgets/issue-workflow/model/derive-runtime-decision'
import { formatPriority, getPriorityStyle } from '../../../shared/lib/label-colors'

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

const runtimeSummaryPresentation: Record<RuntimeSummary, { label: string; className: string; dotClassName: string }> = {
  running: {
    label: 'Running',
    className: 'bg-info-subtle text-info border-info-border',
    dotClassName: 'bg-info animate-pulse',
  },
  queued: {
    label: 'Queued',
    className: 'bg-info-subtle text-info border-info-border',
    dotClassName: 'bg-info',
  },
  'approval-required': {
    label: 'Approval required',
    className: 'bg-warning-subtle text-warning border-warning-border',
    dotClassName: 'bg-warning',
  },
  blocked: {
    label: 'Blocked',
    className: 'bg-warning-subtle text-warning border-warning-border',
    dotClassName: 'bg-warning',
  },
  failed: {
    label: 'Failed',
    className: 'bg-danger-subtle text-danger border-danger-border',
    dotClassName: 'bg-danger',
  },
  done: {
    label: 'Done',
    className: 'bg-success-subtle text-success border-success-border',
    dotClassName: 'bg-success',
  },
}

export function RuntimeSummaryPill({ summary }: { summary: RuntimeSummary }) {
  const presentation = runtimeSummaryPresentation[summary]
  return (
    <span
      data-testid="runtime-status-pill"
      data-summary={summary}
      className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-semibold ${presentation.className}`}
    >
      <span className={`inline-block h-1.5 w-1.5 rounded-full ${presentation.dotClassName}`} />
      {presentation.label}
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
      className="inline-flex items-center gap-1 rounded-full bg-muted text-muted-foreground px-2 py-0.5 text-[10px] font-semibold"
      title="Archived - preserved execution history is still readable below"
    >
      <span className="inline-block h-1.5 w-1.5 rounded-full bg-muted-foreground/60" />
      Archived
    </span>
  )
}
