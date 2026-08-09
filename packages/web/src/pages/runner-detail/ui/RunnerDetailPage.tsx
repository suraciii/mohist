import { ArrowLeftIcon } from 'lucide-react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError } from '../../../shared/api/client'
import type { RunnerActiveWork, RunnerStatusRow } from '../../../entities/runner'
import { useRunner } from '../../../entities/runner'
import { useProjectPath } from '../../../entities/project'
import { CardSection } from '@/shared/ui/components/card-section'
import { Card } from '@/shared/ui/components/card'
import { Button } from '@/shared/ui/components/button'
import {
  SlotsEditor,
  type SlotsEditorMutationHook,
} from '../../../widgets/runner-status'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

function formatTimestamp(value: string | null | undefined): string {
  if (!value) return 'unknown'
  const ms = new Date(value).getTime()
  if (!Number.isFinite(ms)) return value
  return new Date(ms).toLocaleString()
}

function formatRelative(value: string | null | undefined): string {
  if (!value) return 'unknown'
  const diff = Math.max(0, Date.now() - new Date(value).getTime())
  const seconds = Math.floor(diff / 1000)
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  return `${days}d ago`
}

function StatusBadge({ status }: { status: RunnerStatusRow['status'] }) {
  const variants: Record<RunnerStatusRow['status'], string> = {
    idle: 'bg-green-100 text-green-700',
    busy: 'bg-blue-100 text-blue-700',
    stale: 'bg-amber-100 text-amber-700',
    offline: 'bg-gray-100 text-gray-500',
  }
  return (
    <span
      data-testid="runner-status-badge"
      data-status={status}
      className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ${variants[status]}`}
    >
      {status}
    </span>
  )
}

function ScopeBadge({ row }: { row: RunnerStatusRow }) {
  if (row.scope.type === 'global') {
    return (
      <span data-testid="runner-scope-badge" className="inline-flex items-center rounded-full bg-gray-100 text-gray-700 px-2 py-0.5 text-xs font-medium">
        global
      </span>
    )
  }
  return (
    <span
      data-testid="runner-scope-badge"
      data-project-id={row.scope.projectId ?? undefined}
      data-project-name={row.scope.projectName ?? undefined}
      className="inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium"
    >
      {row.scope.projectName ?? row.scope.projectId ?? 'project'}
    </span>
  )
}

function ConnectionBadge({ state }: { state: string | null | undefined }) {
  if (!state) return <span className="text-xs text-gray-400">connection unknown</span>
  const tone = state === 'connected' ? 'text-green-600' : 'text-gray-400'
  return (
    <span data-testid="runner-connection-state" data-state={state} className={`text-xs ${tone}`}>
      {state}
    </span>
  )
}

function ActiveWorkRow({
  work,
  toProjectPath,
}: {
  work: RunnerActiveWork
  toProjectPath: (path: string) => string
}) {
  const label = work.title ?? work.workType ?? work.ownerKind
  return (
    <div
      className="flex flex-col gap-1 rounded-md border border-border/60 p-3"
      data-testid="active-work-detail-row"
      data-work-id={work.workId}
      data-owner-kind={work.ownerKind}
    >
      <div className="flex items-baseline gap-2 flex-wrap">
        <span className="text-sm font-medium text-foreground">{label}</span>
        {work.stage && (
          <span className="text-xs text-muted-foreground">stage: {work.stage}</span>
        )}
        <span className="ml-auto text-xs text-muted-foreground font-mono">{work.ownerKind}</span>
      </div>
      <div className="flex items-center gap-2 text-xs text-muted-foreground flex-wrap">
        <span className="font-mono" data-testid="active-work-work-id">{work.workId}</span>
        <span className="text-gray-300">·</span>
        <span className="font-mono" data-testid="active-work-owner-id">{work.ownerId}</span>
        {work.issue ? (
          <>
            <span className="text-gray-300">·</span>
            <Link
              to={toProjectPath(`/issues/${work.issue.issueNumber}`)}
              className="text-blue-600 hover:text-blue-700 hover:underline"
              data-testid="active-work-issue-link"
              data-issue-number={work.issue.issueNumber}
              data-issue-project-id={work.issue.projectId}
            >
              issue #{work.issue.issueNumber}
            </Link>
          </>
        ) : null}
      </div>
    </div>
  )
}

function RunnerDetailContent({
  row,
  slotsMutationHook,
}: {
  row: RunnerStatusRow
  slotsMutationHook?: SlotsEditorMutationHook
}) {
  const toProjectPath = useProjectPath()
  const activeWorks = row.activeWorks ?? []
  const maxSlots = row.maxWorkflowSlots ?? row.capacity?.totalSlots ?? null

  return (
    <>
      <div className="mb-6" data-testid="runner-detail-header">
        <div className="flex flex-wrap items-center gap-2 mb-2">
          <span className="font-mono text-sm font-medium text-foreground" data-testid="runner-detail-id">
            {row.id}
          </span>
          <span className="text-xs text-muted-foreground">{row.kind}</span>
          <StatusBadge status={row.status} />
          <ScopeBadge row={row} />
        </div>
        <h1 className="text-2xl font-bold text-foreground">{row.id}</h1>
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <CardSection title="Identity">
          <dl className="space-y-2 text-sm" data-testid="runner-detail-identity">
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Runner id</dt>
              <dd className="font-mono text-foreground text-right break-all" data-testid="runner-detail-id-cell">{row.id}</dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Kind</dt>
              <dd className="text-foreground" data-testid="runner-detail-kind">{row.kind}</dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Hostname</dt>
              <dd className="text-foreground" data-testid="runner-detail-hostname">{row.hostname || '—'}</dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Scope</dt>
              <dd><ScopeBadge row={row} /></dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Registered at</dt>
              <dd className="text-foreground" data-testid="runner-detail-registered-at">{formatTimestamp(row.registeredAt)}</dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Build git hash</dt>
              <dd className="font-mono text-foreground text-right break-all" data-testid="runner-detail-build-git-hash">
                {row.buildGitHash ?? '—'}
              </dd>
            </div>
          </dl>
        </CardSection>

        <CardSection title="Capabilities">
          <div className="space-y-2 text-sm" data-testid="runner-detail-capabilities">
            <div>
              <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-1">Capabilities</div>
              {row.capabilities.length === 0 ? (
                <div className="text-xs text-muted-foreground">none</div>
              ) : (
                <div className="flex flex-wrap gap-1" data-testid="runner-detail-capability-list">
                  {row.capabilities.map((cap) => (
                    <span
                      key={cap}
                      className="inline-flex items-center rounded-full bg-gray-100 text-gray-700 px-2 py-0.5 text-xs"
                    >
                      {cap}
                    </span>
                  ))}
                </div>
              )}
            </div>
            <div>
              <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-1">Coder models</div>
              {row.coderModels.length === 0 ? (
                <div className="text-xs text-muted-foreground">none</div>
              ) : (
                <div className="text-foreground" data-testid="runner-detail-coder-models">
                  {row.coderModelCount} model{row.coderModelCount !== 1 ? 's' : ''}: {row.coderModels.join(', ')}
                </div>
              )}
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Max execution slots</dt>
              <dd className="text-foreground" data-testid="runner-detail-max-slots">
                {maxSlots != null ? (
                  <SlotsEditor runnerId={row.id} value={maxSlots} mutationHook={slotsMutationHook} />
                ) : (
                  '—'
                )}
              </dd>
            </div>
            <p className="text-[11px] text-muted-foreground mt-1">
              Limits the combined Workflow work and AgentJobs on this Runner.
            </p>
          </div>
        </CardSection>

        <CardSection title="Active Works" data-testid="runner-detail-active-works-section">
          {activeWorks.length === 0 ? (
            <div className="text-sm text-muted-foreground" data-testid="runner-detail-no-active-works">
              No active works.
            </div>
          ) : (
            <div
              className="space-y-2"
              data-testid="runner-detail-active-works-list"
              data-count={activeWorks.length}
            >
              {activeWorks.map((work) => (
                <ActiveWorkRow key={work.workId} work={work} toProjectPath={toProjectPath} />
              ))}
            </div>
          )}
        </CardSection>

        <CardSection title="Health">
          <dl className="space-y-2 text-sm" data-testid="runner-detail-health">
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Status</dt>
              <dd><StatusBadge status={row.status} /></dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Connection state</dt>
              <dd><ConnectionBadge state={row.connectionState} /></dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-muted-foreground">Last heartbeat</dt>
              <dd className="text-foreground" data-testid="runner-detail-last-heartbeat">
                {row.lastHeartbeatAt ? `${formatTimestamp(row.lastHeartbeatAt)} (${formatRelative(row.lastHeartbeatAt)})` : 'unknown'}
              </dd>
            </div>
            {row.capacity && (
              <div className="flex justify-between gap-3">
                <dt className="text-muted-foreground">Capacity</dt>
                <dd className="text-foreground" data-testid="runner-detail-capacity">
                  {row.capacity.usedSlots}/{row.capacity.totalSlots} slots
                </dd>
              </div>
            )}
          </dl>
        </CardSection>
      </div>
    </>
  )
}

export interface RunnerDetailPageDependencies {
  runnerHook?: typeof useRunner
  slotsMutationHook?: SlotsEditorMutationHook
}

export function RunnerDetailPage({
  dependencies,
}: {
  dependencies?: RunnerDetailPageDependencies
} = {}) {
  const { runnerId } = useParams<{ runnerId: string }>()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const runnerHook = dependencies?.runnerHook ?? useRunner
  const { data: runner, isLoading, error } = runnerHook(runnerId)
  useDocumentTitle(`Runner ${runnerId ?? ''} — Mohist`)

  if (error && (error instanceof ApiError ? error.status === 404 : (error as { status?: number }).status === 404)) {
    return (
      <div className="flex-1 min-w-0 overflow-y-auto" data-testid="runner-detail-page">
        <div className="mx-auto max-w-3xl px-4 sm:px-6 py-10">
          <Card className="p-8 text-center" data-testid="runner-not-found">
            <div className="text-lg font-medium text-foreground mb-2">Runner not found</div>
            <div className="text-sm text-muted-foreground mb-4">
              {`Runner '${runnerId}' is not registered to this project.`}
            </div>
            <div className="flex items-center justify-center gap-2">
              <Button
                type="button"
                variant="outline"
                onClick={() => navigate(toProjectPath('/activity'))}
                data-testid="runner-not-found-back"
              >
                Back to activity
              </Button>
              <Link
                to={toProjectPath('/activity')}
                className="text-sm text-blue-600 hover:text-blue-700"
              >
                Or via list
              </Link>
            </div>
          </Card>
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="flex-1 min-w-0 overflow-y-auto" data-testid="runner-detail-page">
        <div className="mx-auto max-w-3xl px-4 sm:px-6 py-10">
          <Card className="p-8 text-center" data-testid="runner-detail-error">
            <div className="text-lg font-medium text-foreground mb-2">Failed to load runner</div>
            <div className="text-sm text-muted-foreground mb-4">{error.message}</div>
            <Button
              type="button"
              variant="outline"
              onClick={() => navigate(toProjectPath('/activity'))}
            >
              Back to activity
            </Button>
          </Card>
        </div>
      </div>
    )
  }

  if (isLoading || !runner) {
    return (
      <div className="flex items-center justify-center flex-1" data-testid="runner-detail-loading">
        <div className="text-gray-400">Loading...</div>
      </div>
    )
  }

  return (
    <div className="flex-1 min-w-0 overflow-y-auto" data-testid="runner-detail-page">
      <div className="mx-auto max-w-5xl px-4 sm:px-6 py-6">
        <button
          type="button"
          onClick={() => navigate(toProjectPath('/activity'))}
          className="mb-4 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
          data-testid="runner-detail-back"
        >
          <ArrowLeftIcon className="size-3.5" />
          <span>Back to activity</span>
        </button>
        <RunnerDetailContent row={runner} slotsMutationHook={dependencies?.slotsMutationHook} />
      </div>
    </div>
  )
}
