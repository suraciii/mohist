import { Link } from 'react-router-dom'
import { ArrowRightIcon } from 'lucide-react'
import type { Issue, IssueChildRef } from '../../../../entities/issue'
import { IssueHealth, IssueStatus } from '../../../../entities/issue'
import { useProjectPath } from '../../../../entities/project'

export interface CompositeParentOverviewProps {
  children: IssueChildRef[]
  summary: { count: number; doneCount: number; blockedCount: number }
}

const STATUS_PRESENTATION: Record<IssueStatus, { label: string; className: string }> = {
  [IssueStatus.Backlog]: { label: 'Backlog', className: 'bg-muted text-muted-foreground' },
  [IssueStatus.InProgress]: { label: 'In Progress', className: 'bg-blue-100 text-blue-800' },
  [IssueStatus.Done]: { label: 'Done', className: 'bg-emerald-100 text-emerald-800' },
  [IssueStatus.Cancelled]: { label: 'Cancelled', className: 'bg-gray-200 text-gray-700' },
}

function ChildStatusPill({ status }: { status: IssueStatus }) {
  const presentation = STATUS_PRESENTATION[status]
  return (
    <span
      data-testid="composite-child-status-pill"
      data-status={status}
      className={`inline-flex items-center rounded-md px-1.5 py-0.5 text-[10px] font-semibold ${presentation.className}`}
    >
      {presentation.label}
    </span>
  )
}

function ChildBlockedIndicator({ blocked }: { blocked: boolean }) {
  if (!blocked) return null
  return (
    <span
      data-testid="composite-child-blocked-indicator"
      className="inline-flex items-center gap-1 rounded-md bg-red-100 text-red-800 px-1.5 py-0.5 text-[10px] font-semibold"
    >
      Blocked
    </span>
  )
}

function ChildRow({ child }: { child: IssueChildRef }) {
  const toProjectPath = useProjectPath()
  const isBlocked = child.health === IssueHealth.Blocked
  const repository = child.repositoryName ?? null
  return (
    <Link
      to={toProjectPath(`/issues/${child.number}`)}
      data-testid="composite-child-row"
      data-child-number={child.number}
      data-child-status={child.status}
      data-child-blocked={isBlocked ? 'true' : 'false'}
      className="flex min-w-0 items-center justify-between gap-3 rounded-md border border-border bg-background px-3 py-2 text-sm hover:border-muted-foreground/40 transition-colors"
    >
      <div className="flex min-w-0 flex-1 items-center gap-2">
        <span
          data-testid="composite-child-number"
          className="shrink-0 font-mono text-xs tabular-nums text-muted-foreground"
        >
          #{child.number}
        </span>
        <span
          data-testid="composite-child-title"
          className="min-w-0 flex-1 truncate text-foreground"
          title={child.title}
        >
          {child.title}
        </span>
      </div>
      <div className="flex shrink-0 items-center gap-1.5">
        <ChildStatusPill status={child.status} />
        {repository && (
          <span
            data-testid="composite-child-repository"
            data-repository={repository}
            className="inline-flex items-center rounded-md bg-slate-100 text-slate-700 px-1.5 py-0.5 text-[10px] font-medium tabular-nums"
            title={`Target repository: ${repository}`}
          >
            {repository}
          </span>
        )}
        <ChildBlockedIndicator blocked={isBlocked} />
        <ArrowRightIcon className="size-3.5 text-muted-foreground" aria-hidden="true" />
      </div>
    </Link>
  )
}

export function CompositeParentOverview({ children, summary }: CompositeParentOverviewProps) {
  const total = summary.count
  const done = summary.doneCount
  const blocked = summary.blockedCount
  return (
    <section
      data-testid="composite-parent-overview"
      data-child-count={total}
      data-done-count={done}
      data-blocked-count={blocked}
      aria-label="Composite parent overview"
      className="space-y-4"
    >
      <header className="space-y-1">
        <h2 className="text-sm font-semibold text-foreground">Composite Progress</h2>
        <p className="text-xs text-muted-foreground">
          This issue groups {total} child {total === 1 ? 'issue' : 'issues'}.
        </p>
      </header>
      <div
        data-testid="composite-parent-stats"
        className="grid grid-cols-3 gap-3"
      >
        <div
          data-testid="composite-parent-progress-stat"
          data-completed={done >= total && total > 0 ? 'true' : 'false'}
          className="rounded-md border border-border bg-background px-3 py-2"
        >
          <div className="text-[10px] uppercase tracking-wide text-muted-foreground">Done</div>
          <div
            data-testid="composite-parent-progress-label"
            className="mt-0.5 text-sm font-semibold tabular-nums text-foreground"
          >
            {done}/{total} done
          </div>
        </div>
        <div
          data-testid="composite-parent-total-stat"
          className="rounded-md border border-border bg-background px-3 py-2"
        >
          <div className="text-[10px] uppercase tracking-wide text-muted-foreground">Total</div>
          <div className="mt-0.5 text-sm font-semibold tabular-nums text-foreground">
            {total} {total === 1 ? 'child' : 'children'}
          </div>
        </div>
        <div
          data-testid="composite-parent-blocked-stat"
          data-blocked={blocked > 0 ? 'true' : 'false'}
          className="rounded-md border border-border bg-background px-3 py-2"
        >
          <div className="text-[10px] uppercase tracking-wide text-muted-foreground">Blocked</div>
          <div
            data-testid="composite-parent-blocked-label"
            className={`mt-0.5 text-sm font-semibold tabular-nums ${
              blocked > 0 ? 'text-red-700' : 'text-foreground'
            }`}
          >
            {blocked}
          </div>
        </div>
      </div>
      <div className="space-y-2">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          Child Issues
        </h3>
        <div
          data-testid="composite-child-list"
          className="space-y-2"
        >
          {children.length === 0 ? (
            <p
              data-testid="composite-child-empty"
              className="rounded-md border border-dashed border-border bg-background px-3 py-2 text-sm text-muted-foreground"
            >
              No child issues yet.
            </p>
          ) : (
            children.map((child) => <ChildRow key={child.number} child={child} />)
          )}
        </div>
      </div>
    </section>
  )
}

export type CompositeParentIssueInput = Pick<Issue, 'children' | 'childIssuesSummary'>