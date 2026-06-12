import { Link } from 'react-router-dom'
import { ActivityIcon, AlertCircleIcon, CheckCircle2Icon, CircleIcon, MessageSquareIcon } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/shared/ui/components/card'
import { useWorkflowRunSessions, type WorkflowRunSession } from '../../../entities/coder-session'
import { useProjectPath } from '../../../entities/project'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'

interface WorkflowSessionsPanelProps {
  issueNumber: number
  workflowRunId: string | null | undefined
}

function statusLabel(status: string): string {
  if (status === 'active') return 'Active'
  if (status === 'inactive') return 'Inactive'
  if (status === 'running') return 'Running'
  if (status === 'probing') return 'Checking'
  if (status === 'completed') return 'Completed'
  if (status === 'failed') return 'Failed'
  if (status === 'cancelled') return 'Cancelled'
  return status
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
  if (session.totalTokens != null) return `${formatCompact(session.totalTokens)} processed`
  const parts = [
    session.inputTokens != null ? `${formatCompact(session.inputTokens)} in` : '',
    session.outputTokens != null ? `${formatCompact(session.outputTokens)} out` : '',
  ].filter(Boolean)
  return parts.length > 0 ? parts.join(' · ') : 'No usage yet'
}

function contextText(session: WorkflowRunSession): string | null {
  if (session.contextWindowUsed == null) return null
  if (session.contextWindowSize == null || session.contextWindowSize <= 0) {
    return `${formatCompact(session.contextWindowUsed)} ctx`
  }
  const pct = Math.min(100, Math.round((session.contextWindowUsed / session.contextWindowSize) * 100))
  return `${pct}% ctx`
}

function modelLabel(session: WorkflowRunSession): string | null {
  if (session.resolvedModel && session.model && session.resolvedModel !== session.model) {
    return `${session.model} -> ${session.resolvedModel}`
  }
  return session.resolvedModel ?? session.model ?? null
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
    if (session.costAmount == null || !session.costCurrency) continue
    byCurrency.set(session.costCurrency, (byCurrency.get(session.costCurrency) ?? 0) + session.costAmount)
  }
  if (byCurrency.size === 0) return null
  return Array.from(byCurrency.entries()).map(([currency, amount]) => formatCost(amount, currency)).join(' · ')
}

function summarizePeakContext(sessions: WorkflowRunSession[]): string | null {
  let peak: { pct: number; sessionName: string } | null = null
  for (const session of sessions) {
    if (session.contextWindowUsed == null || session.contextWindowSize == null || session.contextWindowSize <= 0) continue
    const pct = Math.min(100, Math.round((session.contextWindowUsed / session.contextWindowSize) * 100))
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
  const cost = formatCost(session.costAmount, session.costCurrency)
  const model = modelLabel(session)
  const lastActivity = session.lastDataAt ?? session.completedAt ?? session.startedAt ?? session.createdAt

  return (
    <Link
      to={transcriptPath}
      className="block px-3 py-2 hover:bg-muted/60 transition-colors"
      title={`Open ${session.sessionName} transcript`}
    >
      <div className="flex items-center gap-2 min-w-0">
        <StatusIcon status={session.status} />
        <span className="font-mono text-xs font-semibold text-foreground truncate">{session.sessionName}</span>
        {model && (
          <span
            className="ml-auto max-w-[180px] truncate rounded border border-border bg-muted/60 px-1.5 py-0.5 text-[11px] font-medium text-foreground/80"
            title={model}
          >
            {model}
          </span>
        )}
      </div>
      <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-muted-foreground">
        <span>{statusLabel(session.status)}</span>
        <span>{usageText(session)}</span>
        {context && <span>{context}</span>}
        {cost && <span>{cost}</span>}
        {session.toolCallCount != null && (
          <span className={session.toolErrorCount ? 'text-orange-600 font-medium' : ''}>
            {session.toolCallCount} tool{session.toolCallCount !== 1 ? 's' : ''}
            {session.toolErrorCount ? ` · ${session.toolErrorCount} error${session.toolErrorCount !== 1 ? 's' : ''}` : ''}
          </span>
        )}
        <span>{relativeTime(lastActivity)}</span>
      </div>
      {session.failureReason && (
        <div className="mt-1 truncate text-[11px] text-red-600">{session.failureReason}</div>
      )}
    </Link>
  )
}

export function WorkflowSessionsPanel({ issueNumber, workflowRunId }: WorkflowSessionsPanelProps) {
  const { sessions, isLoading } = useWorkflowRunSessions(workflowRunId)

  if (!workflowRunId) return null

  const sorted = [...sessions].sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime())
  const totalTokens = sumNullable(sorted.map((session) => session.totalTokens))
  const cost = summarizeCost(sorted)
  const peakContext = summarizePeakContext(sorted)
  const summary = [
    `${sorted.length} session${sorted.length !== 1 ? 's' : ''}`,
    totalTokens != null ? `${formatCompact(totalTokens)} processed` : '',
    peakContext ?? '',
    cost ?? '',
  ].filter(Boolean).join(' · ')

  return (
    <Card>
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between gap-3">
          <CardTitle className="text-sm">Sessions</CardTitle>
          <MessageSquareIcon className="h-4 w-4 text-muted-foreground/70" aria-hidden="true" />
        </div>
        {summary && <div className="text-xs text-muted-foreground">{summary}</div>}
      </CardHeader>
      <CardContent className="p-0">
        {isLoading ? (
          <div className="px-3 pb-3 text-sm text-muted-foreground/70">Loading sessions...</div>
        ) : sorted.length === 0 ? (
          <div className="px-3 pb-3 text-sm text-muted-foreground/70">No sessions yet</div>
        ) : (
          <div className="divide-y divide-border/60">
            {sorted.map((session) => (
              <WorkflowSessionRow key={session.id} issueNumber={issueNumber} session={session} />
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
