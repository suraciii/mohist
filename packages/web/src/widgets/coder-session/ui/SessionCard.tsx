import { Link } from 'react-router-dom'
import { useProjectPath } from '../../../entities/project'
import type { SessionCard as SessionCardType, WaitingCard as WaitingCardType } from '@/entities/agent-ops'
import { ActiveSessionAnomalies, WaitingSessionAnomalies } from '../model/anomaly'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'
import { ContextHealthIndicator } from '@/entities/coder-session'

function formatDuration(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000)
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60

  if (hours > 0) {
    return `${hours}h ${minutes}m`
  }
  return `${minutes}m ${seconds}s`
}

function formatTimeAgo(isoString: string): string {
  const diff = Date.now() - new Date(isoString).getTime()
  const minutes = Math.floor(diff / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  return `${days}d ago`
}

const STAGE_COLORS: Record<string, string> = {
  build: 'bg-info-subtle text-info border-info-border',
  plan: 'bg-info-subtle text-info border-info-border',
  review: 'bg-success-subtle text-success border-success-border',
  check: 'bg-warning-subtle text-warning border-warning-border',
  integrate: 'bg-muted text-muted-foreground border-border',
}

const APPROVAL_CHIP_COLORS = 'bg-warning-subtle text-warning border-warning-border'
const BLOCKED_CHIP_COLORS = 'bg-muted text-muted-foreground border-border'
const RECENT_FAILURE_CHIP_COLORS = 'bg-danger-subtle text-danger border-danger-border'

function ObservabilityBar({ card }: { card: SessionCardType }) {
  const parts: string[] = []

  if (card.resolvedModel && card.resolvedModel !== card.model) {
    parts.push(`using ${card.resolvedModel}`)
  }

  if (card.inputTokens != null || card.outputTokens != null) {
    const usageParts: string[] = []
    if (card.inputTokens != null) usageParts.push(`${formatCompact(card.inputTokens)} in`)
    if (card.outputTokens != null) usageParts.push(`${formatCompact(card.outputTokens)} out`)
    if (usageParts.length > 0) parts.push(usageParts.join(' · '))
  }

  if (card.costAmount != null && card.costCurrency) {
    parts.push(formatCost(card.costAmount, card.costCurrency))
  }

  if (card.failureCategory) {
    parts.push(card.failureCategory)
  }

  if (card.toolCallCount != null) {
    const toolText = `${card.toolCallCount} tool${card.toolCallCount !== 1 ? 's' : ''}`
    if (card.toolErrorCount && card.toolErrorCount > 0) {
      parts.push(`${toolText} · ${card.toolErrorCount} error${card.toolErrorCount !== 1 ? 's' : ''}`)
    } else {
      parts.push(toolText)
    }
  }

  if (parts.length === 0) return null

  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 mt-1.5 text-[10px] text-muted-foreground/70">
      {parts.map((part, i) => (
        <span key={i}>{part}</span>
      ))}
    </div>
  )
}

interface ActiveSessionCardProps {
  card: SessionCardType
  now: number
}

export function ActiveSessionCard({ card, now }: ActiveSessionCardProps) {
  const toProjectPath = useProjectPath()
  const elapsed = now - new Date(card.createdAt).getTime()
  const stageColor = STAGE_COLORS[card.issueStage.toLowerCase()] ?? 'bg-muted text-muted-foreground border-border'

  return (
    <Link
      to={toProjectPath(`/issues/${card.issueNumber}`)}
      className="block rounded-lg border border-border bg-background shadow-sm hover:border-border hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center justify-between mb-1.5">
          <div className="flex items-center gap-2">
            <span className="inline-block h-2 w-2 rounded-full bg-info animate-pulse" />
            <span className="text-xs font-mono text-muted-foreground/70">#{card.issueNumber}</span>
            <span
              data-testid="active-card-stage-chip"
              data-stage={card.issueStage}
              className={`inline-flex items-center rounded-full border px-1.5 py-0.5 text-[10px] font-semibold ${stageColor}`}
            >
              {card.issueStage}
            </span>
            {card.model && (
              <span className="text-[10px] text-muted-foreground/70">{card.model}</span>
            )}
          </div>
          <span className="text-xs font-mono text-muted-foreground tabular-nums">
            {formatDuration(elapsed)}
          </span>
        </div>

        <h3
          className="text-sm font-medium text-foreground mb-1"
          style={{
            display: '-webkit-box',
            WebkitLineClamp: 1,
            WebkitBoxOrient: 'vertical',
            overflow: 'hidden',
          }}
          title={card.title ?? card.issueTitle}
        >
          {card.title ?? card.issueTitle}
        </h3>

        {!card.title && card.taskDescription && (
          <p
            className="text-xs text-muted-foreground mb-2"
            style={{
              display: '-webkit-box',
              WebkitLineClamp: 1,
              WebkitBoxOrient: 'vertical',
              overflow: 'hidden',
            }}
          >
            {card.taskDescription.length > 80
              ? card.taskDescription.slice(0, 79) + '\u2026'
              : card.taskDescription}
          </p>
        )}

        {card.activityPreviews.length > 0 && (
          <div className="space-y-0.5 mb-2">
            {card.activityPreviews.map((preview, i) => (
              <div
                key={i}
                className="flex items-center gap-1.5 text-[11px] text-muted-foreground/70"
              >
                <span className="shrink-0">{preview.kind === 'tool' ? '\u2699' : '\u2022'}</span>
                <span
                  className="truncate"
                  title={preview.text}
                >
                  {preview.text}
                </span>
              </div>
            ))}
          </div>
        )}

        <ObservabilityBar card={card} />

        {card.contextWindowSize != null && card.contextWindowSize > 0 && (
          <div className="mt-1">
            <ContextHealthIndicator
              contextWindowUsed={card.contextWindowUsed ?? null}
              contextWindowSize={card.contextWindowSize ?? null}
              contextUsagePercent={card.contextUsagePercent ?? null}
              healthStatus={card.healthStatus ?? null}
            />
          </div>
        )}

        {card.taskProgress && (
          <div className="mt-1">
            <div className="flex items-center justify-between mb-0.5">
              <span className="text-[10px] text-muted-foreground/70">
                {card.taskProgress.completed}/{card.taskProgress.total} tasks
              </span>
              <span className="text-[10px] text-muted-foreground/70">
                {Math.round((card.taskProgress.completed / card.taskProgress.total) * 100)}%
              </span>
            </div>
            <div className="h-1.5 rounded-full bg-muted overflow-hidden">
              <div
                className="h-full rounded-full bg-info transition-all duration-300"
                style={{ width: `${(card.taskProgress.completed / card.taskProgress.total) * 100}%` }}
              />
            </div>
          </div>
        )}

        <ActiveSessionAnomalies card={card} now={now} />
      </div>
    </Link>
  )
}

