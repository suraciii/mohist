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
  build: 'bg-purple-100 text-purple-700',
  plan: 'bg-blue-100 text-blue-700',
  review: 'bg-teal-100 text-teal-700',
  check: 'bg-orange-100 text-orange-700',
  integrate: 'bg-slate-100 text-slate-700',
}

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
    <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 mt-1.5 text-[10px] text-gray-400">
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
  const stageColor = STAGE_COLORS[card.issueStage.toLowerCase()] ?? 'bg-gray-100 text-gray-700'

  return (
    <Link
      to={toProjectPath(`/issues/${card.issueNumber}`)}
      className="block rounded-lg border border-gray-200 bg-white shadow-sm hover:border-gray-300 hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center justify-between mb-1.5">
          <div className="flex items-center gap-2">
            <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
            <span className="text-xs font-mono text-gray-400">#{card.issueNumber}</span>
            <span className={`inline-flex items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${stageColor}`}>
              {card.issueStage}
            </span>
            {card.model && (
              <span className="text-[10px] text-gray-400">{card.model}</span>
            )}
          </div>
          <span className="text-xs font-mono text-gray-500 tabular-nums">
            {formatDuration(elapsed)}
          </span>
        </div>

        <h3
          className="text-sm font-medium text-gray-900 mb-1"
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
            className="text-xs text-gray-500 mb-2"
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
                className="flex items-center gap-1.5 text-[11px] text-gray-400"
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
              <span className="text-[10px] text-gray-400">
                {card.taskProgress.completed}/{card.taskProgress.total} tasks
              </span>
              <span className="text-[10px] text-gray-400">
                {Math.round((card.taskProgress.completed / card.taskProgress.total) * 100)}%
              </span>
            </div>
            <div className="h-1.5 rounded-full bg-gray-100 overflow-hidden">
              <div
                className="h-full rounded-full bg-blue-500 transition-all duration-300"
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
      className="block rounded-lg border border-gray-200 bg-white shadow-sm hover:border-gray-300 hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center gap-2 mb-1.5">
          <span
            className={`inline-flex items-center gap-1 rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${
              isApproval
                ? 'bg-amber-100 text-amber-700'
                : 'bg-purple-100 text-purple-700'
            }`}
          >
            {isApproval ? '\u23F8' : '\u2753'}
            {card.label}
          </span>
          <span className="text-xs font-mono text-gray-400">#{card.issueNumber}</span>
          {card.issueStage && (
            <span className="text-[10px] text-gray-400">{card.issueStage}</span>
          )}
        </div>

        <h3
          className="text-sm font-medium text-gray-900 mb-1"
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
            className="text-xs text-gray-500"
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
  const stageColor = STAGE_COLORS[card.issueStage.toLowerCase()] ?? 'bg-gray-100 text-gray-700'
  const workTitle = card.title ?? card.taskDescription ?? card.currentWorkTitle

  return (
    <Link
      to={toProjectPath(`/issues/${card.issueNumber}`)}
      className="block rounded-lg border border-gray-200 bg-white shadow-sm hover:border-gray-300 hover:shadow-md transition-colors"
    >
      <div className="p-3">
        <div className="flex items-center justify-between mb-1">
          <div className="flex items-center gap-2">
            <span className={`text-xs ${isFailed ? 'text-red-500' : isInactive ? 'text-gray-400' : 'text-green-500'}`}>
              {isFailed ? '\u2717' : isInactive ? '\u25cf' : '\u2713'}
            </span>
            <span className="text-xs font-mono text-gray-400">#{card.issueNumber}</span>
            <span className={`inline-flex items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${stageColor}`}>
              {card.issueStage}
            </span>
          </div>
          {card.completedAt && (
            <span className="text-[10px] text-gray-400">
              {formatTimeAgo(card.completedAt)}
            </span>
          )}
        </div>

        <h3
          className="text-sm font-medium text-gray-900"
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
            className="mt-1 text-xs text-gray-500"
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
          <span className="inline-flex items-center mt-1 rounded-full px-1.5 py-0.5 text-[10px] font-semibold bg-red-100 text-red-700">
            Failed
          </span>
        )}

        <ObservabilityBar card={card} />
      </div>
    </Link>
  )
}
