import type { ReactNode } from 'react'

export type LegendShape = 'bar' | 'line' | 'dashedLine' | 'dot'

export interface LegendEntry {
  label: string
  shape: LegendShape
  className: string
}

export interface ChartLegendProps {
  entries: LegendEntry[]
}

function LegendSwatch({ shape, className }: { shape: LegendShape; className: string }) {
  switch (shape) {
    case 'bar': {
      return (
        <span className="inline-block w-3 h-3 mr-1.5 align-middle" aria-hidden="true">
          <svg width="12" height="12" viewBox="0 0 12 12">
            <rect x="1" y="2" width="10" height="9" className={className} rx={1} />
          </svg>
        </span>
      )
    }
    case 'line': {
      return (
        <span className="inline-block w-4 h-3 mr-1.5 align-middle" aria-hidden="true">
          <svg width="16" height="12" viewBox="0 0 16 12">
            <polyline
              points="0,10 5,4 11,7 15,2"
              fill="none"
              strokeWidth={2}
              strokeLinecap="round"
              strokeLinejoin="round"
              className={className}
            />
            <circle cx="5" cy="4" r="2" fill="none" className={className} />
            <circle cx="11" cy="7" r="2" fill="none" className={className} />
            <circle cx="15" cy="2" r="2" fill="none" className={className} />
          </svg>
        </span>
      )
    }
    case 'dashedLine': {
      return (
        <span className="inline-block w-4 h-3 mr-1.5 align-middle" aria-hidden="true">
          <svg width="16" height="12" viewBox="0 0 16 12">
            <polyline
              points="0,10 5,4 11,7 15,2"
              fill="none"
              strokeWidth={2}
              strokeDasharray="2 2"
              strokeLinecap="round"
              strokeLinejoin="round"
              className={className}
            />
          </svg>
        </span>
      )
    }
    case 'dot': {
      return (
        <span
          className={`inline-block w-2.5 h-2.5 rounded-full mr-1.5 align-middle ${className}`}
          aria-hidden="true"
        />
      )
    }
  }
}

export function ChartLegend({ entries }: ChartLegendProps): ReactNode {
  if (entries.length <= 1) return null

  return (
    <div
      data-testid="chart-legend"
      className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
    >
      {entries.map((entry) => (
        <span key={entry.label} className="tabular-nums flex items-center whitespace-nowrap">
          <LegendSwatch shape={entry.shape} className={entry.className} />
          {entry.label}
        </span>
      ))}
    </div>
  )
}
