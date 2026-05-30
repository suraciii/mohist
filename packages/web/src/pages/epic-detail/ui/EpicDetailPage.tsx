import { useMemo, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useProject } from '../../../entities/project'
import { useIssues } from '../../../entities/issue'
import {
  useAddEpicIssue,
  useCloseEpic,
  useEpic,
  useMarkEpicDone,
  useRemoveEpicIssue,
} from '../../../entities/epic'
import { EpicStatus, type LinkedIssue } from '../../../entities/epic'
import { ApiError } from '../../../shared/api/client'
import { Button } from '@/shared/ui/components/button'
import { Card } from '@/shared/ui/components/card'
import { Badge } from '@/shared/ui/components/badge'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'

function PriorityBadge({ priority }: { priority: string }) {
  const colors: Record<string, string> = {
    p0: 'bg-red-100 text-red-700',
    p1: 'bg-orange-100 text-orange-700',
    p2: 'bg-yellow-100 text-yellow-700',
    p3: 'bg-blue-100 text-blue-700',
    p4: 'bg-gray-100 text-gray-700',
  }

  return (
    <Badge className={colors[priority] || 'bg-gray-100 text-gray-700'}>
      {priority.toUpperCase()}
    </Badge>
  )
}

function StatusBadge({ status }: { status: EpicStatus }) {
  const colors: Record<EpicStatus, string> = {
    [EpicStatus.Active]: 'bg-green-100 text-green-700',
    [EpicStatus.Done]: 'bg-blue-100 text-blue-700',
    [EpicStatus.Closed]: 'bg-gray-100 text-gray-700',
  }

  return (
    <Badge className={colors[status]}>
      {status}
    </Badge>
  )
}

function toTitleCase(value: string) {
  return value.charAt(0).toUpperCase() + value.slice(1)
}

function issueStatusTone(health: string) {
  switch (health) {
    case 'blocked':
      return 'bg-red-50 text-red-700'
    case 'done':
    case 'cancelled':
      return 'bg-green-50 text-green-700'
    default:
      return 'bg-gray-50 text-gray-700'
  }
}

function LinkedIssueRow({ issue, onRemove, disabled }: { issue: LinkedIssue; onRemove: (issueId: string) => void; disabled: boolean }) {
  return (
    <Card className="flex items-center justify-between gap-4 p-4">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          <Link to={`/issue/${issue.number}`} className="font-medium text-blue-600 hover:text-blue-700 hover:underline">
            #{issue.number}
          </Link>
          <span className={`rounded px-2 py-0.5 text-xs font-medium ${issueStatusTone(issue.health)}`}>{issue.health}</span>
          <Badge variant="secondary">{toTitleCase(issue.status)}</Badge>
          {issue.priority && <Badge variant="secondary">{issue.priority.toUpperCase()}</Badge>}
        </div>
        <div className="mt-1 truncate text-sm font-medium text-foreground">{issue.title}</div>
      </div>
      <Button
        type="button"
        variant="outline"
        onClick={() => onRemove(issue.id)}
        disabled={disabled}
      >
        Remove
      </Button>
    </Card>
  )
}

function formatAddIssueError(error: unknown): string {
  if (error instanceof ApiError && error.code === 'DUPLICATE_EPIC_MEMBERSHIP') {
    const details = error.details as { existingEpicId?: string; existingEpicTitle?: string } | undefined
    if (details?.existingEpicTitle) {
      const epicLabel = details.existingEpicId ? `#${details.existingEpicId.slice(0, 8)} ${details.existingEpicTitle}` : details.existingEpicTitle
      return `Issue already belongs to Epic ${epicLabel}.`
    }
  }

  if (error instanceof ApiError && error.code === 'ISSUE_NOT_FOUND') {
    return 'Issue not found.'
  }

  return error instanceof Error ? error.message : 'Failed to add issue'
}

