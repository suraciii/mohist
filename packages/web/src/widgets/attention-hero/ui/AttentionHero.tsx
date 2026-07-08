import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangleIcon, CheckCircle2Icon, PlayIcon, ShieldOffIcon } from 'lucide-react'
import {
  approveIssue,
  attentionItemTreatment,
  attentionSummaryTreatment,
  deriveAttentionItems,
  invalidateApprovalWait,
  resumeIssue,
  useApprovalWait,
  useIssues,
  type ApprovalWaitMetricsResponse,
  type AttentionItem,
  type Issue,
} from '../../../entities/issue'
import { formatDuration } from '@/shared/lib/format-duration'
import { useAgentStatus, type AgentStatus } from '../../../entities/agent'
import { useProject, useProjectPath } from '../../../entities/project'
import { cn } from '@/shared/lib/utils'
import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'
import { Button } from '@/shared/ui/components/button'

function isApprovalItem(item: AttentionItem): boolean {
  return item.kind === 'approval-needed'
}

function isResumableItem(item: AttentionItem): boolean {
  return item.kind !== 'approval-needed'
}

export interface AttentionHeroProps {
  issues?: Issue[]
  agentStatus?: AgentStatus
  approvalWait?: ApprovalWaitMetricsResponse
}

export function AttentionHero(props: AttentionHeroProps = {}) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()

  const issuesQuery = useIssues(projectId ? { projectId } : undefined)
  const agentStatusQuery = useAgentStatus()
  const approvalWaitQuery = useApprovalWait()

  const issues = props.issues ?? issuesQuery.data
  const agentStatus = props.agentStatus ?? agentStatusQuery.data
  const approvalWait = props.approvalWait ?? approvalWaitQuery.data
  const issuesResolved = props.issues !== undefined || issuesQuery.data !== undefined

  const items = useMemo(
    () => deriveAttentionItems(issues ?? [], agentStatus ?? defaultAgentStatus),
    [issues, agentStatus],
  )

  const runnerDown = agentStatus?.runnerAvailable === false
  const hasAttention = items.length > 0 || runnerDown

  const approveMutation = useMutation({
    mutationFn: (issueNumber: number) => approveIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      invalidateApprovalWait(queryClient)
    },
  })

  const resumeMutation = useMutation({
    mutationFn: (issueNumber: number) => resumeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  if (!issuesResolved && !runnerDown) {
    return <LoadingState />
  }

  if (!hasAttention) {
    return <AllClearState approvalWait={approvalWait} />
  }

  const isPending = approveMutation.isPending || resumeMutation.isPending
  const totalCount = items.length + (runnerDown ? 1 : 0)
  const heroTreatment = attentionSummaryTreatment(items)

  return (
    <section
      data-testid="dashboard-zone-attention"
      data-zone="attention"
      data-family={heroTreatment.family}
      aria-label="Attention"
      className={cn('rounded-lg border p-4', heroTreatment.container)}
    >
      <div className="flex items-center gap-2 mb-3">
        <span className={cn(
          'inline-flex items-center justify-center size-6 rounded-full text-foreground',
          heroTreatment.dot,
        )}>
          <AlertTriangleIcon className="size-3.5" />
        </span>
        <span className={cn(
          'text-xs font-semibold uppercase tracking-wide',
          heroTreatment.text,
        )}>
          Needs attention
        </span>
        <span className={cn('text-xs font-medium', heroTreatment.text)}>({totalCount})</span>
      </div>
      <ApprovalWaitSummary approvalWait={approvalWait} />
      <ul className="flex flex-col gap-2" data-testid="attention-items">
        {items.map((item) => (
          <AttentionItemRow
            key={item.issueId}
            item={item}
            isPending={isPending}
            onApprove={(n) => approveMutation.mutate(n)}
            onResume={(n) => resumeMutation.mutate(n)}
            toProjectPath={toProjectPath}
          />
        ))}
        {runnerDown && agentStatus && (
          <RunnerDownEntry agentStatus={agentStatus} toProjectPath={toProjectPath} />
        )}
      </ul>
    </section>
  )
}

const defaultAgentStatus: AgentStatus = {
  running: false,
  issueId: null,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 0 },
}

interface AttentionItemRowProps {
  item: AttentionItem
  isPending: boolean
  onApprove: (issueNumber: number) => void
  onResume: (issueNumber: number) => void
  toProjectPath: (path: string) => string
}

