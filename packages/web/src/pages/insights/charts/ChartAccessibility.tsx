import type { ReactNode } from 'react'
import { ChartLegend } from './ChartLegend'
import type { LegendEntry } from './ChartLegend'

export { type LegendEntry, type LegendShape } from './ChartLegend'

export interface ChartAccessibilityProps {
  ariaLabel: string
  summary: string
  legend: LegendEntry[]
  viewBox?: string
  className?: string
  children: ReactNode
}

export function ChartAccessibility({
  ariaLabel,
  summary,
  legend,
  viewBox = '0 0 500 300',
  className = 'w-full h-auto',
  children,
}: ChartAccessibilityProps) {
  return (
    <figure data-testid="chart-accessibility">
      <svg
        role="img"
        aria-label={ariaLabel}
        viewBox={viewBox}
        className={className}
      >
        {children}
      </svg>
      <figcaption className="sr-only" data-testid="chart-sr-summary">
        {summary}
      </figcaption>
      <ChartLegend entries={legend} />
    </figure>
  )
}
