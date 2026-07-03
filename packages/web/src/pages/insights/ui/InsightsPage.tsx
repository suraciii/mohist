import { useState } from 'react'
import {
  useCompletionThroughput,
  useDeliveryTime,
  useQualityMetrics,
  useStageDuration,
} from '../../../entities/issue'
import { useCostRollup } from '../../../entities/agent'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { DEFAULT_INSIGHTS_RANGE, type InsightsRange } from '../model/insights-range'
import { SignalSummary } from './SignalSummary'
import { InsightsCharts } from './InsightsCharts'
import { InsightsRangeSelector } from './InsightsRangeSelector'

export function InsightsPage() {
  useDocumentTitle('Insights — Mohist')

  const [range, setRange] = useState<InsightsRange>(DEFAULT_INSIGHTS_RANGE)

  const completion = useCompletionThroughput(range)
  const deliveryTime = useDeliveryTime(range)
  const quality = useQualityMetrics(range)
  const cost = useCostRollup(range)
  const stageDuration = useStageDuration(range)

  return (
    <div
      data-testid="insights-page"
      data-range={range}
      className="flex-1 overflow-y-auto p-4 md:p-6"
    >
      <div className="flex flex-col gap-4 md:gap-6 max-w-5xl mx-auto w-full">
        <header className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h1 className="text-xl font-bold text-foreground" data-testid="insights-title">
              Insights
            </h1>
            <p className="text-sm text-muted-foreground" data-testid="insights-subtitle">
              最近做得怎么样——先看结论，再看图表。
            </p>
          </div>
          <InsightsRangeSelector value={range} onChange={setRange} />
        </header>

        <section
          data-testid="insights-signal-section"
          className="flex flex-col gap-3"
        >
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted-foreground">
            Signal Summary
          </h2>
          <SignalSummary
            completion={completion.data}
            deliveryTime={deliveryTime.data}
            quality={quality.data}
            cost={cost.data}
            stageDuration={stageDuration.data}
          />
        </section>

        <section
          data-testid="insights-charts-section"
          className="flex flex-col gap-3"
        >
          <h2 className="text-sm font-semibold uppercase tracking-wide text-muted-foreground">
            Charts
          </h2>
          <InsightsCharts range={range} />
        </section>
      </div>
    </div>
  )
}