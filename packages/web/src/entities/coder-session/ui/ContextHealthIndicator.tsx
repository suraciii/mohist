import { AlertTriangleIcon, CircleAlertIcon } from 'lucide-react'
import { cn } from '@/shared/lib/utils'
import { clampPercent, isContextHealthStatus, type ContextHealthStatus } from '../lib/context-health'

export interface ContextHealthIndicatorProps {
  contextWindowUsed?: number | null
  contextWindowSize?: number | null
  contextUsagePercent?: number | null
  /**
   * Server-provided health classification. When absent or invalid, the
   * indicator hides rather than fabricating a client-side classification.
   */
  healthStatus?: string | null
  className?: string
  /**
   * Render the absolute token count next to the percent. Defaults to
   * `false` so the compact form stays as tight as possible for session
   * list rows. The bar is always shown; the absolute label is opt-in.
   */
  showTokens?: boolean
  /**
   * Optional accessible label override. Defaults to a severity-aware
   * tooltip: a descriptive message for yellow/red ("Context window
   * NN% full — near limit" / "… at limit, compact or reset recommended")
   * and a simple "Context usage NN%" for healthy (green) usage so a
   * healthy session does not advertise a non-existent problem.
   */
  ariaLabel?: string
}

const DOT_CLASS: Record<ContextHealthStatus, string> = {
  green: 'bg-gray-400',
  yellow: 'bg-yellow-500',
  red: 'bg-red-500',
}

const TEXT_CLASS: Record<ContextHealthStatus, string> = {
  // Healthy usage is deliberately quiet: neutral gray, no warning
  // color, no background pill. The alert treatment reserved for
  // yellow/red signals "this needs attention".
  green: 'text-gray-600',
  yellow: 'text-yellow-800',
  red: 'text-red-800',
}

const CONTAINER_CLASS: Record<ContextHealthStatus, string> = {
  green: '',
  yellow: 'rounded px-1.5 bg-yellow-50 border border-yellow-300',
  red: 'rounded px-1.5 bg-red-50 border border-red-300',
}

const SEVERITY_LABEL: Record<ContextHealthStatus, 'ok' | 'warning' | 'critical'> = {
  green: 'ok',
  yellow: 'warning',
  red: 'critical',
}

/**
 * Build a severity-aware tooltip. Healthy usage keeps a simple
 * description; yellow/red communicate the situation directly so a user
 * hovering a list row understands the situation without opening the
 * session page.
 */
function buildSeverityTooltip(status: ContextHealthStatus, percent: number): string {
  const rounded = Math.round(percent)
  if (status === 'red') {
    return `Context window ${rounded}% full — at limit, compact or reset recommended`
  }
  if (status === 'yellow') {
    return `Context window ${rounded}% full — near limit`
  }
  return `Context usage ${rounded}%`
}

/**
 * Compact context-health badge for session list rows, Pulse compact
 * cards, and the session page. Renders a colored dot plus the current
 * usage percentage (and optionally a "X / Y" token label).
 *
 * Alert treatment is centralized here so every surface renders the
 * same warning behavior:
 *   - yellow (60 – 79.99%)  → warning glyph + role="status"
 *     + descriptive "near limit" tooltip
 *   - red    (>= 80%)       → error glyph + role="alert"
 *     + aria-live="polite" + descriptive "at limit" tooltip
 *   - green  (< 60%)        → quiet, neutral gray, no glyph / role / aria-live
 *   - missing / non-finite / non-positive-window → renders nothing
 *     so a session that has not yet received a `usage.updated` event
 *     does not display a misleading empty indicator.
 */
export function ContextHealthIndicator({
  contextWindowUsed,
  contextWindowSize,
  contextUsagePercent,
  healthStatus,
  className,
  showTokens = false,
  ariaLabel,
}: ContextHealthIndicatorProps) {
  if (contextUsagePercent == null || !Number.isFinite(contextUsagePercent)) return null
  if (!isContextHealthStatus(healthStatus)) return null

  const percent = clampPercent(contextUsagePercent)
  const status = healthStatus

  const label = `${Math.round(percent)}%`
  const description = ariaLabel ?? buildSeverityTooltip(status, percent)
  const severity = SEVERITY_LABEL[status]

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 text-[10px] font-medium',
        TEXT_CLASS[status],
        CONTAINER_CLASS[status],
        className,
      )}
      data-testid="context-health-indicator"
      data-status={status}
      data-severity={severity}
      title={description}
      aria-label={description}
      role={status === 'red' ? 'alert' : status === 'yellow' ? 'status' : undefined}
      aria-live={status === 'red' ? 'polite' : undefined}
    >
      {status === 'red' && (
        <CircleAlertIcon
          className="h-3 w-3 shrink-0"
          aria-hidden="true"
          data-testid="context-health-glyph"
        />
      )}
      {status === 'yellow' && (
        <AlertTriangleIcon
          className="h-3 w-3 shrink-0"
          aria-hidden="true"
          data-testid="context-health-glyph"
        />
      )}
      <span
        className={cn('inline-block h-1.5 w-1.5 rounded-full', DOT_CLASS[status])}
        aria-hidden="true"
      />
      <span className="tabular-nums">{label}</span>
      {showTokens && contextWindowUsed != null && contextWindowSize != null && (
        <span className="text-gray-500 font-normal ml-0.5">
          ({formatCompactPair(contextWindowUsed, contextWindowSize)})
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
