import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useProject, useProjectPath } from '../../../entities/project'
import { IssueStatus, type Issue } from '../../../entities/issue'
import { useIssues } from '../../../entities/issue'
import {
  useAddEpicIssue,
  useCloseEpic,
  useEpic,
  useMarkEpicDone,
  usePauseEpic,
  useRemoveEpicIssue,
  useResumeEpic,
  useStartEpic,
  useStartIssue,
} from '../../../entities/epic'
import { canInlineStartRow, EpicStatus, type EpicProgressIssue, type LinkedIssue } from '../../../entities/epic'
import { EditEpicDialog } from '../../../features/edit-epic'
import { ApiError } from '../../../shared/api/client'
import { Button } from '@/shared/ui/components/button'
import { Card } from '@/shared/ui/components/card'
import { Badge } from '@/shared/ui/components/badge'
import { Input } from '@/shared/ui/components/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { MarkdownReader } from '@/shared/ui'
import { DependencyGraphWidget, type Renderability } from '../../../widgets/epic-dependency-graph'

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
    [EpicStatus.Idle]: 'bg-green-100 text-green-700',
    [EpicStatus.Running]: 'bg-emerald-100 text-emerald-700',
    [EpicStatus.Paused]: 'bg-amber-100 text-amber-700',
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

