import { CompletionTrend } from './CompletionTrend'
import { EpicProgressList } from './EpicProgressList'
import { InvestmentPanel } from './InvestmentPanel'
import { SnapshotRow } from './SnapshotRow'

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
      <InvestmentPanel />
    </div>
  )
}