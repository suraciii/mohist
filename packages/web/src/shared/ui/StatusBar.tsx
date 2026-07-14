interface StatusBarProps {
  active: number
  waiting: number
  completed: number
  failed: number
  activeSlots: number
  maxSlots: number
  children?: React.ReactNode
}

const counts = [
  { key: 'active', label: 'Active', color: 'bg-info-subtle text-info border border-info-border', tone: 'info' as const },
  { key: 'waiting', label: 'Waiting', color: 'bg-warning-subtle text-warning border border-warning-border', tone: 'warning' as const },
  { key: 'completed', label: 'Completed', color: 'bg-success-subtle text-success border border-success-border', tone: 'success' as const },
  { key: 'failed', label: 'Failed', color: 'bg-danger-subtle text-danger border border-danger-border', tone: 'danger' as const },
] as const

export function StatusBar({ active, waiting, completed, failed, activeSlots, maxSlots, children }: StatusBarProps) {
  const values = { active, waiting, completed, failed }

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 md:px-6 bg-background border-b border-border">
      {counts.map(({ key, label, color, tone }) => (
        <span
          key={key}
          data-testid={`status-bar-${key}`}
          data-tone={tone}
          className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold ${color}`}
        >
          <span>{label}:</span>
          <span>{values[key]}</span>
        </span>
      ))}
      {children}
      <span className="ml-auto text-xs text-muted-foreground font-medium">
        {activeSlots}/{maxSlots} slots used
      </span>
    </div>
  )
}
