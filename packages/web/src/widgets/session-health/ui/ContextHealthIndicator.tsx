import { cn } from '@/shared/lib/utils'
import { resolveContextUsage, type ContextHealthStatus } from '../model/context-health'

export interface ContextHealthIndicatorProps {
  contextWindowUsed?: number | null
  contextWindowSize?: number | null
  contextUsagePercent?: number | null
  className?: string
  /**
   * Render the absolute token count next to the percent. Defaults to
   * `false` so the compact form stays as tight as possible for session
   * list rows. The bar is always shown; the absolute label is opt-in.
   */
  showTokens?: boolean
  /**
   * Optional accessible label override. Defaults to a constructed
   * "Context usage NN%" string that screen readers can announce.
   */
  ariaLabel?: string
}

const DOT_CLASS: Record<ContextHealthStatus, string> = {
  green: 'bg-green-500',
  yellow: 'bg-yellow-500',
  red: 'bg-red-500',
}

const TEXT_CLASS: Record<ContextHealthStatus, string> = {
  green: 'text-green-700',
  yellow: 'text-yellow-700',
  red: 'text-red-700',
}

/**
 * Compact context-health badge for session list rows. Renders a colored
 * dot plus the current usage percentage (and optionally a "X / Y" token
 * label). Hidden entirely when no context data is available so that
 * sessions that have not yet received a `usage.updated` event do not
 * show a misleading empty indicator.
 */
export function ContextHealthIndicator({
  contextWindowUsed,
  contextWindowSize,
  contextUsagePercent,
  className,
  showTokens = false,
  ariaLabel,
}: ContextHealthIndicatorProps) {
  const usage = resolveContextUsage({ contextWindowUsed, contextWindowSize, contextUsagePercent })

  if (usage.percent == null || usage.status == null) {
    return null
  }

  const label = `${Math.round(usage.percent)}%`
  const description = ariaLabel ?? `Context usage ${label}`

  return (
    <span
      className={cn('inline-flex items-center gap-1 text-[10px] font-medium', TEXT_CLASS[usage.status], className)}
      data-testid="context-health-indicator"
      data-status={usage.status}
      title={description}
      aria-label={description}
    >
      <span
        className={cn('inline-block h-1.5 w-1.5 rounded-full', DOT_CLASS[usage.status])}
        aria-hidden="true"
      />
      <span className="tabular-nums">{label}</span>
      {showTokens && usage.used != null && usage.size != null && (
        <span className="text-gray-500 font-normal ml-0.5">
          ({formatCompactPair(usage.used, usage.size)})
        </span>
      )}
    </span>
  )
}

function formatCompactPair(used: number, size: number): string {
  const compact = (n: number) => {
    const abs = Math.abs(n)
    if (abs >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
    if (abs >= 1_000) return `${(n / 1_000).toFixed(1)}k`
    return String(n)
  }
  return `${compact(used)}/${compact(size)}`
}
