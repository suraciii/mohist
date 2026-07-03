import type { InsightsRange } from '../model/insights-range'
import { INSIGHTS_RANGES } from '../model/insights-range'

interface InsightsRangeSelectorProps {
  value: InsightsRange
  onChange: (next: InsightsRange) => void
}

export function InsightsRangeSelector({ value, onChange }: InsightsRangeSelectorProps) {
  return (
    <div
      role="group"
      aria-label="Insights time range"
      data-testid="insights-range-selector"
      className="inline-flex rounded-md border border-border bg-card/30 text-xs"
    >
      {INSIGHTS_RANGES.map((range, index) => {
        const active = range === value
        return (
          <button
            key={range}
            type="button"
            data-testid={`insights-range-option-${range}`}
            data-active={active ? 'true' : 'false'}
            aria-pressed={active}
            onClick={() => onChange(range)}
            className={
              `px-2.5 py-1 transition-colors ` +
              (index === 0 ? 'rounded-l-md ' : '') +
              (index === INSIGHTS_RANGES.length - 1 ? 'rounded-r-md ' : '') +
              (index > 0 ? '-ml-px ' : '') +
              (active
                ? 'bg-chart-2 text-background'
                : 'text-muted-foreground hover:text-foreground')
            }
          >
            {range}
          </button>
        )
      })}
    </div>
  )
}