export function EpicDetailPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const { projectId } = useProject()
  const { data: epic, isLoading } = useEpic(id)
  const { data: issues } = useIssues(projectId ? { projectId } : undefined)
  const addEpicIssue = useAddEpicIssue()
  const removeEpicIssue = useRemoveEpicIssue()
  const markEpicDone = useMarkEpicDone()
  const closeEpic = useCloseEpic()
  const [selectedIssueId, setSelectedIssueId] = useState('')

  const availableIssues = useMemo(() => {
    if (!issues || !epic) return []
    const linkedIds = new Set(epic.linkedIssues.map(issue => issue.id))
    return issues.filter(issue => !linkedIds.has(issue.id))
  }, [epic, issues])

  if (isLoading) {
    return <div className="flex items-center justify-center py-12 text-muted-foreground">Loading...</div>
  }

  if (!epic) {
    return (
      <div className="mx-auto max-w-4xl p-6">
        <Card className="p-8 text-center">
          <div className="text-lg font-medium text-foreground">Epic not found</div>
          <Button
            type="button"
            variant="link"
            onClick={() => navigate('/epics')}
            className="mt-4"
          >
            Back to Epics
          </Button>
        </Card>
      </div>
    )
  }

  const progressPercent = epic.progress.totalIssueCount > 0
    ? (epic.progress.completedCount / epic.progress.totalIssueCount) * 100
    : 0
  const epicId = epic.id

  function handleAddIssue(event: FormEvent) {
    event.preventDefault()
    if (!selectedIssueId) return
    addEpicIssue.mutate(
      { epicId, issueId: selectedIssueId },
      { onSuccess: () => setSelectedIssueId('') },
    )
  }

  return (
    <div className="mx-auto max-w-4xl space-y-6 p-6">
      <div>
        <Button
          type="button"
          variant="link"
          onClick={() => navigate('/epics')}
          className="px-0"
        >
          Back to Epics
        </Button>
      </div>

      <Card className="p-6">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
              <span>#{epic.id.slice(0, 8)}</span>
              <StatusBadge status={epic.status} />
              <PriorityBadge priority={epic.priority} />
            </div>
            <h1 className="mt-2 text-2xl font-bold text-foreground">{epic.title}</h1>
            <p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-foreground/80">{epic.description}</p>
          </div>
          <div className="flex flex-wrap gap-2">
            {epic.status === EpicStatus.Active && (
              <Button
                type="button"
                onClick={() => markEpicDone.mutate(epic.id)}
                disabled={markEpicDone.isPending}
              >
                {markEpicDone.isPending ? 'Marking...' : 'Mark Done'}
              </Button>
            )}
            {epic.status !== EpicStatus.Closed && (
              <Button
                type="button"
                variant="outline"
                onClick={() => closeEpic.mutate(epic.id)}
                disabled={closeEpic.isPending}
              >
                {closeEpic.isPending ? 'Closing...' : 'Close Epic'}
              </Button>
            )}
          </div>
        </div>

        <div className="mt-6 grid gap-4 md:grid-cols-3">
          <div className="rounded-lg bg-muted p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Progress</div>
            <div className="mt-2 text-2xl font-semibold text-foreground">
              {epic.progress.completedCount} / {epic.progress.totalIssueCount}
            </div>
            <div className="mt-3 h-2 rounded-full bg-background">
              <div className="h-2 rounded-full bg-blue-600" style={{ width: `${progressPercent}%` }} />
            </div>
          </div>
          <div className="rounded-lg bg-muted p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Next Issue</div>
            {epic.progress.nextIssue ? (
              <Link to={`/issue/${epic.progress.nextIssue.number}`} className="mt-2 block text-sm font-medium text-blue-600 hover:text-blue-700 hover:underline">
                #{epic.progress.nextIssue.number} {epic.progress.nextIssue.title}
              </Link>
            ) : epic.progress.readyToMarkDone ? (
              <div className="mt-2 text-sm font-medium text-green-700">Ready to mark done</div>
            ) : (
              <div className="mt-2 text-sm text-muted-foreground">No linked issues yet</div>
            )}
          </div>
          <div className="rounded-lg bg-muted p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Current Activity</div>
            <div className="mt-2 text-sm text-foreground/80">
              {epic.progress.blockedIssues.length} blocked, {epic.progress.activeIssues.length} active
            </div>
          </div>
        </div>
      </Card>

      <Card className="p-6">
        <h2 className="text-lg font-semibold text-foreground">Linked Issues</h2>
        <form onSubmit={handleAddIssue} className="mt-4 flex flex-col gap-3 sm:flex-row">
          <Select
            value={selectedIssueId}
            onValueChange={(value) => setSelectedIssueId(value ?? '')}
          >
            <SelectTrigger className="flex-1">
              <SelectValue placeholder="Select an issue to link" />
            </SelectTrigger>
            <SelectContent>
              {availableIssues.map(issue => (
                <SelectItem key={issue.id} value={issue.id}>
                  #{issue.number} {issue.title}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            type="submit"
            disabled={!selectedIssueId || addEpicIssue.isPending}
          >
            {addEpicIssue.isPending ? 'Adding...' : 'Add Issue'}
          </Button>
        </form>

        {addEpicIssue.isError && (
          <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700">
            {formatAddIssueError(addEpicIssue.error)}
          </div>
        )}

        <div className="mt-6 space-y-3">
          {epic.linkedIssues.length === 0 ? (
            <div className="rounded-lg border border-dashed p-6 text-sm text-muted-foreground">
              No linked issues yet.
            </div>
          ) : (
            epic.linkedIssues.map(issue => (
              <LinkedIssueRow
                key={issue.id}
                issue={issue}
                onRemove={(issueId) => removeEpicIssue.mutate({ epicId: epic.id, issueId })}
                disabled={removeEpicIssue.isPending}
              />
            ))
          )}
        </div>

        {removeEpicIssue.isError && (
          <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700">
            {removeEpicIssue.error?.message || 'Failed to remove issue'}
          </div>
        )}
      </Card>
    </div>
  )
}
