import type { UsageSnapshot } from '../model/usage-snapshot'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'

interface UsageSnapshotLabelProps {
  snapshot: UsageSnapshot
}

export function UsageSnapshotLabel({ snapshot }: UsageSnapshotLabelProps) {
  const hasTokens = snapshot.inputTokens > 0 || snapshot.outputTokens > 0 || snapshot.totalTokens > 0
  const hasCost = snapshot.costAmount > 0 && snapshot.costCurrency != null

  return (
    <div data-testid="usage-snapshot-label" className="flex items-center gap-2">
      {hasTokens && (
        <span className="text-sm font-medium text-gray-700">
          {formatCompact(snapshot.totalTokens)} total tokens
        </span>
      )}
      {hasCost && (
        <span className="text-sm font-medium text-gray-700">
          {formatCost(snapshot.costAmount, snapshot.costCurrency)}
        </span>
      )}
      {!hasTokens && !hasCost && (
        <span className="text-sm text-gray-400">No usage data</span>
      )}
      <span className="text-xs text-gray-400 italic">activity window only</span>
    </div>
  )
}
