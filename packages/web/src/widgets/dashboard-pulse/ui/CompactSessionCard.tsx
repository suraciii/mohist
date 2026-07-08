import { Link } from 'react-router-dom'
import { useProjectPath } from '@/entities/project'
import { ContextHealthIndicator, ContextUsageTrendMiniChart, type ContextUsageTrendSample } from '@/widgets/session-health'
import { formatCompact, formatCost } from '@/shared/lib/format-compact'
import type { SessionCard } from '@/widgets/coder-session/model/activity-cards'

export const STAGE_COLORS: Record<string, string> = {
  build: 'bg-purple-100 text-purple-700 border-purple-200 dark:bg-purple-900/40 dark:text-purple-200 dark:border-purple-800',
  plan: 'bg-blue-100 text-blue-700 border-blue-200 dark:bg-blue-900/40 dark:text-blue-200 dark:border-blue-800',
  review: 'bg-teal-100 text-teal-700 border-teal-200 dark:bg-teal-900/40 dark:text-teal-200 dark:border-teal-800',
  check: 'bg-orange-100 text-orange-700 border-orange-200 dark:bg-orange-900/40 dark:text-orange-200 dark:border-orange-800',
  integrate: 'bg-slate-100 text-slate-700 border-slate-200 dark:bg-slate-800/60 dark:text-slate-200 dark:border-slate-700',
}

const STAGE_COLOR_FALLBACK = 'bg-muted text-muted-foreground border-border dark:bg-muted/40'

const LINE_CLAMP_STYLE = {
  display: '-webkit-box',
  WebkitLineClamp: 1,
  WebkitBoxOrient: 'vertical' as const,
  overflow: 'hidden',
}

const OWNER_ACTION_CLASS = 'border border-danger-border bg-danger-subtle text-danger'

export function stageColorFor(stage: string | null | undefined): string {
  if (!stage) return STAGE_COLOR_FALLBACK
  return STAGE_COLORS[stage.toLowerCase()] ?? STAGE_COLOR_FALLBACK
}

export interface CompactSessionCardProps {
  card: SessionCard
  needsOwnerAction?: boolean
}

export function CompactSessionCard({ card, needsOwnerAction = false }: CompactSessionCardProps) {
  const toProjectPath = useProjectPath()
  const stageColor = stageColorFor(card.issueStage)
  const title = card.title ?? card.taskDescription ?? card.issueTitle
  const taskProgressPercent = card.taskProgress
    ? getTaskProgressPercent(card.taskProgress.completed, card.taskProgress.total)
    : null

  return (
    <Link
      to={toProjectPath(`/issues/${card.issueNumber}`)}
      data-testid="pulse-compact-card"
      data-issue-number={card.issueNumber}
      className="block rounded-lg border border-border bg-card shadow-sm hover:border-muted-foreground/40 hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <RunningIssueHeader
          issueNumber={card.issueNumber}
          stage={card.issueStage}
          stageColor={stageColor}
          showLiveDot
          needsOwnerAction={needsOwnerAction}
        />

        <h3
          className="text-sm font-medium text-foreground"
          style={LINE_CLAMP_STYLE}
          title={title}
          data-testid="pulse-compact-title"
        >
          {title}
        </h3>

        {(card.totalTokens != null || card.costAmount != null) && (
          <p
            className="mt-1 text-xs text-muted-foreground"
            style={LINE_CLAMP_STYLE}
            data-testid="pulse-compact-usage"
          >
            <UsageLine card={card} />
          </p>
        )}

        {card.taskProgress && (
          <div className="mt-1.5" data-testid="pulse-compact-progress">
            <div className="flex items-center justify-between mb-0.5">
              <span className="text-[10px] text-muted-foreground tabular-nums">
                {card.taskProgress.completed}/{card.taskProgress.total} tasks
              </span>
            </div>
            <div className="h-1 rounded-full bg-muted overflow-hidden">
              <div
                className="h-full rounded-full bg-info transition-all duration-300"
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
              healthStatus={card.healthStatus ?? null}
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

export interface RunningIssueHeaderProps {
  issueNumber: string | number
  stage: string | null | undefined
  stageColor: string
  showLiveDot: boolean
  needsOwnerAction: boolean
}

export function RunningIssueHeader({
  issueNumber,
  stage,
  stageColor,
  showLiveDot,
  needsOwnerAction,
}: RunningIssueHeaderProps) {
  return (
    <div className="flex items-center gap-2 mb-1.5">
      {showLiveDot && (
        <span className="inline-block h-2 w-2 rounded-full bg-info animate-pulse" />
      )}
      {!showLiveDot && (
        <span
          className="inline-block h-2 w-2 rounded-full bg-muted-foreground/40"
          data-testid="pulse-compact-paused-dot"
          aria-hidden
        />
      )}
      <span className="text-xs font-mono text-muted-foreground">#{issueNumber}</span>
      <span
        className={`inline-flex items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${stageColor}`}
        data-testid="pulse-compact-stage"
      >
        {stage ?? 'No stage'}
      </span>
      {needsOwnerAction && (
        <span
          className={`inline-flex items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${OWNER_ACTION_CLASS}`}
          data-testid="pulse-compact-owner-action-cue"
          title="This issue needs your attention"
        >
          Owner action
        </span>
      )}
    </div>
  )
}

export interface IssueRowProps {
  issueNumber: number
  issueTitle: string
  workflowStage: string | null | undefined
  needsOwnerAction: boolean
}

export function IssueRow({ issueNumber, issueTitle, workflowStage, needsOwnerAction }: IssueRowProps) {
  const toProjectPath = useProjectPath()
  const stageColor = stageColorFor(workflowStage)

  return (
    <Link
      to={toProjectPath(`/issues/${issueNumber}`)}
      data-testid="pulse-compact-card"
      data-issue-number={String(issueNumber)}
      className="block rounded-lg border border-border bg-card shadow-sm hover:border-muted-foreground/40 hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <RunningIssueHeader
          issueNumber={issueNumber}
          stage={workflowStage}
          stageColor={stageColor}
          showLiveDot={false}
          needsOwnerAction={needsOwnerAction}
        />

        <h3
          className="text-sm font-medium text-foreground"
          style={LINE_CLAMP_STYLE}
          title={issueTitle}
          data-testid="pulse-compact-title"
        >
          {issueTitle}
        </h3>
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
