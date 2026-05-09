import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import type { Issue, AgentStatus } from '../lib/types'
import { Stage, IssueStatus } from '../lib/types'
import { api } from '../lib/api'
import { getStripColor, getLabelStyle, formatPriority, sortLabels } from '../lib/label-colors'
import { formatRelativeTime } from '../lib/relative-time'

export const APPROVAL_STAGES = new Set<string>([Stage.Plan, Stage.Build, Stage.Check])

const MERGE_STATE_LABELS: Record<string, string> = {
  'build-failed': 'Failed',
  conflict: 'Conflict',
  pending: 'Pending',
  merging: 'Merging',
  'done-not-merged': 'Not Merged',
  '': 'Not Merged',
}

interface Props {
  issue: Issue
  agentStatus: AgentStatus
  showArchiveButton?: boolean
}

type BadgeType = 'conflict' | 'closed' | 'approval' | 'running' | 'false-done' | null

function getBadgeType(issue: Issue, isAgentRunning: boolean): BadgeType {
  if (
    issue.stage === Stage.Done &&
    (issue.mergeState === null || issue.mergeState === undefined || issue.mergeState !== 'merged')
  ) {
    return 'conflict'
  }
  if (issue.stage === Stage.Integrate) {
    if (issue.status === IssueStatus.Blocked || issue.status === IssueStatus.Interrupted) {
      return 'closed'
    }
    return 'running'
  }
  if (issue.status === IssueStatus.Blocked) {
    return 'closed'
  }
  if (issue.status === IssueStatus.Closed) {
    return 'closed'
  }
  if (issue.approvalState?.status === 'awaiting') {
    return 'approval'
  }
  if (isAgentRunning) {
    return 'running'
  }
  return null
}

function Badge({
  type,
  mergeState,
}: {
  type: Exclude<BadgeType, 'closed' | null>
  mergeState?: string | null
}) {
  if (type === 'conflict') {
    const label = MERGE_STATE_LABELS[mergeState ?? ''] ?? mergeState ?? 'Failed'
    return (
      <span className="inline-flex items-center gap-1 text-xs font-medium text-white bg-red-500 px-1.5 py-0.5 rounded">
        {label}
      </span>
    )
  }
  if (type === 'approval') {
    return (
      <span className="inline-flex items-center gap-1 text-xs font-medium text-white px-1.5 py-0.5 rounded" style={{ backgroundColor: '#f59e0b' }}>
        Approval
      </span>
    )
  }
  if (type === 'running') {
    return (
      <span className="inline-flex items-center gap-1 text-xs font-medium text-blue-600 bg-blue-50 px-1.5 py-0.5 rounded">
        <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
        Running
      </span>
    )
  }
  return null
}

function IntegrationBadge({ blockedReason }: { blockedReason?: string | null }) {
  return (
    <span className="inline-flex items-center gap-1 text-xs font-medium text-blue-600 bg-blue-50 px-1.5 py-0.5 rounded">
      <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
      {blockedReason ? 'Integration Failed' : 'Integrating'}
    </span>
  )
}

