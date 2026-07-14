import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { RunnerActiveWork, RunnerStatusRow } from '../../../entities/runner'
import { useRunners } from '../../../entities/runner'
import { useProjectPath } from '../../../entities/project'
import { Card, CardContent, CardHeader, CardTitle } from '@/shared/ui/components/card'
import { Badge } from '@/shared/ui/components/badge'

const RUNNER_START_HINT = 'npx mohist runner'

const STATUS_CONFIG: Record<RunnerStatusRow['status'], { dot: string; badge: string; label: string }> = {
  idle: { dot: 'bg-success', badge: 'bg-success-subtle text-success ring-success-border', label: 'idle' },
  busy: { dot: 'bg-info', badge: 'bg-info-subtle text-info ring-info-border', label: 'busy' },
  stale: { dot: 'bg-warning', badge: 'bg-warning-subtle text-warning ring-warning-border', label: 'stale' },
  offline: { dot: 'bg-muted-foreground', badge: 'bg-muted text-muted-foreground ring-border', label: 'offline' },
}

function RunnerScopeLabel({ scope }: { scope: RunnerStatusRow['scope'] }) {
  if (scope.type === 'global') {
    return <Badge variant="secondary">global</Badge>
  }
  return (
    <Badge variant="outline">
      {scope.projectName ?? scope.projectId ?? 'project'}
    </Badge>
  )
}

