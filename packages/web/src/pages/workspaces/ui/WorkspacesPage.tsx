import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useWorkspaces, workspaceOriginLabel, type Workspace, type WorkspaceStatus } from '../../../entities/workspace'
import { useProjectPath } from '../../../entities/project'
import { Badge } from '@/shared/ui/components/badge'
import { Card } from '@/shared/ui/components/card'
import { Button } from '@/shared/ui/components/button'

function StatusBadge({ status }: { status: WorkspaceStatus }) {
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

function WorkspaceCard({ workspace }: { workspace: Workspace }) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const archived = workspace.status === 'archived'

  return (
    <Card
      className={`p-4 transition-colors cursor-pointer ${
        archived ? 'opacity-60 hover:opacity-80' : 'hover:border-muted-foreground/30'
      }`}
      onClick={() => navigate(toProjectPath(`/workspaces/${encodeURIComponent(workspace.name)}`))}
      data-testid={`workspace-card-${workspace.name}`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex flex-wrap items-center gap-2 mb-1">
            <span className="text-sm font-semibold text-foreground break-words" data-testid="workspace-name">
              {workspace.name}
            </span>
            <StatusBadge status={workspace.status} />
            <OriginBadge origin={workspace.origin} />
          </div>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
            <span data-testid="workspace-bound-sessions">
              {workspace.boundSessionCount} bound session{workspace.boundSessionCount === 1 ? '' : 's'}
            </span>
            {workspace.home ? (
              <span data-testid="workspace-home" className="min-w-0">
                Home: <span className="font-mono">{workspace.home.runnerId}</span>{' '}
                <span className="font-mono text-muted-foreground/70 truncate">{workspace.home.path}</span>
              </span>
            ) : (
              <span>Not materialized</span>
            )}
            <span data-testid="workspace-created-at">
              Created {formatDate(workspace.createdAt)}
            </span>
            {workspace.archivedAt && (
              <span data-testid="workspace-archived-at">
                Archived {formatDate(workspace.archivedAt)}
              </span>
            )}
          </div>
        </div>
      </div>
    </Card>
  )
}

function formatDate(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toISOString().slice(0, 10)
}

function WorkspaceSection({
  title,
  workspaces,
  defaultExpanded,
  testIdPrefix,
}: {
  title: string
  workspaces: Workspace[]
  defaultExpanded: boolean
  testIdPrefix: string
}) {
  const [expanded, setExpanded] = useState(defaultExpanded)

  return (
    <section data-testid={testIdPrefix}>
      <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
        <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">
          {title} ({workspaces.length})
        </h2>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => setExpanded(prev => !prev)}
          aria-expanded={expanded}
          data-testid={`${testIdPrefix}-toggle`}
          className="text-muted-foreground hover:text-foreground"
        >
          {expanded ? 'Collapse' : 'Expand'}
        </Button>
      </div>
      {expanded && (
        <div className="grid gap-4">
          {workspaces.map(workspace => (
            <WorkspaceCard key={workspace.name} workspace={workspace} />
          ))}
        </div>
      )}
    </section>
  )
}

export function WorkspacesPage() {
  const { data: workspaces, isLoading } = useWorkspaces()

  const active = workspaces?.filter(w => w.status === 'active') ?? []
  const archived = workspaces?.filter(w => w.status === 'archived') ?? []

  return (
    <div className="max-w-4xl mx-auto p-6">
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-foreground">Workspaces</h1>
        <p className="text-sm text-muted-foreground mt-1">
          Persistent execution environments in this project: who is bound to them, where they are materialized, and whether they can still be entered.
        </p>
      </div>

      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="text-muted-foreground">Loading...</div>
        </div>
      ) : workspaces && workspaces.length === 0 ? (
        <div className="text-center py-12">
          <div className="text-muted-foreground text-lg mb-4">No workspaces yet</div>
          <div className="text-sm text-muted-foreground">
            Workspaces are created when an issue workflow starts or when a session is launched into one.
          </div>
        </div>
      ) : (
        <div className="space-y-8">
          {active.length > 0 && (
            <WorkspaceSection
              title="Active"
              workspaces={active}
              defaultExpanded={true}
              testIdPrefix="workspace-section-active"
            />
          )}
          {archived.length > 0 && (
            <WorkspaceSection
              title="Archived"
              workspaces={archived}
              defaultExpanded={false}
              testIdPrefix="workspace-section-archived"
            />
          )}
        </div>
      )}
    </div>
  )
}
