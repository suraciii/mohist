import { CompletionTrend } from './CompletionTrend'
import { CostTrendChart } from './CostTrendChart'
import { EpicProgressList } from './EpicProgressList'
import { InvestmentPanel } from './InvestmentPanel'
import { QualityPanel } from './QualityPanel'
import { SnapshotRow } from './SnapshotRow'
import { ThroughputChart } from './ThroughputChart'

export function ProductivityZone() {
  return (
    <div
      data-testid="productivity-zone"
      aria-label="Productivity zone"
      className="flex flex-col gap-4"
    >
      <SnapshotRow />
      <EpicProgressList />
      <CompletionTrend />
      <ThroughputChart />
      <QualityPanel />
      <InvestmentPanel />
      <CostTrendChart />
    </div>
  )
}
