import type { ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useProject, projectPath } from '../../../entities/project'
import type { RunnerActiveWork, RunnerInventory, RunnerNextAction, RunnerStatusEntry } from '../../../entities/runner'
import { useRunners } from '../../../entities/runner'
import { Card, CardContent, CardHeader, CardTitle } from '@/shared/ui/components/card'
import { Badge } from '@/shared/ui/components/badge'

function displayValue(value: string | number | null | undefined) {
  return value == null || value === '' ? 'unknown' : String(value)
}

function actionLabel(action: RunnerNextAction) {
  return action.code.replaceAll('-', ' ')
}

export function RunnerNextActions({
  actions,
  emptyLabel = 'No action reported.',
}: {
  actions: RunnerNextAction[]
  emptyLabel?: string
}) {
  if (actions.length === 0) {
    return <span className="text-xs text-muted-foreground">{emptyLabel}</span>
  }

  return (
    <div className="space-y-2" data-testid="runner-next-actions">
      {actions.map((action) => (
        <div
          key={`${action.code}-${action.command ?? ''}`}
          className="rounded-md border border-border/70 bg-background px-2.5 py-2"
        >
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant="outline" className="text-[10px] uppercase tracking-wide">
              {actionLabel(action)}
            </Badge>
            <span className="text-xs text-foreground">{action.message}</span>
          </div>
          {action.command && (
            <code className="mt-1 block break-all rounded bg-muted px-1.5 py-1 text-[11px] text-muted-foreground">
              {action.command}
            </code>
          )}
        </div>
      ))}
    </div>
  )
}

function RunnerEmptyState({ inventory }: { inventory?: RunnerInventory | null }) {
  const actions = inventory?.nextActions ?? []
  return (
    <div
      className="rounded-lg border border-dashed border-border bg-muted/30 px-4 py-12 text-center"
      data-testid="runners-empty-state"
    >
      <p className="text-sm font-medium text-foreground mb-2">
        {inventory?.state === 'first-install' ? 'No Runner definitions' : 'No Runners'}
      </p>
      <div className="mx-auto max-w-xl text-left">
        <RunnerNextActions actions={actions} emptyLabel="The Server reported no next action." />
      </div>
    </div>
  )
}

function reasonLabel(code: string) {
  return code.replaceAll('-', ' ')
}

function OwnerKind({ work }: { work: RunnerActiveWork }) {
  const kind = work.ownerKind.toLowerCase()
  const isAgentJob = kind === 'agent-job' || kind === 'agentjob'
  return (
    <Badge
      variant={isAgentJob ? 'secondary' : 'outline'}
      className={
        isAgentJob
          ? 'border-info-border bg-info-subtle text-info'
          : 'border-warning-border bg-warning-subtle text-warning'
      }
    >
      {isAgentJob ? 'AgentJob' : 'Workflow'}
    </Badge>
  )
}

function WorkIssueLink({ work }: { work: RunnerActiveWork }) {
  const { projects } = useProject()
  if (!work.issue) return null
  const project = projects.find((candidate) => candidate.id === work.issue?.projectId)
  if (!project) {
    return (
      <span className="text-xs text-muted-foreground" data-testid="active-work-issue-reference">
        {work.issue.projectId} · #{work.issue.issueNumber}
      </span>
    )
  }
  return (
    <Link
      to={projectPath(project.name, `/issues/${work.issue.issueNumber}`)}
      onClick={(event) => event.stopPropagation()}
      className="text-xs text-info hover:text-info-foreground hover:underline"
      data-testid="active-work-issue-link"
      data-work-id={work.workId}
      data-issue-project-id={work.issue.projectId}
    >
      {project.name} · #{work.issue.issueNumber}
    </Link>
  )
}

function ActiveWorkRow({ work }: { work: RunnerActiveWork }) {
  return (
    <div
      className="flex min-w-0 flex-wrap items-center gap-2 rounded border border-border/60 bg-background/70 px-2 py-1.5"
      data-testid="active-work-row"
      data-owner-kind={work.ownerKind}
    >
      <OwnerKind work={work} />
      <span className="min-w-0 truncate text-xs text-foreground">{work.title ?? work.workType}</span>
      <span className="break-all font-mono text-[11px] text-muted-foreground">{work.ownerId}</span>
      {work.stage && <span className="text-[11px] text-muted-foreground">{work.stage}</span>}
      <WorkIssueLink work={work} />
    </div>
  )
}

