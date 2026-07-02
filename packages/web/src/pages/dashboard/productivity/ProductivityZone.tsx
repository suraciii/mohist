import { CompletionTrend } from './CompletionTrend'
import { CostTrendChart } from './CostTrendChart'
import { CumulativeFlowChart } from './CumulativeFlowChart'
import { CycleTimeChart } from './CycleTimeChart'
import { EpicProgressList } from './EpicProgressList'
import { FtrTrendChart } from './FtrTrendChart'
import { InvestmentPanel } from './InvestmentPanel'
import { QualityPanel } from './QualityPanel'
import { StageDurationChart } from './StageDurationChart'
import { ThroughputChart } from './ThroughputChart'

export function ProductivityZone() {
  return (
    <div
      data-testid="productivity-zone"
      aria-label="Productivity zone"
      className="flex flex-col gap-4"
    >
      <EpicProgressList />
      <CompletionTrend />
      <ThroughputChart />
      <CycleTimeChart />
      <StageDurationChart />
      <CumulativeFlowChart />
      <QualityPanel />
      <FtrTrendChart />
      <InvestmentPanel />
      <CostTrendChart />
    </div>
  )
}