function LinkedIssueRow({
  issue,
  onRemove,
  onStart,
  disabled,
  startPending,
  hasInProgress,
}: {
  issue: LinkedIssue
  onRemove: (issueId: string) => void
  onStart: (issueNumber: number) => void
  disabled: boolean
  startPending: boolean
  hasInProgress: boolean
}) {
  const toProjectPath = useProjectPath()
  const showStart = canInlineStartRow(issue, hasInProgress)

  return (
    <Card className="flex items-center justify-between gap-4 p-4">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          <Link to={toProjectPath(`/issues/${issue.number}`)} className="font-medium text-blue-600 hover:text-blue-700 hover:underline">
            #{issue.number}
          </Link>
          <span className={`rounded px-2 py-0.5 text-xs font-medium ${issueStatusTone(issue.health)}`}>{issue.health}</span>
          <Badge variant="secondary">{toTitleCase(issue.status)}</Badge>
          {issue.priority && <Badge variant="secondary">{issue.priority.toUpperCase()}</Badge>}
        </div>
        <div className="mt-1 truncate text-sm font-medium text-foreground">{issue.title}</div>
      </div>
      <div className="flex shrink-0 flex-wrap gap-2">
        {showStart && (
          <Button
            type="button"
            onClick={() => onStart(issue.number)}
            disabled={startPending}
            data-testid="linked-issue-start"
          >
            {startPending ? 'Starting...' : 'Start'}
          </Button>
        )}
        <Button
          type="button"
          variant="outline"
          onClick={() => onRemove(issue.id)}
          disabled={disabled}
        >
          Remove
        </Button>
      </div>
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

function getCandidateUnavailableReason(issue: Issue): string | null {
  if (issue.status === IssueStatus.Done) return 'Closed'
  if (issue.archivedAt) return 'Archived'
  if (issue.blocker?.kind === 'draft') return 'Still a draft'
  if (issue.blocker?.kind === 'waiting-for') {
    return `Waiting for #${issue.blocker.issue.number}`
  }
  return null
}

function isCandidateSelectable(issue: Issue): boolean {
  return getCandidateUnavailableReason(issue) === null
}

interface CurrentActivityEntryProps {
  issue: EpicProgressIssue
  toProjectPath: (path: string) => string
}

function CurrentActivityEntry({ issue, toProjectPath }: CurrentActivityEntryProps) {
  const tone = issueStatusTone(issue.health)
  const healthLabel = toTitleCase(issue.health)
  return (
    <Link
      to={toProjectPath(`/issues/${issue.number}`)}
      data-testid="current-activity-entry"
      data-health={issue.health}
      className="flex items-baseline gap-2 rounded px-2 py-1 -mx-2 text-sm text-foreground/80 hover:bg-background"
    >
      <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${tone}`}>
        {healthLabel}
      </span>
      <span className="min-w-0 flex-1 truncate">
        <span className="font-medium text-blue-600">#{issue.number}</span>
        <span className="ml-1.5 text-muted-foreground">{issue.title}</span>
      </span>
    </Link>
  )
}

function CurrentActivityList({
  active,
  blocked,
}: {
  active: EpicProgressIssue[]
  blocked: EpicProgressIssue[]
}) {
  const toProjectPath = useProjectPath()
  const total = active.length + blocked.length
  if (total === 0) {
    return (
      <div
        className="mt-2 text-sm text-muted-foreground"
        data-testid="current-activity-empty"
      >
        No current activity.
      </div>
    )
  }

  return (
    <ul
      className="mt-2 flex flex-col gap-1"
      data-testid="current-activity-list"
      data-active-count={active.length}
      data-blocked-count={blocked.length}
    >
      {[...blocked, ...active].map(issue => (
        <li key={issue.id}>
          <CurrentActivityEntry issue={issue} toProjectPath={toProjectPath} />
        </li>
      ))}
    </ul>
  )
}

interface EpicIssueSelectorProps {
  candidates: Issue[]
  value: string | null
  onChange: (issueId: string | null) => void
  hasSelectableCandidate: boolean
  disabled?: boolean
}

function EpicIssueSelector({ candidates, value, onChange, hasSelectableCandidate, disabled }: EpicIssueSelectorProps) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const searchRef = useRef<HTMLInputElement>(null)
  const selected = candidates.find(candidate => candidate.id === value) ?? null
  const isDisabled = disabled || (!hasSelectableCandidate && !selected)

  useEffect(() => {
    if (!open) {
      setSearch('')
    }
  }, [open])

  useEffect(() => {
    if (open) {
      requestAnimationFrame(() => searchRef.current?.focus())
    }
  }, [open])

  const filteredCandidates = useMemo(() => {
    const query = search.trim().toLowerCase()
    if (!query) return candidates
    return candidates.filter(candidate => {
      if (candidate.title.toLowerCase().includes(query)) return true
      return `#${candidate.number}`.toLowerCase().includes(query)
        || String(candidate.number).includes(query)
    })
  }, [candidates, search])

  const triggerLabel = selected
    ? `#${selected.number} ${selected.title}`
    : hasSelectableCandidate
      ? 'Select an issue to link'
      : candidates.length === 0
        ? 'No issues available'
        : 'No selectable issues'

  function handleSelect(candidate: Issue) {
    onChange(candidate.id)
    setOpen(false)
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger
        render={
          <Button
            type="button"
            variant="outline"
            data-testid="epic-issue-selector-trigger"
            role="combobox"
            aria-haspopup="listbox"
            aria-expanded={open}
            disabled={isDisabled}
            className={`flex-1 justify-between gap-1.5 min-h-[40px] ${
              open
                ? 'border-blue-500 bg-blue-50 text-blue-700'
                : selected
                  ? 'text-foreground'
                  : 'text-muted-foreground'
            }`}
          />
        }
      >
        <span className="truncate">{triggerLabel}</span>
        <svg className="h-4 w-4 shrink-0 text-muted-foreground" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
        </svg>
      </PopoverTrigger>
      <PopoverContent className="w-[var(--radix-popover-trigger-width)] p-0" align="start">
        <div className="p-2">
          <div className="relative">
            <div className="absolute left-3 top-1/2 -translate-y-1/2">
              <svg className="h-4 w-4 text-muted-foreground" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z" clipRule="evenodd" />
              </svg>
            </div>
            <Input
              ref={searchRef}
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search by number or title..."
              data-testid="epic-issue-search"
              className="pl-9"
            />
          </div>
        </div>
        <div className="max-h-64 overflow-y-auto border-t" role="listbox" data-testid="epic-issue-listbox">
          {candidates.length === 0 ? (
            <div className="px-3 py-6 text-center text-sm text-muted-foreground">
              No issues available
            </div>
          ) : filteredCandidates.length === 0 ? (
            <div className="px-3 py-6 text-center text-sm text-muted-foreground">
              No issues match &quot;{search}&quot;
            </div>
          ) : (
            filteredCandidates.map(candidate => {
              const reason = getCandidateUnavailableReason(candidate)
              const selectable = reason === null
              return (
                <button
                  key={candidate.id}
                  type="button"
                  role="option"
                  aria-selected={candidate.id === value}
                  aria-disabled={!selectable}
                  disabled={!selectable}
                  data-testid="epic-issue-option"
                  data-issue-id={candidate.id}
                  data-unavailable={selectable ? undefined : 'true'}
                  onClick={() => handleSelect(candidate)}
                  className={`flex w-full flex-col items-start gap-0.5 px-3 py-2 text-left text-sm transition-colors ${
                    candidate.id === value
                      ? 'bg-blue-50 text-blue-700'
                      : selectable
                        ? 'text-foreground hover:bg-muted'
                        : 'cursor-not-allowed bg-muted/40 text-muted-foreground'
                  }`}
                >
                  <span className="font-medium">#{candidate.number} {candidate.title}</span>
                  {!selectable && (
                    <span
                      data-testid="epic-issue-option-reason"
                      className="text-xs text-muted-foreground"
                    >
                      {reason}
                    </span>
                  )}
                </button>
              )
            })
          )}
        </div>
      </PopoverContent>
    </Popover>
  )
}

