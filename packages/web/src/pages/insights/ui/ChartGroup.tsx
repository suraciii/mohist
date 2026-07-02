import type { ReactNode } from 'react'

interface ChartGroupProps {
  id: 'output' | 'delivery' | 'quality' | 'investment'
  title: string
  question: string
  children: ReactNode
}

export function ChartGroup({ id, title, question, children }: ChartGroupProps) {
  return (
    <section
      data-testid="insights-chart-group"
      data-dimension={id}
      className="flex flex-col gap-3"
    >
      <header className="flex flex-col gap-1">
        <h3 className="text-base font-semibold text-foreground" data-testid="insights-chart-group-title">
          {title}
        </h3>
        <p className="text-sm text-muted-foreground" data-testid="insights-chart-group-question">
          {question}
        </p>
      </header>
      <div className="flex flex-col gap-4" data-testid="insights-chart-group-charts">
        {children}
      </div>
    </section>
  )
}