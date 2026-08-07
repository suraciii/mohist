import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ArchiveIcon, ChevronLeftIcon } from 'lucide-react'
import { useWorkspace, useCloseWorkspace, workspaceOriginLabel, type Workspace } from '../../../entities/workspace'
import { useProjectPath } from '../../../entities/project'
import { ApiError } from '../../../shared/api/client'
import { ErrorState } from '../../../shared/ui/error-state'
import { Badge } from '@/shared/ui/components/badge'
import { Card } from '@/shared/ui/components/card'
import { Button } from '@/shared/ui/components/button'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'

function StatusBadge({ status }: { status: Workspace['status'] }) {
  const active = status === 'active'
  return (
    <Badge className={active ? 'bg-green-100 text-green-700' : 'bg-gray-100 text-gray-700'}>
      {active ? 'Active' : 'Archived'}
    </Badge>
  )
}

function OriginBadge({ origin }: { origin: Workspace['origin'] }) {
  const colors: Record<string, string> = {
    issue: 'bg-blue-100 text-blue-700',
    slack: 'bg-purple-100 text-purple-700',
    web: 'bg-cyan-100 text-cyan-700',
    manual: 'bg-amber-100 text-amber-700',
    unknown: 'bg-gray-100 text-gray-700',
  }
  return (
    <Badge className={colors[origin.kind] ?? 'bg-gray-100 text-gray-700'}>
      {workspaceOriginLabel(origin)}
    </Badge>
  )
}

function CloseError({ error }: { error: Error }) {
  const details = error instanceof ApiError ? error.details : undefined
  const hint = details && typeof details === 'object' && 'hint' in details
    ? String((details as { hint: unknown }).hint)
    : null

  return (
    <div
      role="alert"
      data-testid="workspace-close-error"
      className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-sm text-danger"
    >
      <div className="font-medium" data-testid="workspace-close-error-message">{error.message}</div>
      {hint && (
        <div className="mt-1 text-danger-foreground/90" data-testid="workspace-close-error-hint">
          {hint}
        </div>
      )}
    </div>
  )
}

function SessionRow({ session }: { session: NonNullable<Workspace['sessions']>[number] }) {
  const toProjectPath = useProjectPath()
  const label = session.sessionName ?? session.agentName ?? session.id

  return (
    <Link
      to={toProjectPath(`/sessions/${encodeURIComponent(session.id)}`)}
      data-testid={`workspace-session-${session.id}`}
      className="flex items-center justify-between gap-3 rounded-md border border-border px-3 py-2 text-sm hover:border-muted-foreground/30 transition-colors"
    >
      <span className="font-medium text-foreground truncate">{label}</span>
      <span className="text-muted-foreground shrink-0">
        {session.activity}{session.model ? ` · ${session.model}` : ''}
      </span>
    </Link>
  )
}

export function WorkspaceDetailPage() {
  const { name: rawName } = useParams<{ name: string }>()
  const name = rawName ? decodeURIComponent(rawName) : null
  const { data: workspace, isLoading, isError, error, refetch } = useWorkspace(name)
  const closeWorkspace = useCloseWorkspace()
  const toProjectPath = useProjectPath()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [closeError, setCloseError] = useState<Error | null>(null)

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <div className="text-muted-foreground">Loading...</div>
      </div>
    )
  }

  if (isError) {
    const notFound = error instanceof ApiError && error.status === 404
    return (
      <ErrorState
        title={notFound ? 'Workspace not found' : 'Could not load workspace'}
        message={notFound ? `No workspace named '${name ?? ''}' exists in this project.` : error.message}
        onRetry={() => refetch()}
        testId="workspace-detail-error"
      />
    )
  }

  if (!workspace) return null

  const active = workspace.status === 'active'

  const handleCloseConfirm = () => {
    setCloseError(null)
    closeWorkspace.mutate(workspace.name, {
      onError: (err) => setCloseError(err),
      onSuccess: () => setConfirmOpen(false),
    })
  }

  return (
    <div className="max-w-4xl mx-auto p-6">
      <div className="mb-4">
        <Link
          to={toProjectPath('/workspaces')}
          className="inline-flex items-center gap-1 text-sm text-info hover:text-info/80 transition-colors"
          data-testid="workspace-detail-back-link"
        >
          <ChevronLeftIcon className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span>Workspaces</span>
        </Link>
      </div>
      <div className="flex flex-wrap items-start justify-between gap-3 mb-6">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2 mb-1">
            <h1 className="text-2xl font-bold text-foreground break-words" data-testid="workspace-detail-name">
              {workspace.name}
            </h1>
            <StatusBadge status={workspace.status} />
            <OriginBadge origin={workspace.origin} />
          </div>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
            <span data-testid="workspace-detail-created-at">Created {workspace.createdAt.slice(0, 10)}</span>
            {workspace.archivedAt && (
              <span data-testid="workspace-detail-archived-at">Archived {workspace.archivedAt.slice(0, 10)}</span>
            )}
          </div>
        </div>
        {active && (
          <Button
            variant="destructive"
            onClick={() => setConfirmOpen(true)}
            data-testid="workspace-close-trigger"
          >
            <ArchiveIcon className="h-4 w-4" aria-hidden="true" />
            Close workspace
          </Button>
        )}
      </div>

      {closeError && <div className="mb-4"><CloseError error={closeError} /></div>}

      <div className="space-y-6">
        <Card className="p-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide mb-2">Home</h2>
          {workspace.home ? (
            <div className="text-sm" data-testid="workspace-detail-home">
              <span className="font-mono">{workspace.home.runnerId}</span>
              <span className="text-muted-foreground"> · </span>
              <span className="font-mono text-muted-foreground">{workspace.home.path}</span>
            </div>
          ) : (
            <div className="text-sm text-muted-foreground" data-testid="workspace-detail-home-empty">
              Not materialized on any runner
            </div>
          )}
        </Card>

        <Card className="p-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide mb-2">
            Repositories ({workspace.repositories.length})
          </h2>
          {workspace.repositories.length === 0 ? (
            <div className="text-sm text-muted-foreground">No repositories attached</div>
          ) : (
            <div className="flex flex-wrap gap-2">
              {workspace.repositories.map(repo => (
                <Badge key={repo} variant="secondary" data-testid="workspace-repository">
                  {repo}
                </Badge>
              ))}
            </div>
          )}
        </Card>

        <Card className="p-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide mb-2">
            Bound sessions ({workspace.sessions?.length ?? 0})
          </h2>
          {workspace.sessions && workspace.sessions.length > 0 ? (
            <div className="grid gap-2">
              {workspace.sessions.map(session => (
                <SessionRow key={session.id} session={session} />
              ))}
            </div>
          ) : (
            <div className="text-sm text-muted-foreground">No sessions bound to this workspace</div>
          )}
        </Card>
      </div>

      <AlertDialog
        open={confirmOpen}
        onOpenChange={(open) => {
          if (closeWorkspace.isPending) return
          setConfirmOpen(open)
        }}
        title="Close this workspace?"
        description={`Archive '${workspace.name}'? Archived workspaces keep their history but no longer accept new sessions.`}
        confirmLabel="Close workspace"
        cancelLabel="Cancel"
        tone="destructive"
        loading={closeWorkspace.isPending}
        onConfirm={handleCloseConfirm}
        data-testid="workspace-close-confirm"
      />
    </div>
  )
}
