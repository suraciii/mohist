import type { ReactNode } from 'react'

export type DashboardZoneId = 'attention' | 'pulse' | 'digest'

interface DashboardZoneProps {
  id: DashboardZoneId
  name: string
  children?: ReactNode
}

export function DashboardZone({ id, name, children }: DashboardZoneProps) {
  return (
    <section
      data-testid={`dashboard-zone-${id}`}
      data-zone={id}
      aria-label={name}
      className="rounded-lg border border-dashed border-muted-foreground/30 bg-muted/20 min-h-[160px] p-4"
    >
      {children}
    </section>
  )
}