import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ArchiveIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import type { AgentStatus } from '../../../entities/agent'
import { IssueStatus, WorkflowStage, IssueHealth, type Issue, type WorkflowStageProgress } from '../../../entities/issue'
import { archiveIssue, rerunIssue, resumeIssue } from '../../../entities/issue'
import { getStripColor, getLabelStyle, formatPriority, getPriorityStyle, sortLabels } from '../../../shared/lib/label-colors'
import { formatRelativeTime } from '../../../shared/lib/relative-time'
import { useProject, useProjectPath } from '../../../entities/project'
import { getStageColors } from '../model/stage-colors'

export const APPROVAL_STAGES = new Set<string>([WorkflowStage.Plan, WorkflowStage.Build, WorkflowStage.Check])

interface Props {
  issue: Issue
  agentStatus: AgentStatus
  showArchiveButton?: boolean
}

type StatusIndicator = 'blocked' | 'cancelled' | 'approval' | 'running' | 'waiting' | 'drift' | null

const WORKFLOW_STAGE_LABELS: Record<WorkflowStage, string> = {
  [WorkflowStage.Plan]: 'Plan',
  [WorkflowStage.Build]: 'Build',
  [WorkflowStage.Check]: 'Check',
  [WorkflowStage.Integrate]: 'Integrate',
  [WorkflowStage.Done]: 'Done',
}

function getStatusIndicator(issue: Issue, isAgentRunning: boolean): StatusIndicator {
  if (issue.health === IssueHealth.Blocked) return 'blocked'
  if (issue.status === IssueStatus.Cancelled) return 'cancelled'
  if (issue.approvalState?.status === 'awaiting') return 'approval'
  if (issue.blocker?.kind === 'waiting-for') return 'waiting'
  if (isAgentRunning) return 'running'
  if (
    issue.drift?.drifted &&
    (issue.drift.decision === 'needs-attention' ||
      issue.drift.decision === 'defer' ||
      issue.drift.decision === 'suggest' ||
      issue.drift.decision === 'enqueue')
  ) {
    return 'drift'
  }
  return null
}

function DraftPill() {
  return (
    <span
      data-testid="draft-pill"
      className="inline-flex items-center gap-1 rounded-full bg-muted text-muted-foreground px-2 py-0.5 text-[10px] font-semibold"
      title="This issue is still a draft and cannot be started yet"
    >
      <span className="inline-block h-1.5 w-1.5 rounded-full bg-muted-foreground/60" />
      Draft
    </span>
  )
}

function StatusPill({ indicator }: { indicator: StatusIndicator }) {
  if (!indicator) return null
  if (indicator === 'blocked') {
    return (
      <span
        data-testid="status-pill"
        className="inline-flex items-center gap-1 rounded-full bg-red-100 text-red-700 px-2 py-0.5 text-[10px] font-semibold"
      >
        <span className="inline-block h-1.5 w-1.5 rounded-full bg-red-500" />
        Blocked
      </span>
    )
  }
  if (indicator === 'cancelled') {
    return (
      <span
        data-testid="status-pill"
        className="inline-flex items-center gap-1 rounded-full bg-gray-200 text-gray-600 px-2 py-0.5 text-[10px] font-semibold"
      >
        Cancelled
      </span>
    )
  }
  if (indicator === 'approval') {
    return (
      <span
        data-testid="status-pill"
        className="inline-flex items-center gap-1 rounded-full bg-amber-100 text-amber-700 px-2 py-0.5 text-[10px] font-semibold"
      >
        <span className="inline-block h-1.5 w-1.5 rounded-full bg-amber-500" />
        Approval
      </span>
    )
  }
  if (indicator === 'running') {
    return (
      <span
        data-testid="status-pill"
        className="inline-flex items-center gap-1 rounded-full bg-blue-100 text-blue-700 px-2 py-0.5 text-[10px] font-semibold"
      >
        <span className="inline-block h-1.5 w-1.5 rounded-full bg-blue-500 animate-pulse" />
        Running
      </span>
    )
  }
  if (indicator === 'waiting') {
    return (
      <span
        data-testid="status-pill"
        className="inline-flex items-center gap-1 rounded-full bg-amber-50 text-amber-700 px-2 py-0.5 text-[10px] font-semibold"
      >
        Waiting
      </span>
    )
  }
  if (indicator === 'drift') {
    return (
      <span
        data-testid="status-pill"
        className="inline-flex items-center gap-1 rounded-full bg-orange-100 text-orange-700 px-2 py-0.5 text-[10px] font-semibold"
      >
        <span className="inline-block h-1.5 w-1.5 rounded-full bg-orange-500" />
        Drift
      </span>
    )
  }
  return null
}

