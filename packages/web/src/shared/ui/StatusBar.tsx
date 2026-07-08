import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'

interface StatusBarProps {
  active: number
  waiting: number
  completed: number
  failed: number
  activeSlots: number
  maxSlots: number
  children?: React.ReactNode
}

interface StatusBarCount {
  key: 'active' | 'waiting' | 'completed' | 'failed'
  label: string
  treatment: StatusTreatment
}

export function StatusBar({ active, waiting, completed, failed, activeSlots, maxSlots, children }: StatusBarProps) {
  const values = { active, waiting, completed, failed }
  const counts: StatusBarCount[] = [
    { key: 'active', label: 'Active', treatment: statusTreatment('workflow-run', 'running') },
    { key: 'waiting', label: 'Waiting', treatment: statusTreatment('workflow-run', 'awaiting-approval') },
    { key: 'completed', label: 'Completed', treatment: statusTreatment('workflow-run', 'completed') },
    { key: 'failed', label: 'Failed', treatment: statusTreatment('workflow-run', 'failed') },
  ]

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 md:px-6 bg-background border-b border-border">
      {counts.map(({ key, label, treatment }) => (
        <span
          key={key}
          data-testid={`status-bar-${key}`}
          data-family={treatment.family}
          className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold ${treatment.container}`}
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