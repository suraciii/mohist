import { useState } from 'react'
import { cn } from '@/shared/lib/utils'
import { Button } from '@/shared/ui/components/button'
import { formatCompact } from '@/shared/lib/format-compact'
import { clampPercent, isContextHealthStatus, type ContextHealthStatus } from '@/entities/coder-session'

export interface ContextHealthBarProps {
  contextWindowUsed?: number | null
  contextWindowSize?: number | null
  contextUsagePercent?: number | null
  /**
   * Server-provided health classification. When absent or invalid, the
   * bar hides rather than fabricating a client-side classification.
   */
  healthStatus?: string | null
  onCompact?: () => void
  onReset?: () => void
  compactLabel?: string
  resetLabel?: string
  className?: string
  /**
   * When false, the warning banner is suppressed even at high usage.
   * Defaults to true. The auto-dismiss is automatic — the banner
   * disappears the moment usage drops below 80% as a new snapshot is
   * received (either via SSE or props).
   */
  showWarning?: boolean
}

const STATUS_BAR_CLASS: Record<ContextHealthStatus, string> = {
  green: 'bg-green-500',
  yellow: 'bg-yellow-500',
  red: 'bg-red-500',
}

const STATUS_DOT_CLASS: Record<ContextHealthStatus, string> = {
  green: 'bg-green-500',
  yellow: 'bg-yellow-500',
  red: 'bg-red-500',
}

/**
 * Visual progress bar for the agent session's context window usage.
 *
 * Renders the standard "used / total (percent%)" label and a fill
 * bar whose colour reflects the current health threshold. Hidden
 * entirely when no context data is available (e.g. contextWindowSize
 * is zero or missing) so the page does not show a misleading empty
 * bar. When usage is 80% or higher, a warning banner with Compact
 * and Reset action affordances is shown above the bar; the banner
 * auto-dismisses the moment usage drops below 80% in a subsequent
 * snapshot.
 */
export function ContextHealthBar({
  contextWindowUsed,
  contextWindowSize,
  contextUsagePercent,
  healthStatus,
  onCompact,
  onReset,
  compactLabel = 'Compact',
  resetLabel = 'Reset',
  className,
  showWarning = true,
}: ContextHealthBarProps) {
  const [warningDismissed, setWarningDismissed] = useState(false)

  if (contextUsagePercent == null || !Number.isFinite(contextUsagePercent)) return null
  if (!isContextHealthStatus(healthStatus)) return null

  const percent = clampPercent(contextUsagePercent)
  const status = healthStatus

  const label = formatUsageLabel(contextWindowUsed ?? null, contextWindowSize ?? null, percent)
  const showWarningBanner = showWarning
    && percent >= 80
    && (onCompact != null || onReset != null)
    && !warningDismissed

  return (
    <div className={cn('flex flex-col gap-2', className)}>
      {showWarningBanner && (
        <div
          className="flex items-start gap-2 rounded-md border border-red-300 bg-red-50 px-3 py-2 text-xs text-red-800"
          role="status"
          aria-live="polite"
        >
          <svg
            className="h-4 w-4 shrink-0 text-red-500"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z"
              clipRule="evenodd"
            />
          </svg>
          <div className="flex-1 min-w-0 space-y-1">
            <div className="font-medium">
              Context window is at {Math.round(percent)}% capacity. Compact or reset the
              session to recover headroom before continuing.
            </div>
            <div className="flex flex-wrap items-center gap-2">
              {onCompact != null && (
                <Button
                  variant="link"
                  size="sm"
                  onClick={onCompact}
                  className="h-auto p-0 text-xs font-medium text-red-700 hover:text-red-900"
                >
                  {compactLabel}
                </Button>
              )}
              {onCompact != null && onReset != null && (
                <span className="text-red-300">·</span>
              )}
              {onReset != null && (
                <Button
                  variant="link"
                  size="sm"
                  onClick={onReset}
                  className="h-auto p-0 text-xs font-medium text-red-700 hover:text-red-900"
                >
                  {resetLabel}
                </Button>
              )}
              <Button
                variant="link"
                size="sm"
                onClick={() => setWarningDismissed(true)}
                className="h-auto p-0 text-xs font-normal text-red-500 hover:text-red-700"
                aria-label="Dismiss context warning"
              >
                Dismiss
              </Button>
            </div>
          </div>
        </div>
      )}

      <div className="flex flex-col gap-1" data-testid="context-health-bar" data-status={status}>
        <div className="flex items-center gap-2 text-xs">
          <span className={cn('inline-block h-2 w-2 rounded-full', STATUS_DOT_CLASS[status])} aria-hidden="true" />
          <span className="font-mono text-gray-700" data-testid="context-health-label">{label}</span>
        </div>
        <div className="h-1.5 w-full rounded-full bg-gray-200 overflow-hidden">
          <div
            className={cn('h-full rounded-full transition-all duration-300', STATUS_BAR_CLASS[status])}
            style={{ width: `${percent}%` }}
            data-testid="context-health-fill"
            data-percent={Math.round(percent)}
          />
        </div>
      </div>
    </div>
  )
}

function formatUsageLabel(used: number | null, size: number | null, percent: number): string {
  if (used != null && size != null) {
    return `${formatCompact(used)} / ${formatCompact(size)} tokens (${Math.round(percent)}%)`
  }
  if (used != null) {
    return `${formatCompact(used)} tokens (${Math.round(percent)}%)`
  }
  return `${Math.round(percent)}% of context window`
}
