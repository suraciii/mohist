import { Link } from 'react-router-dom'
import { ActivityIcon, AlertCircleIcon, CheckCircle2Icon, CircleIcon, MessageSquareIcon } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/shared/ui/components/card'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'
import { useWorkflowRunSessions, type WorkflowRunSession } from '../../../entities/coder-session'
import { useProjectPath } from '../../../entities/project'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'
import {
  WORKFLOW_PIPELINE_STAGES,
  useWorkflowSessionFiltering,
  type WorkflowPipelineStage,
  type WorkflowSessionSortKey,
} from '../model/useWorkflowSessionFiltering'

const STATUS_LABELS: Record<string, string> = {
  active: 'Active',
  inactive: 'Inactive',
  running: 'Running',
  probing: 'Checking',
  completed: 'Completed',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

const STAGE_LABELS: Record<WorkflowPipelineStage, string> = {
  plan: 'Plan',
  build: 'Build',
  check: 'Check',
  integrate: 'Integrate',
}

const SORT_LABELS: Record<WorkflowSessionSortKey, string> = {
  createdAt: 'Created',
  tokens: 'Tokens',
  duration: 'Duration',
}

interface WorkflowSessionsPanelProps {
  issueNumber: number
  workflowRunId: string | null | undefined
}

function statusLabel(status: string): string {
  return STATUS_LABELS[status] ?? status
}

function StatusIcon({ status }: { status: string }) {
  if (status === 'active' || status === 'running' || status === 'probing') {
    return <ActivityIcon className="h-3.5 w-3.5 text-blue-500" aria-hidden="true" />
  }
  if (status === 'completed') {
    return <CheckCircle2Icon className="h-3.5 w-3.5 text-green-500" aria-hidden="true" />
  }
  if (status === 'failed' || status === 'cancelled') {
    return <AlertCircleIcon className="h-3.5 w-3.5 text-red-500" aria-hidden="true" />
  }
  return <CircleIcon className="h-3.5 w-3.5 text-gray-400" aria-hidden="true" />
}

function usageText(session: WorkflowRunSession): string {
  const usage = session.usage
  if (usage?.totalTokens != null) return `${formatCompact(usage.totalTokens)} processed`
  const parts = [
    usage?.inputTokens != null ? `${formatCompact(usage.inputTokens)} in` : '',
    usage?.outputTokens != null ? `${formatCompact(usage.outputTokens)} out` : '',
  ].filter(Boolean)
  return parts.length > 0 ? parts.join(' · ') : 'Usage unavailable'
}

function contextText(session: WorkflowRunSession): string | null {
  const pct = session.usage?.contextUsagePercent
  if (pct == null) return null
  const clamped = Math.max(0, Math.min(100, pct))
  return `${Math.round(clamped)}% ctx`
}

function modelLabel(session: WorkflowRunSession): string | null {
  const resolved = session.eventSummary?.resolvedModel
  if (resolved && session.model && resolved !== session.model) {
    return `${session.model} -> ${resolved}`
  }
  return resolved ?? session.model ?? null
}

function relativeTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const diff = Date.now() - new Date(iso).getTime()
  if (diff < 60_000) return 'just now'
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h ago`
  return new Date(iso).toLocaleDateString()
}

function sumNullable(values: Array<number | null | undefined>): number | null {
  let hasValue = false
  let total = 0
  for (const value of values) {
    if (value == null) continue
    hasValue = true
    total += value
  }
  return hasValue ? total : null
}

function summarizeCost(sessions: WorkflowRunSession[]): string | null {
  const byCurrency = new Map<string, number>()
  for (const session of sessions) {
    const amount = session.usage?.costAmount
    const currency = session.usage?.costCurrency
    if (amount == null || !currency) continue
    byCurrency.set(currency, (byCurrency.get(currency) ?? 0) + amount)
  }
  if (byCurrency.size === 0) return null
  return Array.from(byCurrency.entries()).map(([currency, amount]) => formatCost(amount, currency)).join(' · ')
}

function summarizePeakContext(sessions: WorkflowRunSession[]): string | null {
  let peak: { pct: number; sessionName: string } | null = null
  for (const session of sessions) {
    const rawPct = session.usage?.contextUsagePercent
    if (rawPct == null || !Number.isFinite(rawPct)) continue
    const pct = Math.max(0, Math.min(100, Math.round(rawPct)))
    if (!peak || pct > peak.pct) {
      peak = { pct, sessionName: session.sessionName }
    }
  }
  return peak ? `peak ${peak.pct}% ${peak.sessionName}` : null
}

function WorkflowSessionRow({ issueNumber, session }: { issueNumber: number; session: WorkflowRunSession }) {
  const toProjectPath = useProjectPath()
  const transcriptPath = toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(session.sessionName)}`)
  const context = contextText(session)
  const cost = formatCost(session.usage?.costAmount, session.usage?.costCurrency)
  const model = modelLabel(session)
  const lastActivity = session.lastDataAt ?? session.completedAt ?? session.startedAt ?? session.createdAt
  const toolCallCount = session.eventSummary?.toolCallCount
  const toolErrorCount = session.eventSummary?.toolErrorCount

  return (
    <Link
      to={transcriptPath}
      className="block min-w-0 px-3 py-2 hover:bg-muted/60 transition-colors"
      data-testid="workflow-session-row"
      title={`Open ${session.sessionName} transcript`}
    >
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0" data-testid="workflow-session-row-header">
        <StatusIcon status={session.status} />
        <span className="min-w-0 truncate font-mono text-xs font-semibold text-foreground">{session.sessionName}</span>
        {model && (
          <span
            className="ml-auto min-w-0 max-w-full truncate rounded border border-border bg-muted/60 px-1.5 py-0.5 text-[11px] font-medium text-foreground/80 sm:max-w-[180px]"
            title={model}
          >
            {model}
          </span>
        )}
      </div>
      <div
        className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-muted-foreground"
        data-testid="workflow-session-row-metrics"
      >
        <span>{statusLabel(session.status)}</span>
        <span>{usageText(session)}</span>
        {context && <span>{context}</span>}
        {cost && <span>{cost}</span>}
        {toolCallCount != null && (
          <span className={toolErrorCount ? 'text-orange-600 font-medium' : ''}>
            {toolCallCount} tool{toolCallCount !== 1 ? 's' : ''}
            {toolErrorCount ? ` · ${toolErrorCount} error${toolErrorCount !== 1 ? 's' : ''}` : ''}
          </span>
        )}
        <span>{relativeTime(lastActivity)}</span>
      </div>
      {session.failureReason && (
        <div className="mt-1 truncate min-w-0 text-[11px] text-red-600">{session.failureReason}</div>
      )}
    </Link>
  )
}

