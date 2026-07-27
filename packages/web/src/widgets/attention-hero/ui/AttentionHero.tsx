import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangleIcon, CheckCircle2Icon, GaugeIcon, ShieldOffIcon } from 'lucide-react'
import {
  approveIssue,
  invalidateApprovalWait,
  issueListKeys,
  useApprovalWait,
  useIssues,
  type ApprovalWaitMetricsResponse,
  type Issue,
} from '../../../entities/issue'
import {
  deriveAttentionItems,
  isIssueAttentionItem,
  type AttentionItem,
} from '../../../entities/agent-ops'
import { formatDuration } from '@/shared/lib/format-duration'
import { useAgentStatus, type AgentStatus } from '../../../entities/agent'
import { useProject, useProjectPath } from '../../../entities/project'
import { cn } from '@/shared/lib/utils'

interface AttentionTreatment {
  family: 'danger' | 'warning'
  container: string
  border: string
  dot: string
  text: string
}

const dangerTreatment: AttentionTreatment = {
  family: 'danger',
  container: 'border-danger-border bg-danger-subtle',
  border: 'border-danger-border',
  dot: 'bg-danger',
  text: 'text-danger',
}

const warningTreatment: AttentionTreatment = {
  family: 'warning',
  container: 'border-warning-border bg-warning-subtle',
  border: 'border-warning-border',
  dot: 'bg-warning',
  text: 'text-warning',
}

function isApprovalItem(item: AttentionItem): boolean {
  return item.kind === 'approval-needed'
}

function attentionTreatment(item: AttentionItem): AttentionTreatment {
  return item.kind === 'approval-needed' || item.kind === 'runner-capacity-limited'
    ? warningTreatment
    : dangerTreatment
}

function attentionSummaryTreatment(items: AttentionItem[]): AttentionTreatment {
  return items.some((item) => attentionTreatment(item).family === 'danger')
    ? dangerTreatment
    : warningTreatment
}

export interface AttentionHeroProps {
  issues?: Issue[]
  agentStatus?: AgentStatus
  approvalWait?: ApprovalWaitMetricsResponse
  dataHook?: AttentionHeroDataHook
  approveIssueFn?: typeof approveIssue
}

export interface AttentionHeroData {
  issues: Issue[] | undefined
  agentStatus: AgentStatus | undefined
  approvalWait: ApprovalWaitMetricsResponse | null | undefined
  issuesResolved: boolean
}

export type AttentionHeroDataHook = () => AttentionHeroData

const useDefaultData: AttentionHeroDataHook = () => {
  const { projectId } = useProject()
  const issuesQuery = useIssues(projectId ? { projectId } : undefined)
  const agentStatusQuery = useAgentStatus()
  const approvalWaitQuery = useApprovalWait()
  return {
    issues: issuesQuery.data,
    agentStatus: agentStatusQuery.data,
    approvalWait: approvalWaitQuery.data,
    issuesResolved: issuesQuery.data !== undefined,
  }
}

export function AttentionHero({
  issues: issuesOverride,
  agentStatus: agentStatusOverride,
  approvalWait: approvalWaitOverride,
  dataHook = useDefaultData,
  approveIssueFn = approveIssue,
}: AttentionHeroProps = {}) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()

  const data = dataHook()
  const issues = issuesOverride ?? data.issues
  const agentStatus = agentStatusOverride ?? data.agentStatus
  const approvalWait = approvalWaitOverride ?? data.approvalWait ?? undefined
  const issuesResolved = issuesOverride !== undefined || data.issuesResolved

  const items = useMemo(
    () => deriveAttentionItems(issues ?? [], agentStatus ?? defaultAgentStatus),
    [issues, agentStatus],
  )

  const hasAttention = items.length > 0
  const approveMutation = useMutation({
    mutationFn: (issueNumber: number) => approveIssueFn(issueNumber, {}, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      invalidateApprovalWait(queryClient)
    },
  })

  if (!issuesResolved && items.length === 0) {
    return <LoadingState />
  }

  if (!hasAttention) {
    return <AllClearState approvalWait={approvalWait} />
  }

  const isPending = approveMutation.isPending
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
          'inline-flex items-center justify-center size-6 rounded-full text-warning-foreground',
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
        <span className={cn('text-xs font-medium', heroTreatment.text)}>({items.length})</span>
      </div>
      <ApprovalWaitSummary approvalWait={approvalWait} />
      <ul className="flex flex-col gap-2" data-testid="attention-items">
        {items.map((item) => (
          <AttentionItemRow
            key={attentionKey(item)}
            item={item}
            isPending={isPending}
            onApprove={(issueNumber) => approveMutation.mutate(issueNumber)}
            toProjectPath={toProjectPath}
          />
        ))}
      </ul>
    </section>
  )
}

