import type { StatusKind } from '../data/SessionDataSource'

const sessionStatusPresentation: Partial<
  Record<StatusKind, { label: string; className: string; dotClassName?: string; withDot?: boolean }>
> = {
  active: {
    label: 'Active',
    className: 'bg-info-subtle text-info border-info-border',
    dotClassName: 'bg-info',
    withDot: true,
  },
  idle: {
    label: 'Idle',
    className: 'bg-muted text-muted-foreground border-border',
    dotClassName: 'bg-muted-foreground/60',
  },
  unknown: {
    label: 'Unknown',
    className: 'bg-warning-subtle text-warning border-warning-border',
    dotClassName: 'bg-warning',
  },
  recovering: {
    label: 'Recovering',
    className: 'bg-warning-subtle text-warning border-warning-border',
    dotClassName: 'bg-warning',
  },
}

export function StatusBadge({ kind }: { kind: StatusKind }) {
  const presentation = sessionStatusPresentation[kind] ?? sessionStatusPresentation.unknown!
  const { label, className, dotClassName, withDot } = presentation
  return (
    <span
      data-testid="session-status-badge"
      data-status-kind={kind}
      data-tone={
        className.startsWith('bg-danger')
          ? 'danger'
          : className.startsWith('bg-warning')
            ? 'warning'
            : className.startsWith('bg-success')
              ? 'success'
              : className.startsWith('bg-info')
                ? 'info'
                : 'neutral'
      }
      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium border ${className}`}
    >
      {withDot && dotClassName && (
        <span className="relative flex h-2 w-2">
          <span className={`animate-ping absolute inline-flex h-full w-full rounded-full opacity-75 ${dotClassName}`} />
          <span className={`relative inline-flex rounded-full h-2 w-2 ${dotClassName}`} />
        </span>
      )}
      {label}
    </span>
  )
}