export function EpicDetailPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { projectId } = useProject()
  const { data: epic, isLoading } = useEpic(id)
  const { data: issues } = useIssues(projectId ? { projectId } : undefined)
  const addEpicIssue = useAddEpicIssue()
  const removeEpicIssue = useRemoveEpicIssue()
  const startIssue = useStartIssue()
  const [pendingStartIssueNumber, setPendingStartIssueNumber] = useState<number | null>(null)
  const markEpicDone = useMarkEpicDone()
  const closeEpic = useCloseEpic()
  const pauseEpic = usePauseEpic()
  const resumeEpic = useResumeEpic()
  const startEpic = useStartEpic()
  const [selectedIssueId, setSelectedIssueId] = useState<string | null>(null)
  const [editDialogOpen, setEditDialogOpen] = useState(false)
  const [closeConfirmOpen, setCloseConfirmOpen] = useState(false)
  const [pauseConfirmOpen, setPauseConfirmOpen] = useState(false)
  const [pauseReason, setPauseReason] = useState('')
  const [linkedIssuesView, setLinkedIssuesView] = useState<'list' | 'graph'>('list')
  const [graphRenderable, setGraphRenderable] = useState<{ renderable: boolean; reason: Renderability | null }>({
    renderable: false,
    reason: null,
  })

  const handleGraphRenderabilityChange = useCallback((state: { renderable: boolean; reason: Renderability | null }) => {
    setGraphRenderable(state)
  }, [])

  const availableIssues = useMemo(() => {
    if (!issues || !epic) return []
    const linkedIds = new Set(epic.linkedIssues.map(issue => issue.id))
    return issues.filter(issue => !linkedIds.has(issue.id))
  }, [epic, issues])

  const hasSelectableCandidate = useMemo(
    () => availableIssues.some(isCandidateSelectable),
    [availableIssues],
  )

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
            onClick={() => navigate(toProjectPath('/epics'))}
            className="mt-4"
          >
            Back to Epics
          </Button>
        </Card>
      </div>
    )
  }

  const progressPercent = epic.progress.totalIssueCount > 0
    ? (epic.progress.deliveredCount / epic.progress.totalIssueCount) * 100
    : 0
  const epicId = epic.id
  const submitDisabled = !selectedIssueId || addEpicIssue.isPending
  const unfinishedCount = Math.max(epic.progress.totalIssueCount - epic.progress.deliveredCount, 0)
  const isPaused = epic.status === EpicStatus.Paused
  const isIdle = epic.status === EpicStatus.Idle
  const isRunning = epic.status === EpicStatus.Running
  const isSelfDriving = isIdle || isRunning
  const markDoneBlocked = isPaused || (isSelfDriving && !epic.progress.readyToMarkDone)
  const markDoneTooltip = isPaused
    ? 'Resume this Epic before marking it done'
    : markDoneBlocked
      ? unfinishedCount === 1
        ? '1 linked issue remains unfinished'
        : `${unfinishedCount} linked issues remain unfinished`
      : undefined
  const isClosed = epic.status === EpicStatus.Closed
  const isDone = epic.status === EpicStatus.Done
  const linkedIssueCount = epic.linkedIssues.length
  const graphAvailable = linkedIssueCount >= 2
  const graphSelected = graphAvailable && linkedIssuesView === 'graph'
  const showList = !graphSelected || graphRenderable.reason === 'cyclic' || graphRenderable.reason === 'empty'

  function handleConfirmClose() {
    closeEpic.mutate(epicId, {
      onSettled: () => setCloseConfirmOpen(false),
    })
  }

  function handleConfirmPause() {
    pauseEpic.mutate(
      { id: epicId, reason: pauseReason.trim() || null },
      {
        onSettled: () => {
          setPauseConfirmOpen(false)
          setPauseReason('')
        },
      },
    )
  }

  function handleAddIssue(event: FormEvent) {
    event.preventDefault()
    if (!selectedIssueId) return
    addEpicIssue.mutate(
      { epicId, issueId: selectedIssueId },
      { onSuccess: () => setSelectedIssueId(null) },
    )
  }

  function handleStartIssue(issueNumber: number) {
    setPendingStartIssueNumber(issueNumber)
    startIssue.mutate(issueNumber, {
      onSettled: () => setPendingStartIssueNumber(null),
    })
  }

  const hasInProgress = epic.linkedIssues.some(i => i.status === IssueStatus.InProgress)

  return (
    <div className="mx-auto w-full min-w-0 max-w-4xl space-y-6 p-6">
      <div>
        <Button
          type="button"
          variant="link"
          onClick={() => navigate(toProjectPath('/epics'))}
          className="px-0"
        >
          Back to Epics
        </Button>
      </div>

      <Card className="p-6">
        <div className="flex flex-col gap-4 md:flex-row md:flex-wrap md:items-start md:justify-between">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
              <span data-testid="epic-number">
                {epic.number != null ? `#${epic.number}` : `#${epic.id.slice(0, 8)}`}
              </span>
              <StatusBadge status={epic.status} />
              <PriorityBadge priority={epic.priority} />
              {epic.pauseReason && (
                <span
                  data-testid="pause-reason"
                  className="rounded bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700"
                >
                  {epic.pauseReason}
                </span>
              )}
            </div>
            <h1 className="mt-2 text-2xl font-bold text-foreground [overflow-wrap:anywhere]">{epic.title}</h1>
            {epic.description && (
              <div
                className="mt-3 text-sm leading-6 text-foreground/80 [overflow-wrap:anywhere]"
                data-testid="epic-description"
              >
                <MarkdownReader content={epic.description} baseHeadingLevel={3} />
              </div>
            )}
          </div>
          <div className="flex flex-wrap justify-start gap-2 md:justify-end">
            <Button
              type="button"
              variant="outline"
              onClick={() => setEditDialogOpen(true)}
              data-testid="edit-epic-button"
            >
              Edit
            </Button>
            {isIdle && (
              <Button
                type="button"
                onClick={() => startEpic.mutate(epicId)}
                disabled={startEpic.isPending}
                data-testid="start-epic-trigger"
              >
                {startEpic.isPending ? 'Starting...' : 'Start Epic'}
              </Button>
            )}
            {isRunning && (
              <Button
                type="button"
                variant="outline"
                onClick={() => setPauseConfirmOpen(true)}
                disabled={pauseEpic.isPending}
                data-testid="pause-epic-trigger"
              >
                {pauseEpic.isPending ? 'Pausing...' : 'Pause'}
              </Button>
            )}
            {isPaused && (
              <Button
                type="button"
                variant="outline"
                onClick={() => resumeEpic.mutate(epicId)}
                disabled={resumeEpic.isPending}
                data-testid="resume-epic-trigger"
              >
                {resumeEpic.isPending ? 'Resuming...' : 'Resume'}
              </Button>
            )}
            {!isDone && !isClosed && (
              <Button
                type="button"
                onClick={() => markEpicDone.mutate(epic.id)}
                disabled={markEpicDone.isPending || markDoneBlocked}
                title={markDoneTooltip}
                data-testid="mark-epic-done"
              >
                {markEpicDone.isPending ? 'Marking...' : 'Mark Done'}
              </Button>
            )}
            {!isDone && !isClosed && (
              <Button
                type="button"
                variant="outline"
                onClick={() => setCloseConfirmOpen(true)}
                disabled={closeEpic.isPending}
                data-testid="close-epic-trigger"
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
              {epic.progress.deliveredCount} / {epic.progress.totalIssueCount}
            </div>
            <div className="mt-3 h-2 rounded-full bg-background">
              <div className="h-2 rounded-full bg-blue-600" style={{ width: `${progressPercent}%` }} />
            </div>
          </div>
          <div className="rounded-lg bg-muted p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Next Issue</div>
            {epic.progress.nextIssue ? (
              <Link
                to={toProjectPath(`/issues/${epic.progress.nextIssue.number}`)}
                data-testid="next-issue"
                className="mt-2 block text-sm font-medium text-blue-600 hover:text-blue-700 hover:underline"
              >
                #{epic.progress.nextIssue.number} {epic.progress.nextIssue.title}
              </Link>
            ) : epic.progress.readyToMarkDone ? (
              <div className="mt-2 text-sm font-medium text-green-700">Ready to mark done</div>
            ) : epic.progress.nextIssueReason ? (
              <div
                className="mt-2 text-sm text-foreground/80"
                data-testid="next-issue-reason"
              >
                {epic.progress.nextIssueReason}
              </div>
            ) : (
              <div className="mt-2 text-sm text-muted-foreground">No linked issues yet</div>
            )}
          </div>
          <div className="rounded-lg bg-muted p-4">
            <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Current Activity</div>
            <CurrentActivityList
              active={epic.progress.activeIssues}
              blocked={epic.progress.blockedIssues}
            />
          </div>
        </div>
      </Card>

      <Card className="p-6">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-lg font-semibold text-foreground">Linked Issues</h2>
          {graphAvailable && (
            <div
              role="tablist"
              aria-label="Linked Issues view"
              data-testid="linked-issues-view-toggle"
              className="inline-flex items-center rounded-md border bg-muted/40 p-0.5 text-sm"
            >
              <button
                type="button"
                role="tab"
                aria-selected={linkedIssuesView === 'list'}
                data-testid="linked-issues-view-list"
                data-view="list"
                onClick={() => setLinkedIssuesView('list')}
                className={`rounded px-3 py-1 text-xs font-medium transition-colors ${
                  linkedIssuesView === 'list'
                    ? 'bg-background text-foreground shadow-sm'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                List
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={linkedIssuesView === 'graph'}
                data-testid="linked-issues-view-graph"
                data-view="graph"
                onClick={() => setLinkedIssuesView('graph')}
                className={`rounded px-3 py-1 text-xs font-medium transition-colors ${
                  linkedIssuesView === 'graph'
                    ? 'bg-background text-foreground shadow-sm'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                Graph
              </button>
            </div>
          )}
        </div>
        <form onSubmit={handleAddIssue} className="mt-4 flex flex-col gap-3 sm:flex-row">
          <EpicIssueSelector
            candidates={availableIssues}
            value={selectedIssueId}
            onChange={setSelectedIssueId}
            hasSelectableCandidate={hasSelectableCandidate}
          />
          <Button
            type="submit"
            disabled={submitDisabled}
            data-testid="add-issue-submit"
          >
            {addEpicIssue.isPending ? 'Adding...' : 'Add Issue'}
          </Button>
        </form>

        {addEpicIssue.isError && (
          <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700">
            {formatAddIssueError(addEpicIssue.error)}
          </div>
        )}

        {graphSelected ? (
          <div
            className="mt-6"
            data-testid="linked-issues-graph-region"
            data-renderability={graphRenderable.renderable ? 'renderable' : (graphRenderable.reason ?? 'loading')}
          >
            <DependencyGraphWidget
              linkedIssues={epic.linkedIssues}
              onRenderabilityChange={handleGraphRenderabilityChange}
            />
          </div>
        ) : null}

        {showList ? (
          <div
            className="mt-6 space-y-3"
            data-testid="linked-issues-list-region"
            data-fallback-for={graphSelected ? graphRenderable.reason ?? undefined : undefined}
          >
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
                  onStart={handleStartIssue}
                  disabled={removeEpicIssue.isPending}
                  startPending={pendingStartIssueNumber === issue.number}
                  hasInProgress={hasInProgress}
                />
              ))
            )}
          </div>
        ) : null}

        {removeEpicIssue.isError && (
          <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700">
            {removeEpicIssue.error?.message || 'Failed to remove issue'}
          </div>
        )}
      </Card>

      <EditEpicDialog
        open={editDialogOpen}
        onClose={() => setEditDialogOpen(false)}
        epic={epic}
      />

      <Dialog open={closeConfirmOpen} onOpenChange={(v) => !v && setCloseConfirmOpen(false)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Close Epic?</DialogTitle>
            <DialogDescription>
              {linkedIssueCount === 0
                ? 'This Epic has no linked issues. Closing it will mark the Epic as closed.'
                : linkedIssueCount === 1
                  ? 'Closing this Epic will unlink 1 associated issue. Issue workflow state will not change.'
                  : `Closing this Epic will unlink ${linkedIssueCount} associated issues. Issue workflow state will not change.`}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => setCloseConfirmOpen(false)}
              disabled={closeEpic.isPending}
              data-testid="close-epic-cancel"
            >
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              onClick={handleConfirmClose}
              disabled={closeEpic.isPending}
              data-testid="close-epic-confirm"
            >
              {closeEpic.isPending ? 'Closing...' : 'Close Epic'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={pauseConfirmOpen} onOpenChange={(v) => !v && setPauseConfirmOpen(false)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Pause Epic?</DialogTitle>
            <DialogDescription>
              Pausing this Epic will keep all linked issues connected. The Epic will be hidden from the active view until resumed.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-2">
            <label htmlFor="pause-reason-input" className="text-sm font-medium text-foreground">
              Reason (optional)
            </label>
            <Input
              id="pause-reason-input"
              value={pauseReason}
              onChange={(e) => setPauseReason(e.target.value)}
              placeholder="Why are you pausing this Epic?"
              data-testid="pause-reason-input"
              disabled={pauseEpic.isPending}
            />
          </div>
          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => {
                setPauseConfirmOpen(false)
                setPauseReason('')
              }}
              disabled={pauseEpic.isPending}
              data-testid="pause-epic-cancel"
            >
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              onClick={handleConfirmPause}
              disabled={pauseEpic.isPending}
              data-testid="pause-epic-confirm"
            >
              {pauseEpic.isPending ? 'Pausing...' : 'Pause Epic'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
