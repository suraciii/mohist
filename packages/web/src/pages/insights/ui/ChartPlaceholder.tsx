import { BarChart3Icon } from 'lucide-react'

export function ChartPlaceholder() {
  return (
    <div
      data-testid="insights-chart-placeholder"
      data-future="charts-m2"
      className="rounded-lg border border-dashed border-muted-foreground/30 bg-muted/30 px-4 py-10 text-center"
    >
      <BarChart3Icon className="mx-auto size-8 text-muted-foreground/60" aria-hidden="true" />
      <div className="mt-3 text-sm font-medium text-foreground">
        图表将在后续迁移
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        Signal Summary 之上，图表 M2 迁入
      </div>
    </div>
  )
}