export function IssueCard({ issue, agentStatus, showArchiveButton }: Props) {
  const queryClient = useQueryClient()
  const isAgentRunning = agentStatus.activeAgents?.some(
    (a) => a.issueNumber === issue.number,
  ) ?? false
  const badge = getBadgeType(issue, isAgentRunning)
  const isBlocked = issue.status === IssueStatus.Blocked
  const isClosed = issue.status === IssueStatus.Closed
  const isInterrupted = issue.status === IssueStatus.Interrupted

  const reopenMutation = useMutation({
    mutationFn: () => api.reopenIssue(issue.number),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const rerunMutation = useMutation({
    mutationFn: () => api.rerunIssue(issue.number),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const archiveMutation = useMutation({
    mutationFn: () => api.archiveIssue(issue.number),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['archived-issues'] })
      if (data.warning) {
        toast.warning(data.warning)
      } else {
        toast.success('Issue archived')
      }
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Archive failed')
    },
  })

  const priorityText = issue.priority ? formatPriority(issue.priority) : ''
  const sortedLabels = sortLabels(issue.labels)
  const relativeTime = formatRelativeTime(issue.updatedAt || issue.createdAt)

  return (
    <Link
      to={`/issue/${issue.number}`}
      className="block rounded-lg border border-gray-200 border-l-4 bg-white shadow-sm hover:border-gray-300 hover:shadow-md transition-colors relative overflow-hidden"
      style={{ borderLeftColor: getStripColor(issue.labels) }}
    >
      {isClosed && (
        <div className="absolute inset-0 bg-gray-400/50 z-10 flex items-center justify-center">
          <span className="text-sm font-semibold text-gray-700">Closed</span>
        </div>
      )}

      {isBlocked && (
        <div className="absolute inset-0 bg-red-100/40 z-10 flex items-center justify-center">
          <span className="text-sm font-semibold text-red-700">Blocked</span>
        </div>
      )}

      <div className={`p-3 ${isClosed ? 'opacity-50' : ''}`}>
        <div className="flex items-center justify-between mb-1">
          <div className="flex items-center gap-2">
            <span className="text-xs font-mono text-gray-400">
              #{issue.number}
            </span>
            {priorityText && (
              <span className="text-xs font-semibold text-gray-700">
                {priorityText}
              </span>
            )}
          </div>
          <div className="flex items-center gap-1">
            {issue.stage === Stage.Integrate && (
              <IntegrationBadge blockedReason={issue.blockedReason} />
            )}
            {badge && badge !== 'closed' && badge !== 'running' && (
              <Badge type={badge} mergeState={issue.mergeState} />
            )}
            {showArchiveButton && issue.status === IssueStatus.Completed && (
              <button
                onClick={(e) => {
                  e.preventDefault()
                  e.stopPropagation()
                  archiveMutation.mutate()
                }}
                disabled={archiveMutation.isPending}
                className="text-gray-400 hover:text-gray-600 disabled:opacity-50 transition-colors text-sm"
                title="Archive issue"
              >
                📦
              </button>
            )}
          </div>
        </div>

        <h3
          className="text-sm font-medium text-gray-900"
          style={{
            display: '-webkit-box',
            WebkitLineClamp: 2,
            WebkitBoxOrient: 'vertical',
            overflow: 'hidden',
          }}
          title={issue.title}
        >
          {issue.title}
        </h3>

        {sortedLabels.length > 0 && (
          <div className="mt-2 flex items-center gap-1 flex-nowrap overflow-hidden">
            {sortedLabels.map((label) => {
              const s = getLabelStyle(label)
              return (
                <span
                  key={label}
                  className={`inline-block rounded-full px-1.5 font-medium whitespace-nowrap ${
                    s.size === 'sm' ? 'text-[10px] py-px' : 'text-xs py-0.5'
                  }`}
                  style={{ backgroundColor: s.bg, color: s.text }}
                >
                  {label}
                </span>
              )
            })}
          </div>
        )}

        <div className="mt-1.5 flex justify-end">
          {relativeTime && (
            <span className="text-[10px] text-gray-400">{relativeTime}</span>
          )}
        </div>

        {isInterrupted && (
          <div className="mt-2 flex items-center justify-between">
            <span className="text-xs text-orange-600">
              Pipeline was interrupted
            </span>
            <button
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                reopenMutation.mutate()
              }}
              disabled={reopenMutation.isPending}
              className="rounded bg-orange-500 px-2 py-0.5 text-xs font-medium text-white hover:bg-orange-600 disabled:opacity-50 transition-colors"
            >
              {reopenMutation.isPending ? 'Resuming...' : 'Resume'}
            </button>
          </div>
        )}

        {!isClosed && !isBlocked && !isInterrupted && issue.stage !== Stage.Backlog && issue.stage !== Stage.Done && !isAgentRunning && (
          <div className="mt-2 flex justify-end">
            <button
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                rerunMutation.mutate()
              }}
              disabled={rerunMutation.isPending}
              className="rounded border border-gray-300 bg-white px-2 py-0.5 text-xs font-medium text-gray-600 hover:bg-gray-50 disabled:opacity-50 transition-colors"
            >
              {rerunMutation.isPending ? 'Rerunning...' : 'Rerun'}
            </button>
          </div>
        )}

        {isBlocked && issue.blockedReason && (
          <div className="mt-2">
            <p
              className="text-xs text-red-600"
              style={{
                display: '-webkit-box',
                WebkitLineClamp: 1,
                WebkitBoxOrient: 'vertical',
                overflow: 'hidden',
              }}
              title={issue.blockedReason}
            >
              {issue.blockedReason.length > 60
                ? issue.blockedReason.slice(0, 60) + '...'
                : issue.blockedReason}
            </p>
          </div>
        )}
      </div>
    </Link>
  )
}
