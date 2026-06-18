export type DashboardZoneId = 'attention' | 'pulse' | 'productivity' | 'digest'

interface DashboardZonePlaceholderProps {
  id: DashboardZoneId
  name: string
}

export function DashboardZonePlaceholder({ id, name }: DashboardZonePlaceholderProps) {
  return (
    <section
      data-testid={`dashboard-zone-${id}`}
      data-zone={id}
      aria-label={name}
      className="rounded-lg border border-dashed border-muted-foreground/30 bg-muted/20 min-h-[160px] p-4"
    />
  )
}