function attentionKey(item: AttentionItem): string {
  if (isIssueAttentionItem(item)) return String(item.issueNumber)
  return item.kind
}

const defaultAgentStatus: AgentStatus = {
  running: false,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 0 },
}

interface AttentionItemRowProps {
  item: AttentionItem
  isPending: boolean
  onApprove: (issueNumber: number) => void
  toProjectPath: (path: string) => string
}

function AttentionItemRow({
  item,
  isPending,
  onApprove,
  toProjectPath,
}: AttentionItemRowProps) {
  if (!isIssueAttentionItem(item)) {
    return <RunnerAttentionRow item={item} toProjectPath={toProjectPath} />
  }

  const showApprove = isApprovalItem(item)
  const treatment = attentionTreatment(item)

  return (
    <li
      data-testid="attention-item"
      data-issue-number={item.issueNumber}
      data-label={item.label}
      data-kind={item.kind}
      data-family={treatment.family}
      className={cn('flex items-center gap-3 rounded-md bg-background px-3 py-2 border', treatment.border)}
    >
      <span className={cn('font-mono font-semibold text-sm', treatment.text)}>
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
          className={cn('text-xs hover:underline hover:opacity-80', treatment.text)}
        >
          Open
        </Link>
        {showApprove && (
          <button
            type="button"
            data-testid="attention-item-approve"
            data-action="approve"
            disabled={isPending}
            onClick={() => onApprove(item.issueNumber)}
            className={cn(
              'inline-flex items-center gap-1 rounded-md bg-warning px-2 py-1 text-xs font-medium text-warning-foreground',
              'hover:bg-warning/90 disabled:opacity-50 disabled:pointer-events-none',
            )}
          >
            <CheckCircle2Icon className="size-3" />
            Approve
          </button>
        )}
      </div>
    </li>
  )
}

function RunnerAttentionRow({
  item,
  toProjectPath,
}: {
  item: AttentionItem
  toProjectPath: (path: string) => string
}) {
  const treatment = attentionTreatment(item)

  if (item.kind === 'runner-unavailable') {
    return (
      <li
        data-testid="runner-down-entry"
        data-family={treatment.family}
        className={cn('flex items-center gap-3 rounded-md px-3 py-2 border', treatment.container)}
      >
        <span className="inline-flex items-center justify-center size-5 rounded-full text-danger-foreground shrink-0 bg-danger">
          <ShieldOffIcon className="size-3" />
        </span>
        <span className={cn('font-medium text-sm', treatment.text)}>{item.label}</span>
        <span
          data-testid="runner-down-message"
          className={cn('text-sm truncate min-w-0 flex-1', treatment.text)}
        >
          {item.detail ?? 'No runner is connected.'}
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

  return (
    <li
      data-testid="runner-capacity-entry"
      data-family={treatment.family}
      data-kind={item.kind}
      className={cn('flex items-center gap-3 rounded-md px-3 py-2 border', treatment.container)}
    >
      <span className={cn('inline-flex items-center justify-center size-5 rounded-full shrink-0 text-warning-foreground', treatment.dot)}>
        <GaugeIcon className="size-3" />
      </span>
      <span className="font-medium text-sm text-foreground">{item.label}</span>
      {item.detail && (
        <span
          data-testid="runner-capacity-detail"
          className="text-muted-foreground text-sm truncate min-w-0 flex-1"
        >
          {item.detail}
        </span>
      )}
      <Link
        to={toProjectPath('/activity')}
        data-testid="runner-capacity-link"
        className="shrink-0 text-xs hover:underline hover:opacity-80 text-muted-foreground"
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
  return (
    <section
      data-testid="dashboard-zone-attention"
      data-zone="attention"
      data-family="success"
      aria-label="Attention"
      className="rounded-lg border border-success-border bg-success-subtle p-4"
    >
      <div className="flex items-center gap-2 mb-2">
        <span className="inline-flex items-center justify-center size-6 rounded-full bg-success text-success-foreground">
          <CheckCircle2Icon className="size-3.5" />
        </span>
        <span className="text-sm font-semibold text-success uppercase tracking-wide">
          All clear
        </span>
      </div>
      <p className="text-sm text-success mb-3">
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