function SessionFilterControls({
  statusFilter,
  availableStatuses,
  onStatusChange,
  stageFilter,
  onStageChange,
  sortKey,
  onSortChange,
}: {
  statusFilter: string | null
  availableStatuses: string[]
  onStatusChange: (value: string | null) => void
  stageFilter: WorkflowPipelineStage | null
  onStageChange: (value: WorkflowPipelineStage | null) => void
  sortKey: WorkflowSessionSortKey
  onSortChange: (value: WorkflowSessionSortKey) => void
}) {
  return (
    <div className="flex flex-wrap items-center gap-2 px-3 pb-2 pt-1" data-testid="workflow-sessions-controls">
      <label className="flex items-center gap-1 text-[11px] text-muted-foreground">
        <span>Status</span>
        <Select
          value={statusFilter ?? ''}
          onValueChange={(value) => onStatusChange(value === '' ? null : value)}
        >
          <SelectTrigger
            aria-label="Filter sessions by status"
            data-testid="workflow-sessions-status-filter"
            size="sm"
            className="h-7 min-w-[140px] text-xs font-normal"
          >
            <SelectValue placeholder="All statuses">{statusFilter ? statusLabel(statusFilter) : 'All statuses'}</SelectValue>
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="">All statuses</SelectItem>
            {availableStatuses.map((status) => (
              <SelectItem key={status} value={status}>{statusLabel(status)}</SelectItem>
            ))}
          </SelectContent>
        </Select>
      </label>
      <label className="flex items-center gap-1 text-[11px] text-muted-foreground">
        <span>Stage</span>
        <Select
          value={stageFilter ?? ''}
          onValueChange={(value) => onStageChange(value === '' ? null : (value as WorkflowPipelineStage))}
        >
          <SelectTrigger
            aria-label="Filter sessions by stage"
            data-testid="workflow-sessions-stage-filter"
            size="sm"
            className="h-7 min-w-[120px] text-xs font-normal"
          >
            <SelectValue placeholder="All stages">{stageFilter ? STAGE_LABELS[stageFilter] : 'All stages'}</SelectValue>
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="">All stages</SelectItem>
            {WORKFLOW_PIPELINE_STAGES.map((stage) => (
              <SelectItem key={stage} value={stage}>
                {STAGE_LABELS[stage]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </label>
      <label className="ml-auto flex items-center gap-1 text-[11px] text-muted-foreground">
        <span>Sort</span>
        <Select
          value={sortKey}
          onValueChange={(value) => onSortChange(value as WorkflowSessionSortKey)}
        >
          <SelectTrigger
            aria-label="Sort sessions"
            data-testid="workflow-sessions-sort"
            size="sm"
            className="h-7 min-w-[100px] text-xs font-normal"
          >
            <SelectValue>{SORT_LABELS[sortKey]}</SelectValue>
          </SelectTrigger>
          <SelectContent>
            {(['createdAt', 'tokens', 'duration'] as const).map((key) => (
              <SelectItem key={key} value={key}>{SORT_LABELS[key]}</SelectItem>
            ))}
          </SelectContent>
        </Select>
      </label>
    </div>
  )
}

export function WorkflowSessionsPanel({ issueNumber, workflowRunId }: WorkflowSessionsPanelProps) {
  const { sessions, isLoading } = useWorkflowRunSessions(workflowRunId)
  const filtering = useWorkflowSessionFiltering(sessions)

  if (!workflowRunId) return null

  const totalTokens = sumNullable(sessions.map((session) => session.usage?.totalTokens))
  const cost = summarizeCost(sessions)
  const peakContext = summarizePeakContext(sessions)
  const summary = [
    `${sessions.length} session${sessions.length !== 1 ? 's' : ''}`,
    totalTokens != null ? `${formatCompact(totalTokens)} processed` : '',
    peakContext ?? '',
    cost ?? '',
  ].filter(Boolean).join(' · ')

  const filteredCount = filtering.sessions.length
  const totalCount = filtering.totalCount
  const filteredNotice =
    filteredCount !== totalCount
      ? `Showing ${filteredCount} of ${totalCount} sessions`
      : null

  return (
    <Card data-testid="workflow-sessions-panel">
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between gap-3">
          <CardTitle className="text-sm">Sessions</CardTitle>
          <MessageSquareIcon className="h-4 w-4 text-muted-foreground/70" aria-hidden="true" />
        </div>
        {summary && <div className="text-xs text-muted-foreground">{summary}</div>}
        {filteredNotice && (
          <div className="text-[11px] text-muted-foreground" data-testid="workflow-sessions-filter-notice">
            {filteredNotice}
          </div>
        )}
      </CardHeader>
      <CardContent className="p-0">
        {isLoading ? (
          <div className="px-3 pb-3 text-sm text-muted-foreground/70">Loading sessions...</div>
        ) : sessions.length === 0 ? (
          <div className="px-3 pb-3 text-sm text-muted-foreground/70">No sessions yet</div>
        ) : (
          <>
            <SessionFilterControls
              statusFilter={filtering.statusFilter}
              availableStatuses={filtering.availableStatuses}
              onStatusChange={filtering.setStatusFilter}
              stageFilter={filtering.stageFilter}
              onStageChange={filtering.setStageFilter}
              sortKey={filtering.sortKey}
              onSortChange={filtering.setSortKey}
            />
            {filteredCount === 0 ? (
              <div className="px-3 pb-3 text-sm text-muted-foreground/70">No sessions match the current filters.</div>
            ) : (
              <div className="divide-y divide-border/60">
                {filtering.sessions.map((session) => (
                  <WorkflowSessionRow key={session.id} issueNumber={issueNumber} session={session} />
                ))}
              </div>
            )}
          </>
        )}
      </CardContent>
    </Card>
  )
}