function WorkflowStagePill({ issue }: { issue: Issue }) {
  const stage = issue.status === IssueStatus.Done ? WorkflowStage.Done : issue.workflowStage
  if (!stage) return null

  const colors = getStageColors(
    issue.status === IssueStatus.Done
      ? IssueStatus.Done
      : IssueStatus.InProgress,
  )

  return (
    <span
      data-testid="workflow-stage-badge"
      className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold"
      style={{ backgroundColor: `${colors.accent}1a`, color: colors.accent }}
    >
      <span
        className="inline-block h-1.5 w-1.5 rounded-full"
        style={{ backgroundColor: colors.accent }}
      />
      {WORKFLOW_STAGE_LABELS[stage]}
    </span>
  )
}

function PriorityChip({ priority }: { priority: string | null | undefined }) {
  if (!priority) return null
  const style = getPriorityStyle(priority)
  return (
    <span
      data-testid="priority-chip"
      className="inline-flex items-center rounded px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-wide"
      style={{ backgroundColor: style.bg, color: style.text }}
    >
      {formatPriority(priority)}
    </span>
  )
}

function WorkflowStageProgressIndicator({
  progress,
}: {
  progress?: WorkflowStageProgress | null
}) {
  if (!progress || progress.total === 0) return null

  const label = `${progress.completed}/${progress.total}`
  return (
    <span
      data-testid="workflow-stage-progress"
      className="text-[10px] tabular-nums text-muted-foreground/80"
      title={progress.currentTaskTitle ? `${progress.currentTaskTitle} (${label})` : label}
    >
      {label}
    </span>
  )
}

