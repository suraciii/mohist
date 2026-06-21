import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangleIcon, CheckCircle2Icon, PlayIcon, ShieldOffIcon } from 'lucide-react'
import {
  approveIssue,
  deriveAttentionItems,
  resumeIssue,
  useIssues,
  type AttentionItem,
  type Issue,
} from '../../../entities/issue'
import { useAgentStatus, type AgentStatus } from '../../../entities/agent'
import { useProject, useProjectPath } from '../../../entities/project'
import { cn } from '@/shared/lib/utils'

const APPROVAL_LABEL = 'Approval needed'

function isApprovalItem(item: AttentionItem): boolean {
  return item.label === APPROVAL_LABEL
}

function isResumableItem(item: AttentionItem): boolean {
  return item.label !== APPROVAL_LABEL
}

export interface AttentionHeroProps {
  issues?: Issue[]
  agentStatus?: AgentStatus
}

export function AttentionHero(props: AttentionHeroProps = {}) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()

  const issuesQuery = useIssues(projectId ? { projectId } : undefined)
  const agentStatusQuery = useAgentStatus()

  const issues = props.issues ?? issuesQuery.data
  const agentStatus = props.agentStatus ?? agentStatusQuery.data
  const issuesResolved = props.issues !== undefined || issuesQuery.data !== undefined

  const items = useMemo(
    () => deriveAttentionItems(issues ?? [], agentStatus ?? defaultAgentStatus),
    [issues, agentStatus],
  )

  const runnerDown = agentStatus?.runnerAvailable === false
  const hasAttention = items.length > 0 || runnerDown

  if (!issuesResolved && !runnerDown) {
    return <LoadingState />
  }

  const approveMutation = useMutation({
    mutationFn: (issueNumber: number) => approveIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const resumeMutation = useMutation({
    mutationFn: (issueNumber: number) => resumeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  if (!hasAttention) {
    return <AllClearState />
  }

  const isPending = approveMutation.isPending || resumeMutation.isPending
  const totalCount = items.length + (runnerDown ? 1 : 0)

  return (
    <section
      data-testid="dashboard-zone-attention"
      data-zone="attention"
      aria-label="Attention"
      className="rounded-lg border border-amber-200 bg-amber-50/60 p-4"
    >
      <div className="flex items-center gap-2 mb-3">
        <span className="inline-flex items-center justify-center size-6 rounded-full bg-amber-500 text-white">
          <AlertTriangleIcon className="size-3.5" />
        </span>
        <span className="text-xs font-semibold text-amber-800 uppercase tracking-wide">
          Needs attention
        </span>
        <span className="text-xs text-amber-700/80 font-medium">({totalCount})</span>
      </div>
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
  return (
    <li
      data-testid="attention-item"
      data-issue-number={item.issueNumber}
      data-label={item.label}
      className="flex items-center gap-3 rounded-md bg-background px-3 py-2 border border-amber-200/80"
    >
      <span className="font-mono font-semibold text-amber-700 text-sm">
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
          className="text-xs text-amber-700 hover:underline"
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
              'inline-flex items-center gap-1 rounded-md bg-amber-600 px-2 py-1 text-xs font-medium text-white',
              'hover:bg-amber-700 disabled:opacity-50 disabled:pointer-events-none',
            )}
          >
            <CheckCircle2Icon className="size-3" />
            Approve
          </button>
        )}
        {showResume && (
          <button
            type="button"
            data-testid="attention-item-resume"
            data-action="resume"
            disabled={isPending}
            onClick={() => onResume(item.issueNumber)}
            className={cn(
              'inline-flex items-center gap-1 rounded-md bg-foreground/90 px-2 py-1 text-xs font-medium text-background',
              'hover:bg-foreground disabled:opacity-50 disabled:pointer-events-none',
            )}
          >
            <PlayIcon className="size-3" />
            Resume
          </button>
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
  return (
    <li
      data-testid="runner-down-entry"
      className="flex items-center gap-3 rounded-md bg-red-50 px-3 py-2 border border-red-200"
    >
      <span className="inline-flex items-center justify-center size-5 rounded-full bg-red-500 text-white shrink-0">
        <ShieldOffIcon className="size-3" />
      </span>
      <span className="font-medium text-red-800 text-sm">Runner unavailable</span>
      <span
        data-testid="runner-down-message"
        className="text-red-700/80 text-sm truncate min-w-0 flex-1"
      >
        {agentStatus.runnerMessage ?? 'No runner is connected.'}
      </span>
      <Link
        to={toProjectPath('/activity')}
        data-testid="runner-down-link"
        className="shrink-0 text-xs text-red-700 hover:underline"
      >
        View runner status
      </Link>
    </li>
  )
}

function AllClearState() {
  return (
    <section
      data-testid="dashboard-zone-attention"
      data-zone="attention"
      aria-label="Attention"
      className="rounded-lg border border-emerald-200 bg-emerald-50/60 p-4"
    >
      <div className="flex items-center gap-2 mb-2">
        <span className="inline-flex items-center justify-center size-6 rounded-full bg-emerald-500 text-white">
          <CheckCircle2Icon className="size-3.5" />
        </span>
        <span className="text-sm font-semibold text-emerald-800 uppercase tracking-wide">
          All clear
        </span>
      </div>
      <p className="text-sm text-emerald-700/80 mb-3">
        Nothing needs your attention right now.
      </p>
      <div
        data-testid="productivity-placeholder"
        className="rounded-md border border-dashed border-emerald-200 bg-background/40 p-3"
      >
        <p className="text-xs text-muted-foreground">
          Productivity preview will appear here once it ships.
        </p>
      </div>
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
