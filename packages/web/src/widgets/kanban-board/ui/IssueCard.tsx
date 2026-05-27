import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { Badge } from '@/shared/ui/components/badge'
import { Button } from '@/shared/ui/components/button'
import type { AgentStatus } from '../../../entities/agent'
import { IssueStage, WorkflowStage, IssueStatus, type Issue } from '../../../entities/issue'
import { archiveIssue, rerunIssue, resumeIssue } from '../../../entities/issue'
import { getStripColor, getLabelStyle, formatPriority, sortLabels } from '../../../shared/lib/label-colors'
import { formatRelativeTime } from '../../../shared/lib/relative-time'
import { useProject } from '../../../entities/project'

export const APPROVAL_STAGES = new Set<string>([WorkflowStage.Plan, WorkflowStage.Build, WorkflowStage.Check])

interface Props {
  issue: Issue
  agentStatus: AgentStatus
  showArchiveButton?: boolean
}

type BadgeType = 'conflict' | 'attention' | 'approval' | 'running' | 'waiting' | 'drift' | null

function getBadgeType(issue: Issue, isAgentRunning: boolean): BadgeType {
  if (issue.workflowStage === WorkflowStage.Integrate) {
    if (issue.status === IssueStatus.Blocked || issue.status === IssueStatus.Interrupted) {
      return 'attention'
    }
    return 'running'
  }
  if (issue.status === IssueStatus.Blocked) {
    return 'attention'
  }
  if (issue.stage === IssueStage.Cancelled) {
    return 'attention'
  }
  if (issue.approvalState?.status === 'awaiting') {
    return 'approval'
  }
  if (issue.startEligibility?.waitingForCompletion?.length) {
    return 'waiting'
  }
  if (isAgentRunning) {
    return 'running'
  }
  if (issue.drift?.drifted && (issue.drift.decision === 'needs-attention' || issue.drift.decision === 'defer' || issue.drift.decision === 'suggest' || issue.drift.decision === 'enqueue')) {
    return 'drift'
  }
  return null
}

function StatusBadge({
  type,
  driftDecision,
}: {
  type: Exclude<BadgeType, 'attention' | null>
  driftDecision?: string | null
}) {
  if (type === 'conflict') {
    return (
      <Badge variant="destructive" className="text-xs">
        Failed
      </Badge>
    )
  }
  if (type === 'approval') {
    return (
      <Badge className="text-xs bg-amber-500 text-white hover:bg-amber-600">
        Approval
      </Badge>
    )
  }
  if (type === 'running') {
    return (
      <Badge variant="secondary" className="text-xs text-blue-600 bg-blue-50">
        <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse mr-1" />
        Running
      </Badge>
    )
  }
  if (type === 'waiting') {
    return (
      <Badge variant="secondary" className="text-xs text-amber-600 bg-amber-50">
        Waiting
      </Badge>
    )
  }
  if (type === 'drift') {
    const label = driftDecision === 'needs-attention' ? 'Needs Attention' : driftDecision === 'defer' ? 'Rebase Deferred' : driftDecision === 'suggest' ? 'Rebase Suggested' : 'Base Drift'
    return (
      <Badge variant="secondary" className="text-xs text-orange-600 bg-orange-50">
        {label}
      </Badge>
    )
  }
  return null
}

function IntegrationBadge({ blockedReason }: { blockedReason?: string | null }) {
  return (
    <Badge variant="secondary" className="text-xs text-blue-600 bg-blue-50">
      <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse mr-1" />
      {blockedReason ? 'Integration Failed' : 'Integrating'}
    </Badge>
  )
}

