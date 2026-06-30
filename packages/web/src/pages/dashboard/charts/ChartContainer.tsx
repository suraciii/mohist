import type { ReactNode } from 'react'

export type ChartStatus = 'loading' | 'error' | 'empty' | 'resolved'

export interface ChartContainerProps {
  status: ChartStatus
  emptyAction: ReactNode
  errorMessage?: string
  loadingMessage?: string
  children: ReactNode
}

export function ChartContainer({
  status,
  emptyAction,
  errorMessage = 'Failed to load chart data.',
  loadingMessage = 'Loading chart data\u2026',
  children,
}: ChartContainerProps) {
  if (status === 'loading') {
    return (
      <div
        role="status"
        aria-live="polite"
        data-testid="chart-container-loading"
        className="rounded-lg border border-border bg-card/50 p-4 min-h-[200px] flex items-center justify-center"
      >
        <p className="text-sm text-muted-foreground">{loadingMessage}</p>
      </div>
    )
  }

  if (status === 'error') {
    return (
      <div
        role="status"
        aria-live="polite"
        data-testid="chart-container-error"
        className="rounded-lg border border-border bg-card/50 p-4 min-h-[200px] flex items-center justify-center"
      >
        <p className="text-sm text-destructive">{errorMessage}</p>
      </div>
    )
  }

  if (status === 'empty') {
    return (
      <div data-testid="chart-container-empty" className="rounded-lg border border-border bg-card/50 p-4 min-h-[200px] flex items-center justify-center">
        {emptyAction}
      </div>
    )
  }

  return <>{children}</>
}
