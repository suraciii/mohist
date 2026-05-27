import { useMemo, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useProject } from '../../../entities/project/model/ProjectContext'
import { useIssues } from '../../../entities/issue/api/queries'
import {
  useAddEpicIssue,
  useCloseEpic,
  useEpic,
  useMarkEpicDone,
  useRemoveEpicIssue,
} from '../../../entities/epic/api/queries'
import { EpicStatus, IssueStatus, type LinkedIssue } from '../../../shared/api/types'
import { ApiError } from '../../../shared/api/client'

function PriorityBadge({ priority }: { priority: string }) {
  const colors: Record<string, string> = {
    p0: 'bg-red-100 text-red-700',
    p1: 'bg-orange-100 text-orange-700',
    p2: 'bg-yellow-100 text-yellow-700',
    p3: 'bg-blue-100 text-blue-700',
    p4: 'bg-gray-100 text-gray-700',
  }

  return (
    <span className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-medium ${colors[priority] || 'bg-gray-100 text-gray-700'}`}>
      {priority.toUpperCase()}
    </span>
  )
}

function StatusBadge({ status }: { status: EpicStatus }) {
  const colors: Record<EpicStatus, string> = {
    [EpicStatus.Active]: 'bg-green-100 text-green-700',
    [EpicStatus.Done]: 'bg-blue-100 text-blue-700',
    [EpicStatus.Closed]: 'bg-gray-100 text-gray-700',
  }

  return (
    <span className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-medium ${colors[status]}`}>
      {status}
    </span>
  )
}

function toTitleCase(value: string) {
  return value.charAt(0).toUpperCase() + value.slice(1)
}

function issueStatusTone(status: IssueStatus) {
  switch (status) {
    case 'blocked':
      return 'bg-red-50 text-red-700'
    case IssueStatus.Done:
    case IssueStatus.Cancelled:
      return 'bg-green-50 text-green-700'
    default:
      return 'bg-gray-50 text-gray-700'
  }
}

function LinkedIssueRow({ issue, onRemove, disabled }: { issue: LinkedIssue; onRemove: (issueId: string) => void; disabled: boolean }) {
  return (
    <div className="flex items-center justify-between gap-4 rounded-lg border border-gray-200 bg-white p-4">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2 text-sm text-gray-500">
          <Link to={`/issue/${issue.number}`} className="font-medium text-blue-600 hover:text-blue-700 hover:underline">
            #{issue.number}
          </Link>
          <span className={`rounded px-2 py-0.5 text-xs font-medium ${issueStatusTone(issue.status)}`}>{issue.status}</span>
          <span className="rounded bg-gray-100 px-2 py-0.5 text-xs text-gray-600">{toTitleCase(issue.stage)}</span>
          {issue.priority && <span className="rounded bg-gray-100 px-2 py-0.5 text-xs text-gray-600">{issue.priority.toUpperCase()}</span>}
        </div>
        <div className="mt-1 truncate text-sm font-medium text-gray-900">{issue.title}</div>
      </div>
      <button
        type="button"
        onClick={() => onRemove(issue.id)}
        disabled={disabled}
        className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
      >
        Remove
      </button>
    </div>
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
    return <div className="flex items-center justify-center py-12 text-gray-400">Loading...</div>
  }

  if (!epic) {
    return (
      <div className="mx-auto max-w-4xl p-6">
        <div className="rounded-lg border border-gray-200 bg-white p-8 text-center">
          <div className="text-lg font-medium text-gray-900">Epic not found</div>
          <button
            type="button"
            onClick={() => navigate('/epics')}
            className="mt-4 text-sm text-blue-600 hover:text-blue-700 hover:underline"
          >
            Back to Epics
          </button>
        </div>
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
        <button
          type="button"
          onClick={() => navigate('/epics')}
          className="text-sm text-blue-600 hover:text-blue-700 hover:underline"
        >
          Back to Epics
        </button>
      </div>

      <div className="rounded-lg border border-gray-200 bg-white p-6">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2 text-sm text-gray-500">
              <span>#{epic.id.slice(0, 8)}</span>
              <StatusBadge status={epic.status} />
              <PriorityBadge priority={epic.priority} />
            </div>
            <h1 className="mt-2 text-2xl font-bold text-gray-900">{epic.title}</h1>
            <p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-gray-700">{epic.description}</p>
          </div>
          <div className="flex flex-wrap gap-2">
            {epic.status === EpicStatus.Active && (
              <button
                type="button"
                onClick={() => markEpicDone.mutate(epic.id)}
                disabled={markEpicDone.isPending}
                className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
              >
                {markEpicDone.isPending ? 'Marking...' : 'Mark Done'}
              </button>
            )}
            {epic.status !== EpicStatus.Closed && (
              <button
                type="button"
                onClick={() => closeEpic.mutate(epic.id)}
                disabled={closeEpic.isPending}
                className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
              >
                {closeEpic.isPending ? 'Closing...' : 'Close Epic'}
              </button>
            )}
          </div>
        </div>

        <div className="mt-6 grid gap-4 md:grid-cols-3">
          <div className="rounded-lg bg-gray-50 p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Progress</div>
            <div className="mt-2 text-2xl font-semibold text-gray-900">
              {epic.progress.completedCount} / {epic.progress.totalIssueCount}
            </div>
            <div className="mt-3 h-2 rounded-full bg-gray-200">
              <div className="h-2 rounded-full bg-blue-600" style={{ width: `${progressPercent}%` }} />
            </div>
          </div>
          <div className="rounded-lg bg-gray-50 p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Next Issue</div>
            {epic.progress.nextIssue ? (
              <Link to={`/issue/${epic.progress.nextIssue.number}`} className="mt-2 block text-sm font-medium text-blue-600 hover:text-blue-700 hover:underline">
                #{epic.progress.nextIssue.number} {epic.progress.nextIssue.title}
              </Link>
            ) : epic.progress.readyToMarkDone ? (
              <div className="mt-2 text-sm font-medium text-green-700">Ready to mark done</div>
            ) : (
              <div className="mt-2 text-sm text-gray-500">No linked issues yet</div>
            )}
          </div>
          <div className="rounded-lg bg-gray-50 p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-gray-500">Current Activity</div>
            <div className="mt-2 text-sm text-gray-700">
              {epic.progress.blockedIssues.length} blocked, {epic.progress.activeIssues.length} active
            </div>
          </div>
        </div>
      </div>

      <div className="rounded-lg border border-gray-200 bg-white p-6">
        <h2 className="text-lg font-semibold text-gray-900">Linked Issues</h2>
        <form onSubmit={handleAddIssue} className="mt-4 flex flex-col gap-3 sm:flex-row">
          <select
            value={selectedIssueId}
            onChange={(event) => setSelectedIssueId(event.target.value)}
            className="flex-1 rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          >
            <option value="">Select an issue to link</option>
            {availableIssues.map(issue => (
              <option key={issue.id} value={issue.id}>
                #{issue.number} {issue.title}
              </option>
            ))}
          </select>
          <button
            type="submit"
            disabled={!selectedIssueId || addEpicIssue.isPending}
            className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {addEpicIssue.isPending ? 'Adding...' : 'Add Issue'}
          </button>
        </form>

        {addEpicIssue.isError && (
          <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700">
            {formatAddIssueError(addEpicIssue.error)}
          </div>
        )}

        <div className="mt-6 space-y-3">
          {epic.linkedIssues.length === 0 ? (
            <div className="rounded-lg border border-dashed border-gray-200 p-6 text-sm text-gray-500">
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
      </div>
    </div>
  )
}