function CapacityIndicator({ capacity }: { capacity: RunnerStatusEntry['capacity'] }) {
  if (!capacity) {
    return (
      <span className="text-xs text-muted-foreground" data-testid="runner-capacity">
        unknown / unknown slots
      </span>
    )
  }
  const used = capacity.used
  const pct = used == null || capacity.total <= 0 ? 0 : Math.min(100, (used / capacity.total) * 100)
  const color = used == null ? 'bg-muted-foreground' : used >= capacity.total ? 'bg-warning' : 'bg-info'
  return (
    <div className="flex min-w-[150px] items-center gap-2" data-testid="runner-capacity">
      <span className="shrink-0 text-xs tabular-nums text-muted-foreground">
        {used == null ? 'unknown' : used}/{capacity.total} slots
      </span>
      <div className="h-1.5 min-w-12 flex-1 overflow-hidden rounded-full bg-muted">
        <div className={`h-full rounded-full ${color}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground/70">{label}</dt>
      <dd className="mt-0.5 break-words text-xs text-foreground">{children}</dd>
    </div>
  )
}

function RunnerRow({ row }: { row: RunnerStatusEntry }) {
  const navigate = useNavigate()
  const detailHref = `/runners/${encodeURIComponent(row.identity.id)}`
  const activeWorks = row.activeWorks ?? []
  const reasons = row.admission.reasonCodes ?? []

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
      className="block border-b border-border px-4 py-4 transition-colors last:border-0 hover:bg-muted/40 cursor-pointer"
      data-testid="runner-row"
      data-runner-id={row.identity.id}
      data-href={detailHref}
    >
      <div className="flex min-w-0 flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex min-w-0 flex-wrap items-center gap-2">
            <span className="max-w-full break-all font-mono text-sm font-medium text-foreground">
              {row.identity.id}
            </span>
            <Badge variant={row.presence.state === 'online' ? 'secondary' : 'outline'}>{row.presence.state}</Badge>
            <Badge variant="outline">control {row.control.state}</Badge>
            <Badge variant={row.admission.state === 'ready' ? 'secondary' : 'destructive'}>
              admission {row.admission.state}
            </Badge>
          </div>
          <div className="mt-1 flex min-w-0 flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground">
            <span>host: {displayValue(row.identity.hostname)}</span>
            <span>kind: {displayValue(row.identity.kind)}</span>
            {row.presence.lastObservedAt && (
              <time dateTime={row.presence.lastObservedAt}>observed {row.presence.lastObservedAt}</time>
            )}
          </div>
        </div>
        <CapacityIndicator capacity={row.capacity} />
      </div>

      {reasons.length > 0 && (
        <div className="mt-3 flex flex-wrap items-center gap-1.5" data-testid="runner-admission-reasons">
          <span className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Reasons</span>
          {reasons.map((reason) => (
            <span key={reason} className="rounded bg-warning-subtle px-1.5 py-0.5 text-[11px] text-warning">
              {reasonLabel(reason)}
            </span>
          ))}
        </div>
      )}

      <div className="mt-3 grid min-w-0 gap-3 md:grid-cols-[minmax(0,1fr)_minmax(220px,0.8fr)]">
        <div className="min-w-0">
          <div className="mb-1 flex items-center gap-2">
            <span className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Active owners</span>
            <span className="text-[11px] tabular-nums text-muted-foreground">{activeWorks.length}</span>
          </div>
          {activeWorks.length === 0 ? (
            <span className="text-xs text-muted-foreground">No active owners reported.</span>
          ) : (
            <div className="space-y-1" data-testid="runner-active-works" data-count={activeWorks.length}>
              {activeWorks.map((work) => (
                <ActiveWorkRow key={work.workId} work={work} />
              ))}
            </div>
          )}
        </div>
        <div className="min-w-0">
          <div className="mb-1 text-[10px] font-medium uppercase tracking-wide text-muted-foreground">
            Drain and next actions
          </div>
          {row.drain?.active ? (
            <div className="mb-2 rounded bg-warning-subtle px-2 py-1 text-xs text-warning" data-testid="runner-drain">
              draining · {row.drain.kind}
              {row.drain.updateInterruptId ? ` · ${row.drain.updateInterruptId}` : ''}
            </div>
          ) : (
            <div className="mb-2 text-xs text-muted-foreground" data-testid="runner-drain">
              not draining
            </div>
          )}
          <RunnerNextActions actions={row.nextActions} />
        </div>
      </div>

      <dl className="mt-3 flex flex-wrap gap-x-4 gap-y-2 border-t border-border/60 pt-2">
        <Fact label="Component">{displayValue(row.identity.component)}</Fact>
        <Fact label="Source">{displayValue(row.identity.sourceRevision)}</Fact>
        <Fact label="Release">{displayValue(row.identity.releaseId)}</Fact>
        <Fact label="Generation">{displayValue(row.identity.generation)}</Fact>
        <Fact label="Capabilities">{row.capabilities.length > 0 ? row.capabilities.join(', ') : 'none'}</Fact>
      </dl>
    </div>
  )
}

export interface RunnerListProps {
  rows: RunnerStatusEntry[]
  inventory?: RunnerInventory | null
}

export function RunnerList({ rows, inventory }: RunnerListProps) {
  if (rows.length === 0) return <RunnerEmptyState inventory={inventory} />
  return (
    <div>
      {rows.map((row) => (
        <RunnerRow key={row.identity.id} row={row} />
      ))}
    </div>
  )
}

export function RunnerListCard() {
  const { data, isLoading } = useRunners()
  return (
    <Card>
      <CardHeader className="pb-0">
        <CardTitle>Runners</CardTitle>
      </CardHeader>
      <CardContent className="pt-3">
        {isLoading ? (
          <div className="py-4 text-xs text-muted-foreground">Loading…</div>
        ) : (
          <RunnerList rows={data?.runners ?? []} inventory={data?.inventory} />
        )}
      </CardContent>
    </Card>
  )
}