function AttentionItemRow({
  item,
  isPending,
  onApprove,
  onResume,
  toProjectPath,
}: AttentionItemRowProps) {
  const showApprove = isApprovalItem(item)
  const showResume = isResumableItem(item)
  const itemTreatment: StatusTreatment = attentionItemTreatment(item)
  return (
    <li
      data-testid="attention-item"
      data-issue-number={item.issueNumber}
      data-label={item.label}
      data-kind={item.kind}
      data-family={itemTreatment.family}
      className={cn(
        'flex items-center gap-3 rounded-md px-3 py-2 border',
        'bg-background',
        itemTreatment.border,
      )}
    >
      <span className={cn('font-mono font-semibold text-sm', itemTreatment.text)}>
        #{item.issueNumber}
      </span>
      <span className="font-medium text-foreground text-sm">{item.label}</span>
      {item.detail && (
        <span
          data-testid="attention-item-detail"
          className="text-muted-foreground text-sm truncate min-w-0 flex-1"
        >
          {item.detail}
        </span>
      )}
      <div className="flex items-center gap-1.5 shrink-0">
        <Link
          to={toProjectPath(`/issues/${item.issueNumber}`)}
          data-testid="attention-item-link"
          className={cn('text-xs hover:underline hover:opacity-80', itemTreatment.text)}
        >
          Open
        </Link>
        {showApprove && (
          <Button
            type="button"
            variant="warning"
            size="xs"
            data-testid="attention-item-approve"
            data-action="approve"
            disabled={isPending}
            onClick={() => onApprove(item.issueNumber)}
          >
            <CheckCircle2Icon className="size-3" />
            Approve
          </Button>
        )}
        {showResume && (
          <Button
            type="button"
            variant="default"
            size="xs"
            data-testid="attention-item-resume"
            data-action="resume"
            disabled={isPending}
            onClick={() => onResume(item.issueNumber)}
          >
            <PlayIcon className="size-3" />
            Resume
          </Button>
        )}
      </div>
    </li>
  )
}

interface RunnerDownEntryProps {
  agentStatus: AgentStatus
  toProjectPath: (path: string) => string
}

function RunnerDownEntry({ agentStatus, toProjectPath }: RunnerDownEntryProps) {
  // Runner-down is the most-severe state on this surface; route through
  // the danger family. The glyph deliberately keeps a solid `danger`
  // fill (per design risk note: blocking signals must stay prominent
  // even in dark mode), while the row's softer treatment keeps the
  // border/text legible.
  const dangerTreatment = statusTreatment('workflow-run', 'failed')
  return (
    <li
      data-testid="runner-down-entry"
      data-family={dangerTreatment.family}
      className={cn(
        'flex items-center gap-3 rounded-md px-3 py-2 border',
        dangerTreatment.container,
        dangerTreatment.border,
      )}
    >
      <span className="inline-flex items-center justify-center size-5 rounded-full text-white shrink-0 bg-danger">
        <ShieldOffIcon className="size-3" />
      </span>
      <span className={cn('font-medium text-sm', dangerTreatment.text)}>Runner unavailable</span>
      <span
        data-testid="runner-down-message"
        className={cn('text-sm truncate min-w-0 flex-1', 'text-danger')}
      >
        {agentStatus.runnerMessage ?? 'No runner is connected.'}
      </span>
      <Link
        to={toProjectPath('/activity')}
        data-testid="runner-down-link"
        className="shrink-0 text-xs text-danger hover:underline hover:opacity-80"
      >
        View runner status
      </Link>
    </li>
  )
}

interface AllClearStateProps {
  approvalWait?: ApprovalWaitMetricsResponse
}

function ApprovalWaitSummary({ approvalWait }: { approvalWait?: ApprovalWaitMetricsResponse }) {
  if (!approvalWait) return null

  const hasData = approvalWait.sampleCount > 0 && approvalWait.averageSeconds != null
  if (!hasData) {
    return (
      <p
        data-testid="approval-wait-empty"
        data-state="empty"
        className="text-xs text-muted-foreground"
      >
        Approval wait metric appears once an approval is completed.
      </p>
    )
  }

  return (
    <p data-testid="approval-wait-value" data-state="value" className="text-sm text-foreground">
      Your approvals averaged{' '}
      <span className="font-semibold">{formatDuration(approvalWait.averageSeconds)}</span> over the last 7 days.
    </p>
  )
}

function AllClearState({ approvalWait }: AllClearStateProps) {
  // "All clear" is the healthy/available runner reservation — same
  // family as `runner.idle` / `workflow-run.completed`: `success`.
  const treatment = statusTreatment('runner', 'idle')
  return (
    <section
      data-testid="dashboard-zone-attention"
      data-zone="attention"
      data-family={treatment.family}
      aria-label="Attention"
      className={cn('rounded-lg border p-4', treatment.container, 'border-success-border')}
    >
      <div className="flex items-center gap-2 mb-2">
        <span className={cn(
          'inline-flex items-center justify-center size-6 rounded-full text-white',
          // Solid success fill keeps the all-clear glyph prominent.
          'bg-success',
        )}>
          <CheckCircle2Icon className="size-3.5" />
        </span>
        <span className={cn(
          'text-sm font-semibold uppercase tracking-wide',
          treatment.text,
        )}>
          All clear
        </span>
      </div>
      <p className={cn('text-sm mb-3', 'text-success')}>
        Nothing needs your attention right now.
      </p>
      <ApprovalWaitSummary approvalWait={approvalWait} />
    </section>
  )
}

function LoadingState() {
  return (
    <section
      data-testid="dashboard-zone-attention"
      data-zone="attention"
      aria-label="Attention"
      className="rounded-lg border border-border bg-muted/30 p-4"
    >
      <div className="flex items-center gap-2 mb-2">
        <span className="inline-flex items-center justify-center size-6 rounded-full bg-muted-foreground/30 text-muted-foreground">
          <AlertTriangleIcon className="size-3.5" />
        </span>
        <span className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">
          Checking attention
        </span>
      </div>
      <p className="text-sm text-muted-foreground">
        Loading current issue status...
      </p>
    </section>
  )
}