interface WaitingCardProps {
  card: WaitingCardType
}

export function WaitingCard({ card }: WaitingCardProps) {
  const toProjectPath = useProjectPath()
  const isApproval = card.label === 'Needs Approval'

  return (
    <Link
      to={toProjectPath(`/issues/${card.issueNumber}`)}
      className="block rounded-lg border border-border bg-background shadow-sm hover:border-border hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center gap-2 mb-1.5">
          <span
            className={`inline-flex items-center gap-1 rounded-full border px-1.5 py-0.5 text-[10px] font-semibold ${
              isApproval
                ? APPROVAL_CHIP_COLORS
                : BLOCKED_CHIP_COLORS
            }`}
            data-testid="waiting-card-chip"
            data-tone={isApproval ? 'warning' : 'neutral'}
          >
            {isApproval ? '\u23F8' : '\u2753'}
            {card.label}
          </span>
          <span className="text-xs font-mono text-muted-foreground/70">#{card.issueNumber}</span>
          {card.issueStage && (
            <span className="text-[10px] text-muted-foreground/70">{card.issueStage}</span>
          )}
        </div>

        <h3
          className="text-sm font-medium text-foreground mb-1"
          style={{
            display: '-webkit-box',
            WebkitLineClamp: 1,
            WebkitBoxOrient: 'vertical',
            overflow: 'hidden',
          }}
          title={card.issueTitle}
        >
          {card.issueTitle}
        </h3>

        {card.questionPreview && (
          <p
            className="text-xs text-muted-foreground"
            style={{
              display: '-webkit-box',
              WebkitLineClamp: 2,
              WebkitBoxOrient: 'vertical',
              overflow: 'hidden',
            }}
            title={card.questionPreview}
          >
            {card.questionPreview}
          </p>
        )}

        <WaitingSessionAnomalies card={card} />
      </div>
    </Link>
  )
}

interface RecentCardProps {
  card: SessionCardType
}

export function RecentCard({ card }: RecentCardProps) {
  const toProjectPath = useProjectPath()
  const isFailed = card.status === 'failed'
  const isInactive = card.status === 'inactive'
  const stageColor = STAGE_COLORS[card.issueStage.toLowerCase()] ?? 'bg-muted text-muted-foreground border-border'
  const workTitle = card.title ?? card.taskDescription ?? card.currentWorkTitle

  return (
    <Link
      to={toProjectPath(`/issues/${card.issueNumber}`)}
      className="block rounded-lg border border-border bg-background shadow-sm hover:border-border hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center justify-between mb-1">
          <div className="flex items-center gap-2">
            <span
              data-testid="recent-card-status-glyph"
              data-tone={isFailed ? 'danger' : isInactive ? 'neutral' : 'success'}
              className={`text-xs ${isFailed ? 'text-danger' : isInactive ? 'text-muted-foreground/70' : 'text-success'}`}
            >
              {isFailed ? '\u2717' : isInactive ? '\u25cf' : '\u2713'}
            </span>
            <span className="text-xs font-mono text-muted-foreground/70">#{card.issueNumber}</span>
            <span
              data-testid="recent-card-stage-chip"
              data-stage={card.issueStage}
              className={`inline-flex items-center rounded-full border px-1.5 py-0.5 text-[10px] font-semibold ${stageColor}`}
            >
              {card.issueStage}
            </span>
          </div>
          {card.completedAt && (
            <span className="text-[10px] text-muted-foreground/70">
              {formatTimeAgo(card.completedAt)}
            </span>
          )}
        </div>

        <h3
          className="text-sm font-medium text-foreground"
          style={{
            display: '-webkit-box',
            WebkitLineClamp: 1,
            WebkitBoxOrient: 'vertical',
            overflow: 'hidden',
          }}
          title={card.issueTitle}
        >
          {card.issueTitle}
        </h3>

        {workTitle && (
          <p
            className="mt-1 text-xs text-muted-foreground"
            style={{
              display: '-webkit-box',
              WebkitLineClamp: 1,
              WebkitBoxOrient: 'vertical',
              overflow: 'hidden',
            }}
            title={workTitle}
          >
            {workTitle}
          </p>
        )}

        {isFailed && (
          <span
            data-testid="recent-card-failure-chip"
            className={`inline-flex items-center mt-1 rounded-full border px-1.5 py-0.5 text-[10px] font-semibold ${RECENT_FAILURE_CHIP_COLORS}`}
          >
            Failed
          </span>
        )}

        <ObservabilityBar card={card} />
      </div>
    </Link>
  )
}