export function IssueCard({ issue, agentStatus, showArchiveButton }: Props) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const isAgentRunning = agentStatus.activeAgents?.some(
    (a) => a.issueNumber === issue.number,
  ) ?? false
  const badge = getBadgeType(issue, isAgentRunning)
  const isBlocked = issue.status === IssueStatus.Blocked
  const isCancelled = issue.stage === IssueStage.Cancelled
  const isInterrupted = issue.status === IssueStatus.Interrupted
  const isAwaitingApproval = issue.approvalState?.status === 'awaiting'

  const resumeMutation = useMutation({
    mutationFn: () => resumeIssue(issue.number, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const rerunMutation = useMutation({
    mutationFn: () => rerunIssue(issue.number, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const archiveMutation = useMutation({
    mutationFn: () => archiveIssue(issue.number, projectId),
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
      className="block rounded-lg border border-l-4 bg-background shadow-sm hover:border-muted hover:shadow-md transition-colors relative overflow-hidden"
      style={{ borderLeftColor: getStripColor(issue.labels) }}
    >
      {isCancelled && (
        <div className="absolute inset-0 bg-muted-foreground/50 z-10 flex items-center justify-center">
          <span className="text-sm font-semibold text-foreground/80">Cancelled</span>
        </div>
      )}

      {isBlocked && (
        <div className="absolute inset-0 bg-red-100/40 z-10 flex items-center justify-center">
          <span className="text-sm font-semibold text-red-700">Needs Action</span>
        </div>
      )}

      <div className={`p-3 ${isCancelled ? 'opacity-50' : ''}`}>
        <div className="flex items-center justify-between mb-1">
          <div className="flex items-center gap-2">
            <span className="text-xs font-mono text-muted-foreground/70">
              #{issue.number}
            </span>
            {priorityText && (
              <span className="text-xs font-semibold text-foreground/80">
                {priorityText}
              </span>
            )}
          </div>
          <div className="flex items-center gap-1">
            {issue.workflowStage === WorkflowStage.Integrate && (
              <IntegrationBadge blockedReason={issue.blockedReason} />
            )}
            {badge && badge !== 'attention' && badge !== 'running' && (
              <StatusBadge type={badge} driftDecision={issue.drift?.decision ?? undefined} />
            )}
            {showArchiveButton && issue.stage === IssueStage.Done && (
              <Button
                variant="ghost"
                size="icon"
                className="h-6 w-6 text-muted-foreground/70 hover:text-muted-foreground disabled:opacity-50 text-sm"
                onClick={(e) => {
                  e.preventDefault()
                  e.stopPropagation()
                  archiveMutation.mutate()
                }}
                disabled={archiveMutation.isPending}
                title="Archive issue"
              >
                📦
              </Button>
            )}
          </div>
        </div>

        <h3
          className="text-sm font-medium text-foreground"
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
            <span className="text-[10px] text-muted-foreground/70">{relativeTime}</span>
          )}
        </div>

        {isInterrupted && (
          <div className="mt-2 flex items-center justify-between">
            <span className="text-xs text-orange-600">
              Workflow was interrupted
            </span>
            <Button
              variant="default"
              size="sm"
              className="h-6 text-xs bg-orange-500 text-white hover:bg-orange-600 disabled:opacity-50"
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                resumeMutation.mutate()
              }}
              disabled={resumeMutation.isPending}
            >
              {resumeMutation.isPending ? 'Resuming...' : 'Resume'}
            </Button>
          </div>
        )}

        {!isCancelled && !isBlocked && !isInterrupted && !isAwaitingApproval && issue.workflowStage && issue.stage !== IssueStage.Done && !isAgentRunning && (
          <div className="mt-2 flex justify-end">
            <Button
              variant="outline"
              size="sm"
              className="h-6 text-xs text-muted-foreground hover:bg-muted/50 disabled:opacity-50"
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                rerunMutation.mutate()
              }}
              disabled={rerunMutation.isPending}
            >
              {rerunMutation.isPending ? 'Rerunning...' : 'Rerun'}
            </Button>
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

        {issue.startEligibility?.waitingForCompletion?.length && !isCancelled && (
          <div className="mt-2">
            <p
              className="text-xs text-amber-600"
              style={{
                display: '-webkit-box',
                WebkitLineClamp: 1,
                WebkitBoxOrient: 'vertical',
                overflow: 'hidden',
              }}
              title={issue.startEligibility.message ?? `Waiting for #${issue.startEligibility.waitingForCompletion[0].number}`}
            >
              {issue.startEligibility.message ?? `Waiting for #${issue.startEligibility.waitingForCompletion[0].number}`}
            </p>
          </div>
        )}
      </div>
    </Link>
  )
}
