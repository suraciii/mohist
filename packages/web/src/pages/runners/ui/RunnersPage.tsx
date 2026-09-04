import { useMemo, useState } from 'react'
import type { RunnerStatusRow } from '../../../entities/runner'
import { useRunners } from '../../../entities/runner'
import { useProject } from '../../../entities/project'
import { RunnerList } from '../../../widgets/runner-status'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

type ScopeFilter = 'all' | 'global' | 'project'

const SCOPE_FILTERS: { key: ScopeFilter; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'global', label: 'Global' },
  { key: 'project', label: 'This project' },
]

const STATUS_KEYS = ['idle', 'busy', 'stale', 'offline'] as const
type StatusKey = (typeof STATUS_KEYS)[number]

const STATUS_LABEL: Record<StatusKey, string> = {
  idle: 'idle',
  busy: 'busy',
  stale: 'stale',
  offline: 'offline',
}

const STATUS_DOT: Record<StatusKey, string> = {
  idle: 'bg-emerald-500',
  busy: 'bg-blue-500',
  stale: 'bg-amber-500',
  offline: 'bg-gray-400',
}

const RUNNER_INSTALL_COMMAND = 'mo install runner --repo-root <path>'
const RUNNER_START_COMMAND = 'mo service start runner'

function filterByScope(rows: RunnerStatusRow[], scope: ScopeFilter): RunnerStatusRow[] {
  if (scope === 'all') return rows
  if (scope === 'global') return rows.filter((row) => row.scope.type === 'global')
  return rows.filter((row) => row.scope.type === 'project')
}

function countByStatus(rows: RunnerStatusRow[]): Record<StatusKey, number> {
  const counts: Record<StatusKey, number> = { idle: 0, busy: 0, stale: 0, offline: 0 }
  for (const row of rows) {
    const status = row.status as StatusKey
    counts[status] += 1
  }
  return counts
}

function ScopeFilterBar({
  value,
  onChange,
}: {
  value: ScopeFilter
  onChange: (next: ScopeFilter) => void
}) {
  return (
    <div
      role="group"
      aria-label="Runner scope filter"
      data-testid="runners-scope-filter"
      className="inline-flex items-center rounded-lg bg-muted p-0.5"
    >
      {SCOPE_FILTERS.map((option) => {
        const active = option.key === value
        return (
          <button
            key={option.key}
            type="button"
            onClick={() => onChange(option.key)}
            aria-pressed={active}
            data-testid={`runners-scope-${option.key}`}
            className={`px-3 py-1 text-xs font-medium rounded-md transition-colors ${
              active
                ? 'bg-background text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'
            }`}
          >
            {option.label}
          </button>
        )
      })}
    </div>
  )
}

function RunnerStatusSummaryBar({ rows }: { rows: RunnerStatusRow[] }) {
  const counts = countByStatus(rows)
  const total = rows.length
  return (
    <div
      data-testid="runners-summary-bar"
      data-scope-counts={JSON.stringify(counts)}
      className="flex items-center gap-4 flex-wrap"
    >
      <span className="text-sm font-medium text-foreground tabular-nums">{total} runner{total !== 1 ? 's' : ''}</span>
      <span className="h-4 w-px bg-border" />
      {STATUS_KEYS.map((status) => (
        <div
          key={status}
          data-testid={`runners-summary-${status}`}
          className="inline-flex items-center gap-1.5"
        >
          <span className={`inline-block h-2 w-2 rounded-full ${STATUS_DOT[status]}`} aria-hidden="true" />
          <span className="text-xs text-muted-foreground">{STATUS_LABEL[status]}</span>
          <span
            data-testid={`runners-summary-${status}-count`}
            className="text-xs font-semibold tabular-nums text-foreground"
          >
            {counts[status]}
          </span>
        </div>
      ))}
    </div>
  )
}

function RunnerEmptyState() {
  return (
    <div
      data-testid="runners-empty-state"
      className="rounded-lg border border-dashed border-border bg-muted/30 px-4 py-16 text-center"
    >
      <p className="text-sm font-medium text-foreground mb-2">No runners connected</p>
      <p className="text-xs text-muted-foreground">
        First install:{' '}
        <code className="text-xs bg-background border border-border px-1.5 py-0.5 rounded font-mono text-foreground">
          {RUNNER_INSTALL_COMMAND}
        </code>
        <br />
        Later starts:{' '}
        <code className="text-xs bg-background border border-border px-1.5 py-0.5 rounded font-mono text-foreground">
          {RUNNER_START_COMMAND}
        </code>
      </p>
    </div>
  )
}

function NoProjectState() {
  return (
    <div
      data-testid="runners-no-project-state"
      className="rounded-lg border border-dashed border-border bg-muted/30 px-4 py-16 text-center"
    >
      <p className="text-sm font-medium text-foreground">No project selected</p>
      <p className="text-xs text-muted-foreground mt-1">
        Select a project to view its eligible runners.
      </p>
    </div>
  )
}

export function RunnersPage({
  runnersHook = useRunners,
}: {
  runnersHook?: typeof useRunners
} = {}) {
  useDocumentTitle('Runners — Mohist')

  const { projectId } = useProject()
  const [scope, setScope] = useState<ScopeFilter>('all')
  const { data: rows = [] } = runnersHook()

  const filteredRows = useMemo(() => filterByScope(rows, scope), [rows, scope])

  return (
    <div
      data-testid="runners-page"
      data-scope-filter={scope}
      className="flex-1 overflow-y-auto bg-background"
    >
      <div className="max-w-4xl mx-auto px-6 py-6 space-y-5">
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <div>
            <h1 className="text-lg font-semibold text-foreground">Runners</h1>
            <p className="text-xs text-muted-foreground mt-0.5">
              Live status of all runners eligible for this project.
            </p>
          </div>
          <ScopeFilterBar value={scope} onChange={setScope} />
        </div>

        {projectId ? (
          <>
            <RunnerStatusSummaryBar rows={filteredRows} />
            {filteredRows.length === 0 ? (
              <RunnerEmptyState />
            ) : (
              <div className="rounded-lg border border-border bg-card overflow-hidden">
                <RunnerList rows={filteredRows} />
              </div>
            )}
          </>
        ) : (
          <NoProjectState />
        )}
      </div>
    </div>
  )
}