export function IssueCard({ issue, agentStatus, showArchiveButton }: Props) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const isAgentRunning = agentStatus.activeAgents?.some(
    (a) => a.issueNumber === issue.number,
  ) ?? false
  const indicator = getStatusIndicator(issue, isAgentRunning)
  const isCancelled = issue.status === IssueStatus.Cancelled
  const isInterrupted = issue.health === IssueHealth.Interrupted
  const isAwaitingApproval = issue.approvalState?.status === 'awaiting'
  const isDone = issue.status === IssueStatus.Done

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

  const sortedLabels = sortLabels(issue.labels)
  const relativeTime = formatRelativeTime(issue.updatedAt || issue.createdAt)
  const showWorkflowStagePill = !!issue.workflowStage
  const isIntegrateWithFailure =
    issue.workflowStage === WorkflowStage.Integrate &&
    !isDone &&
    (issue.health === IssueHealth.Blocked || issue.health === IssueHealth.Interrupted)
  const isDraft = issue.isDraft
  const waitingFor = !isDraft && issue.blocker?.kind === 'waiting-for' ? issue.blocker.issue : null
  const cardDeEmphasis = isDone
    ? 'opacity-70'
    : isDraft
      ? 'opacity-60 border-dashed bg-muted/30'
      : ''

  return (
    <Link
      to={toProjectPath(`/issues/${issue.number}`)}
      data-testid="issue-card"
      data-draft={isDraft ? 'true' : undefined}
      className={`block rounded-lg border border-l-4 bg-background shadow-sm hover:border-muted hover:shadow-md transition-colors relative overflow-hidden ${cardDeEmphasis}`}
      style={{ borderLeftColor: getStripColor(issue.labels) }}
    >
      {isCancelled && (
        <div className="absolute inset-0 bg-muted-foreground/40 z-10 flex items-center justify-center pointer-events-none">
          <span className="text-xs font-bold text-foreground/80 tracking-wider uppercase">
            Cancelled
          </span>
        </div>
      )}

      <div className={`p-3 ${isCancelled ? 'opacity-50' : ''}`}>
        <div className="flex items-center gap-1.5 mb-1.5">
          <span className="text-[11px] font-mono text-muted-foreground/70 tabular-nums">
            #{issue.number}
          </span>
          <PriorityChip priority={issue.priority} />
          {isDraft && <DraftPill />}
          {showWorkflowStagePill && <WorkflowStagePill issue={issue} />}
          <WorkflowStageProgressIndicator progress={issue.workflowStageProgress} />
          {isIntegrateWithFailure && (
            <span
              data-testid="integration-badge"
              className="inline-flex items-center gap-1 rounded-full bg-blue-100 text-blue-700 px-2 py-0.5 text-[10px] font-semibold"
            >
              <span className="inline-block h-1.5 w-1.5 rounded-full bg-blue-500 animate-pulse" />
              {issue.blockedReason ? 'Integration Failed' : 'Integrating'}
            </span>
          )}
          {indicator && !isIntegrateWithFailure && (
            <StatusPill indicator={indicator} />
          )}
          {showArchiveButton && isDone && (
            <Button
              variant="ghost"
              size="icon"
              data-testid="archive-button"
              className="ml-auto h-6 w-6 text-muted-foreground/70 hover:text-foreground"
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                archiveMutation.mutate()
              }}
              disabled={archiveMutation.isPending}
              title="Archive issue"
            >
              <ArchiveIcon className="size-3.5" />
            </Button>
          )}
        </div>

        <h3
          className={`text-sm font-medium ${isDraft ? 'text-muted-foreground italic' : 'text-foreground'}`}
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
            {sortedLabels.slice(0, 4).map((label) => {
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
            {sortedLabels.length > 4 && (
              <span className="text-[10px] text-muted-foreground/70">
                +{sortedLabels.length - 4}
              </span>
            )}
          </div>
        )}

        <div className="mt-2 flex items-center justify-between gap-2">
          {relativeTime && (
            <span className="text-[10px] text-muted-foreground/70">
              {relativeTime}
            </span>
          )}
          {(isInterrupted ||
            (!isCancelled &&
              !isDone &&
              !isDraft &&
              !isAwaitingApproval &&
              issue.workflowStage &&
              !isAgentRunning)) && (
            <Button
              variant="ghost"
              size="sm"
              data-testid="rerun-button"
              className="h-5 text-[10px] px-1.5 text-muted-foreground hover:bg-muted/50 disabled:opacity-50"
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                if (isInterrupted) {
                  resumeMutation.mutate()
                } else {
                  rerunMutation.mutate()
                }
              }}
              disabled={resumeMutation.isPending || rerunMutation.isPending}
            >
              {resumeMutation.isPending || rerunMutation.isPending
                ? 'Working...'
                : isInterrupted
                  ? 'Resume'
                  : 'Rerun'}
            </Button>
          )}
        </div>

        {issue.blockedReason && indicator === 'blocked' && (
          <div className="mt-1.5">
            <p
              className="text-[11px] text-red-600"
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

        {waitingFor && !isCancelled && (
          <div className="mt-1.5">
            <p
              data-testid="blocker-reason"
              className="text-[11px] text-amber-600"
              style={{
                display: '-webkit-box',
                WebkitLineClamp: 1,
                WebkitBoxOrient: 'vertical',
                overflow: 'hidden',
              }}
              title={waitingFor.title ? `Waiting for #${waitingFor.number} ${waitingFor.title}` : `Waiting for #${waitingFor.number}`}
            >
              {`Waiting for #${waitingFor.number}`}
            </p>
          </div>
        )}
      </div>
    </Link>
  )
}
