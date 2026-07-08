import type { ReactNode } from 'react'

export type DashboardZoneId = 'pulse' | 'digest'

interface DashboardZoneProps {
  id: DashboardZoneId
  name: string
  children?: ReactNode
}

/**
 * Wrapper for a dashboard zone that simply adds the standard zone chrome
 * (border, rounded corners, padding) and the `dashboard-zone-{id}` test
 * contract. Zones size to their content; an empty zone does not reserve
 * a fixed-height box — when there is no content, the parent page should
 * skip rendering this wrapper altogether.
 */
export function DashboardZone({ id, name, children }: DashboardZoneProps) {
  return (
    <section
      data-testid={`dashboard-zone-${id}`}
      data-zone={id}
      aria-label={name}
      className="rounded-lg border border-dashed border-muted-foreground/30 bg-muted/20 p-4"
    >
      {children}
    </section>
  )
}