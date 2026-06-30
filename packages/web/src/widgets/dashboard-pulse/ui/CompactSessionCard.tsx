import { Link } from 'react-router-dom'
import { useProjectPath } from '@/entities/project'
import { ContextHealthIndicator, ContextUsageTrendMiniChart, type ContextUsageTrendSample } from '@/widgets/session-health'
import { formatCompact, formatCost } from '@/shared/lib/format-compact'
import type { SessionCard } from '@/widgets/coder-session/model/activity-cards'

const STAGE_COLORS: Record<string, string> = {
  build: 'bg-purple-100 text-purple-700',
  plan: 'bg-blue-100 text-blue-700',
  review: 'bg-teal-100 text-teal-700',
  check: 'bg-orange-100 text-orange-700',
  integrate: 'bg-slate-100 text-slate-700',
}

const LINE_CLAMP_STYLE = {
  display: '-webkit-box',
  WebkitLineClamp: 1,
  WebkitBoxOrient: 'vertical' as const,
  overflow: 'hidden',
}

export interface CompactSessionCardProps {
  card: SessionCard
}

export function CompactSessionCard({ card }: CompactSessionCardProps) {
  const toProjectPath = useProjectPath()
  const stageColor = STAGE_COLORS[card.issueStage.toLowerCase()] ?? 'bg-gray-100 text-gray-700'
  const title = card.title ?? card.taskDescription ?? card.issueTitle
  const taskProgressPercent = card.taskProgress
    ? getTaskProgressPercent(card.taskProgress.completed, card.taskProgress.total)
    : null

  return (
    <Link
      to={toProjectPath(`/issues/${card.issueNumber}`)}
      data-testid="pulse-compact-card"
      data-issue-number={card.issueNumber}
      className="block rounded-lg border border-gray-200 bg-white shadow-sm hover:border-gray-300 hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center gap-2 mb-1.5">
          <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
          <span className="text-xs font-mono text-gray-400">#{card.issueNumber}</span>
          <span
            className={`inline-flex items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${stageColor}`}
            data-testid="pulse-compact-stage"
          >
            {card.issueStage}
          </span>
        </div>

        <h3
          className="text-sm font-medium text-gray-900"
          style={LINE_CLAMP_STYLE}
          title={title}
          data-testid="pulse-compact-title"
        >
          {title}
        </h3>

        {(card.totalTokens != null || card.costAmount != null) && (
          <p
            className="mt-1 text-xs text-gray-500"
            style={LINE_CLAMP_STYLE}
            data-testid="pulse-compact-usage"
          >
            <UsageLine card={card} />
          </p>
        )}

        {card.taskProgress && (
          <div className="mt-1.5" data-testid="pulse-compact-progress">
            <div className="flex items-center justify-between mb-0.5">
              <span className="text-[10px] text-gray-400 tabular-nums">
                {card.taskProgress.completed}/{card.taskProgress.total} tasks
              </span>
            </div>
            <div className="h-1 rounded-full bg-gray-100 overflow-hidden">
              <div
                className="h-full rounded-full bg-blue-500 transition-all duration-300"
                style={{
                  width: `${taskProgressPercent}%`,
                }}
              />
            </div>
          </div>
        )}

        {card.contextWindowSize != null && card.contextWindowSize > 0 && (
          <div className="mt-1.5">
            <ContextHealthIndicator
              contextWindowUsed={card.contextWindowUsed ?? null}
              contextWindowSize={card.contextWindowSize ?? null}
              contextUsagePercent={card.contextUsagePercent ?? null}
            />
          </div>
        )}

        {hasTrendData(card.contextUsageHistory) && (
          <div className="mt-1" data-testid="pulse-compact-trend">
            <ContextUsageTrendMiniChart history={card.contextUsageHistory} />
          </div>
        )}
      </div>
    </Link>
  )
}

function getTaskProgressPercent(completed: number, total: number): number {
  if (total <= 0) return 0
  return Math.max(0, Math.min(100, (completed / total) * 100))
}

/**
 * Count the finite-numbered samples in a usage history. The chart
 * needs at least two such samples to plot a meaningful trend, so the
 * Pulse card hides its trend block entirely when this returns `< 2`
 * (no empty-axis visual to compete with the snapshot indicator).
 */
function countUsableHistorySamples(
  history: ContextUsageTrendSample[] | null | undefined,
): number {
  if (!history) return 0
  let n = 0
  for (const sample of history) {
    if (typeof sample?.percent === 'number' && Number.isFinite(sample.percent)) n += 1
  }
  return n
}

function hasTrendData(history: ContextUsageTrendSample[] | null | undefined): boolean {
  return countUsableHistorySamples(history) >= 2
}

function UsageLine({ card }: { card: SessionCard }) {
  const tokens = card.totalTokens != null ? `${formatCompact(card.totalTokens)} tok` : null
  const cost = card.costAmount != null ? formatCost(card.costAmount, card.costCurrency) : null
  const parts = [tokens, cost].filter((p): p is string => !!p)
  if (parts.length === 0) return null
  const joined = parts.join(' · ')
  return (
    <span className="tabular-nums" title={joined}>
      {joined}
    </span>
  )
}
