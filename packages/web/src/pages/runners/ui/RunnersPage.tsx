import type { RunnerStatusEntry } from '../../../entities/runner'
import { useRunners } from '../../../entities/runner'
import { RunnerList } from '../../../widgets/runner-status'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

function RunnerInventorySummary({ rows }: { rows: RunnerStatusEntry[] }) {
  const ready = rows.filter((row) => row.admission.state === 'ready').length
  const blocked = rows.length - ready
  const activeWorks = rows.reduce((total, row) => total + row.activeWorks.length, 0)
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2" data-testid="runners-summary-bar">
      <span className="text-sm font-medium text-foreground tabular-nums">
        {rows.length} Runner{rows.length === 1 ? '' : 's'}
      </span>
      <span className="text-xs text-muted-foreground">{ready} admission ready</span>
      <span className="text-xs text-muted-foreground">{blocked} admission blocked</span>
      <span className="text-xs text-muted-foreground">
        {activeWorks} active work{activeWorks === 1 ? '' : 's'}
      </span>
    </div>
  )
}

export function RunnersPage({ runnersHook = useRunners }: { runnersHook?: typeof useRunners } = {}) {
  useDocumentTitle('Runners — Mohist')
  const { data, isLoading, isError, error, refetch } = runnersHook()
  const rows = data?.runners ?? []

  return (
    <div data-testid="runners-page" className="flex-1 min-w-0 overflow-y-auto bg-background">
      <div className="mx-auto max-w-5xl space-y-5 px-4 py-6 sm:px-6">
        <div>
          <h1 className="text-lg font-semibold text-foreground">Runners</h1>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Application-wide Runner inventory and current admission facts.
          </p>
        </div>

        {isLoading && (
          <div className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
            Loading Runner inventory…
          </div>
        )}

        {isError && (
          <div
            className="rounded-lg border border-danger-border bg-danger-subtle px-4 py-6"
            data-testid="runners-error-state"
          >
            <p className="text-sm font-medium text-danger">Runner inventory could not be loaded.</p>
            <p className="mt-1 break-words text-xs text-muted-foreground">
              {error instanceof Error ? error.message : 'The Server did not return a Runner snapshot.'}
            </p>
            <button
              type="button"
              onClick={() => void refetch()}
              className="mt-3 rounded border border-border bg-background px-2.5 py-1 text-xs font-medium text-foreground hover:bg-muted"
            >
              Retry
            </button>
          </div>
        )}

        {!isLoading && !isError && (
          <>
            {rows.length > 0 && <RunnerInventorySummary rows={rows} />}
            <div className="overflow-hidden rounded-lg border border-border bg-card">
              <RunnerList rows={rows} inventory={data?.inventory} />
            </div>
          </>
        )}
      </div>
    </div>
  )
}
