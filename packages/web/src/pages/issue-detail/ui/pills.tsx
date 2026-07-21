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
