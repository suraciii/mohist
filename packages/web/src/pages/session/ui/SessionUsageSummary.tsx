import { formatCompact, formatCost } from '../../../shared/lib/format-compact'
import type { AgentSessionUsage } from '../../../entities/coder-session'
import { ContextHealthIndicator } from '../../../entities/coder-session'

export interface SessionUsageSummaryProps {
  usage?: AgentSessionUsage | null
}

function hasAnyUsage(usage: AgentSessionUsage): boolean {
  return (
    usage.inputTokens != null ||
    usage.outputTokens != null ||
    usage.totalTokens != null ||
    usage.cachedReadTokens != null ||
    usage.thoughtTokens != null ||
    usage.costAmount != null ||
    usage.contextWindowUsed != null
  )
}

function shouldShowToken(value: number | null | undefined): value is number {
  return value != null && value > 0
}

export function SessionUsageSummary({ usage }: SessionUsageSummaryProps) {
  if (!usage || !hasAnyUsage(usage)) return null

  const contextWindowPct =
    usage.contextUsagePercent != null
      ? Math.round(Math.max(0, Math.min(100, usage.contextUsagePercent)))
      : null

  return (
    <div className="border-b border-border bg-muted px-4 py-2" data-testid="session-usage-summary">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
        {/* Token detail */}
        <span className="text-muted-foreground font-medium" data-testid="usage-summary-tokens">
          Tokens:
          {usage.inputTokens != null && (
            <span className="ml-1 text-foreground" data-testid="usage-summary-input">{formatCompact(usage.inputTokens)} in</span>
          )}
          {usage.outputTokens != null && (
            <span className="ml-1 text-foreground" data-testid="usage-summary-output">· {formatCompact(usage.outputTokens)} out</span>
          )}
          {usage.totalTokens != null && (
            <span className="ml-1 text-foreground font-semibold" data-testid="usage-summary-total">· {formatCompact(usage.totalTokens)} total</span>
          )}
          {shouldShowToken(usage.cachedReadTokens) && (
            <span className="ml-1 text-muted-foreground" data-testid="usage-summary-cached">· {formatCompact(usage.cachedReadTokens)} cached</span>
          )}
          {shouldShowToken(usage.thoughtTokens) && (
            <span className="ml-1 text-muted-foreground" data-testid="usage-summary-thought">· {formatCompact(usage.thoughtTokens)} thought</span>
          )}
        </span>

        {/* Cost */}
        {usage.costAmount != null && usage.costCurrency != null && (
          <span className="text-muted-foreground" data-testid="usage-summary-cost">
            {formatCost(usage.costAmount, usage.costCurrency)}
          </span>
        )}

        {/* Context window */}
        {usage.contextWindowUsed != null && (
          <span className="text-muted-foreground" data-testid="usage-summary-context">
            Context:
            <span className="ml-1 text-foreground">
              {usage.contextWindowSize != null
                ? `${formatCompact(usage.contextWindowUsed)} / ${formatCompact(usage.contextWindowSize)}`
                : `${formatCompact(usage.contextWindowUsed)} used`}
            </span>
            {contextWindowPct != null && (
              <span className="ml-1 text-muted-foreground/70">({contextWindowPct}%)</span>
            )}
          </span>
        )}

        {/* Health status */}
        {usage.contextUsagePercent != null && (
          <span data-testid="usage-summary-health">
            <ContextHealthIndicator
              contextWindowUsed={usage.contextWindowUsed}
              contextWindowSize={usage.contextWindowSize}
              contextUsagePercent={usage.contextUsagePercent}
              healthStatus={usage.healthStatus}
            />
          </span>
        )}
      </div>
    </div>
  )
}