function RunnerStatusBadge({ status }: { status: RunnerStatusRow['status'] }) {
  const config = STATUS_CONFIG[status]
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-md px-1.5 py-0.5 text-xs font-medium ring-1 ring-inset ${config.badge}`}
    >
      <span className={`inline-block h-1.5 w-1.5 rounded-full ${config.dot}`} aria-hidden="true" />
      {config.label}
    </span>
  )
}

function formatHeartbeatAge(lastHeartbeatAt: string, nowMs = Date.now()) {
  const heartbeatMs = new Date(lastHeartbeatAt).getTime()
  if (!Number.isFinite(heartbeatMs)) return 'unknown'

  const ageMs = Math.max(0, nowMs - heartbeatMs)
  const minuteMs = 60 * 1000
  const hourMs = 60 * minuteMs
  const dayMs = 24 * hourMs

  if (ageMs < minuteMs) return 'just now'
  if (ageMs < hourMs) return `${Math.floor(ageMs / minuteMs)}m ago`
  if (ageMs < dayMs) return `${Math.floor(ageMs / hourMs)}h ago`
  return `${Math.floor(ageMs / dayMs)}d ago`
}

function formatHeartbeatTimestamp(lastHeartbeatAt: string) {
  const heartbeatMs = new Date(lastHeartbeatAt).getTime()
  if (!Number.isFinite(heartbeatMs)) return 'unknown'

  return new Date(heartbeatMs).toLocaleTimeString()
}

function ActiveWorkSummary({
  work,
  toProjectPath,
}: {
  work: RunnerActiveWork
  toProjectPath: (path: string) => string
}) {
  const label = work.title ?? work.workType ?? work.ownerKind
  if (work.issue) {
    return (
      <div className="flex items-center gap-1.5 text-xs" data-testid="active-work-row">
        <span className="text-foreground truncate">{label}</span>
        <span className="text-muted-foreground shrink-0">·</span>
        <Link
          to={toProjectPath(`/issues/${work.issue.issueNumber}`)}
          onClick={(event) => event.stopPropagation()}
          className="text-info hover:text-info-foreground hover:underline shrink-0"
          data-testid="active-work-issue-link"
          data-work-id={work.workId}
        >
          #{work.issue.issueNumber}
        </Link>
        {work.stage && <span className="text-muted-foreground shrink-0">({work.stage})</span>}
      </div>
    )
  }
  return (
    <div className="flex items-center gap-1.5 text-xs" data-testid="active-work-row">
      <span className="text-foreground truncate">{label}</span>
      {work.stage && <span className="text-muted-foreground shrink-0">({work.stage})</span>}
      <span className="text-muted-foreground font-mono shrink-0">{work.ownerId}</span>
    </div>
  )
}

function CapacityIndicator({ used, total }: { used: number; total: number }) {
  const pct = total > 0 ? Math.min(100, (used / total) * 100) : 0
  const color = used >= total ? 'bg-warning' : used > 0 ? 'bg-info' : 'bg-success'
  return (
    <div className="flex items-center gap-2" data-testid="runner-capacity">
      <div className="flex items-center gap-1.5">
        <span className="text-xs tabular-nums text-muted-foreground">
          {used}/{total}
        </span>
        <span className="text-xs text-muted-foreground">slots</span>
      </div>
      <div className="h-1.5 w-16 rounded-full bg-muted overflow-hidden">
        <div className={`h-full rounded-full ${color} transition-all`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

function ModelList({ models, count }: { models: string[]; count: number }) {
  const [expanded, setExpanded] = useState(false)
  const display = expanded ? models : models.slice(0, 5)
  const hidden = count - display.length

  return (
    <div className="flex flex-wrap items-center gap-1">
      {display.map((model) => (
        <span
          key={model}
          className="inline-block rounded bg-muted px-1.5 py-0.5 text-[11px] font-mono text-muted-foreground"
        >
          {model}
        </span>
      ))}
      {hidden > 0 && (
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation()
            setExpanded((v) => !v)
          }}
          className="inline-block rounded bg-muted px-1.5 py-0.5 text-[11px] font-medium text-muted-foreground hover:bg-muted-foreground/20 transition-colors"
        >
          {expanded ? 'show less' : `+${hidden} more`}
        </button>
      )}
    </div>
  )
}

function MetaItem({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center gap-1.5 min-w-0">
      <span className="text-xs text-muted-foreground/70 shrink-0">{label}</span>
      <span className="text-xs text-muted-foreground truncate">{children}</span>
    </div>
  )
}

function RunnerRow({ row }: { row: RunnerStatusRow }) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const detailHref = toProjectPath(`/runners/${encodeURIComponent(row.id)}`)
  const activeWorks = row.activeWorks ?? []

  return (
    <div
      role="link"
      tabIndex={0}
      onClick={() => navigate(detailHref)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          navigate(detailHref)
        }
      }}
      className="block px-4 py-3 border-b border-border last:border-0 hover:bg-muted/40 cursor-pointer transition-colors"
      data-testid="runner-row"
      data-runner-id={row.id}
      data-href={detailHref}
    >
      {/* Identity row */}
      <div className="flex items-center gap-2 flex-wrap">
        <span className="font-mono text-sm font-medium text-foreground">{row.id}</span>
        <RunnerStatusBadge status={row.status} />
        <RunnerScopeLabel scope={row.scope} />
        {row.capacity && (
          <div className="ml-auto">
            <CapacityIndicator used={row.capacity.usedSlots} total={row.capacity.totalSlots} />
          </div>
        )}
      </div>

      {/* Metadata row */}
      <div className="flex items-center gap-3 mt-1.5 flex-wrap">
        {row.hostname && <MetaItem label="host">{row.hostname}</MetaItem>}
        <MetaItem label="heartbeat">
          {row.lastHeartbeatAt ? formatHeartbeatAge(row.lastHeartbeatAt) : 'unknown'}
        </MetaItem>
        {row.lastHeartbeatAt && (
          <MetaItem label="at">{formatHeartbeatTimestamp(row.lastHeartbeatAt)}</MetaItem>
        )}
        {row.connectionState && (
          <span
            className={`text-xs ${row.connectionState === 'connected' ? 'text-success' : 'text-muted-foreground'}`}
          >
            {row.connectionState}
          </span>
        )}
        {row.kind && <MetaItem label="type">{row.kind}</MetaItem>}
      </div>

      {/* Active work */}
      {activeWorks.length > 0 && (
          <div
            className="mt-2 space-y-1 rounded-md bg-info-subtle px-2.5 py-1.5"
          data-testid="runner-active-works"
          data-count={activeWorks.length}
        >
          {activeWorks.map((work) => (
            <ActiveWorkSummary key={work.workId} work={work} toProjectPath={toProjectPath} />
          ))}
        </div>
      )}

      {/* Capabilities */}
      {row.capabilities.length > 0 && (
        <div className="mt-2 flex flex-wrap items-center gap-1">
          {row.capabilities.map((cap) => (
            <span
              key={cap}
              className="inline-block rounded border border-border px-1.5 py-0.5 text-[11px] text-muted-foreground"
            >
              {cap}
            </span>
          ))}
        </div>
      )}

      {/* Models */}
      {row.coderModels.length > 0 && (
        <div className="mt-2">
          <div className="text-[11px] text-muted-foreground/70 mb-1">
            {row.coderModelCount} model{row.coderModelCount !== 1 ? 's' : ''}
          </div>
          <ModelList models={row.coderModels} count={row.coderModelCount} />
        </div>
      )}
    </div>
  )
}

function RunnerEmptyState() {
  return (
    <div className="py-12 text-center">
      <p className="text-sm font-medium text-foreground mb-2">No runners connected</p>
      <p className="text-xs text-muted-foreground">
        Start a runner: <code className="text-xs bg-muted px-1.5 py-0.5 rounded font-mono text-foreground">{RUNNER_START_HINT}</code>
      </p>
    </div>
  )
}

interface RunnerListProps {
  rows: RunnerStatusRow[]
}

export function RunnerList({ rows }: RunnerListProps) {
  if (rows.length === 0) {
    return <RunnerEmptyState />
  }

  return (
    <div>
      {rows.map((row) => (
        <RunnerRow key={row.id} row={row} />
      ))}
    </div>
  )
}

export function RunnerListCard() {
  const { data: rows = [], isLoading } = useRunners()

  return (
    <Card>
      <CardHeader className="pb-0">
        <CardTitle>Runners</CardTitle>
      </CardHeader>
      <CardContent className="pt-3">
        {isLoading ? (
          <div className="py-4 text-xs text-muted-foreground">Loading...</div>
        ) : (
          <RunnerList rows={rows} />
        )}
      </CardContent>
    </Card>
  )
}
