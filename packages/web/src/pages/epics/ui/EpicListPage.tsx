import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useEpics, useStartIssue, EpicStatus, type EpicWithProgress } from '../../../entities/epic'
import { EpicCreateDialog } from '../../../features/create-epic'
import { Button } from '@/shared/ui/components/button'
import { Badge } from '@/shared/ui/components/badge'
import { Card } from '@/shared/ui/components/card'
import { useProjectPath } from '../../../entities/project'
import { groupActiveEpics, type ActiveEpicGroups } from './groupActiveEpics'

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
  const labels: Record<EpicStatus, string> = {
    [EpicStatus.Idle]: 'Idle',
    [EpicStatus.Running]: 'Running',
    [EpicStatus.Paused]: 'Paused',
    [EpicStatus.Done]: 'Done',
    [EpicStatus.Closed]: 'Closed',
  }
  return (
    <Badge className={colors[status]}>
      {labels[status]}
    </Badge>
  )
}

type ActiveGroupKey = 'running' | 'readyToStart' | 'waitingBlocked' | 'idleEmpty'

interface ActiveGroupDescriptor {
  key: ActiveGroupKey
  title: string
  testIdPrefix: string
  epics: EpicWithProgress[]
}

function EpicProgressBody({ progress }: { progress: EpicWithProgress['progress'] }) {
  const next = progress.nextIssue
  if (next) {
    return (
      <span
        className="text-muted-foreground break-words"
        data-testid="epic-card-next"
      >
        Next: <span className="text-foreground/80 font-medium">#{next.number}</span>
        <span className="text-muted-foreground ml-1 break-words">{next.title}</span>
      </span>
    )
  }
  if (progress.nextIssueReason) {
    return (
      <span
        className="text-muted-foreground break-words"
        data-testid="epic-card-next"
      >
        {progress.nextIssueReason}
      </span>
    )
  }
  if (progress.readyToMarkDone) {
    return (
      <span className="text-green-600 font-medium" data-testid="epic-card-ready">
        Ready to mark done
      </span>
    )
  }
  return (
    <span className="text-muted-foreground" data-testid="epic-card-empty">
      No linked issues
    </span>
  )
}

function StartNextIssueButton({
  issueNumber,
  onStartNextIssue,
  startPending,
}: {
  issueNumber: number
  onStartNextIssue: (issueNumber: number) => void
  startPending?: boolean
}) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <Button
        type="button"
        size="sm"
        variant="outline"
        onClick={(e) => {
          e.stopPropagation()
          onStartNextIssue(issueNumber)
        }}
        disabled={startPending}
        data-testid="epic-card-start"
        className="self-start"
      >
        {startPending ? 'Starting...' : 'Start next issue'}
      </Button>
    </div>
  )
}

function EpicCardBody({
  epic,
  group,
  onStartNextIssue,
  startPending,
}: {
  epic: EpicWithProgress
  group: CardGroup
  onStartNextIssue?: (issueNumber: number) => void
  startPending?: boolean
}) {
  const { progress, status } = epic

  if (status === EpicStatus.Done) {
    return <span className="text-blue-700 font-medium">Completed</span>
  }
  if (status === EpicStatus.Closed) {
    return <span className="text-muted-foreground font-medium">Closed</span>
  }

  if (group === 'running') {
    const inProgress = progress.activeIssues[0]
    return (
      <div className="flex flex-col gap-0.5" data-testid="epic-card-running">
        {inProgress && (
          <span
            className="text-muted-foreground break-words"
            data-testid="epic-card-in-progress"
          >
            In progress:{' '}
            <span className="text-foreground/80 font-medium">#{inProgress.number}</span>
            <span className="text-muted-foreground ml-1 break-words">{inProgress.title}</span>
          </span>
        )}
      </div>
    )
  }

  if (group === 'readyToStart') {
    const next = progress.nextIssue
    if (!next) return null
    return (
      <div className="flex flex-col gap-1" data-testid="epic-card-ready">
        <span
          className="text-muted-foreground break-words"
          data-testid="epic-card-next"
        >
          Next: <span className="text-foreground/80 font-medium">#{next.number}</span>
          <span className="text-muted-foreground ml-1 break-words">{next.title}</span>
        </span>
        {onStartNextIssue && (
          <StartNextIssueButton
            issueNumber={next.number}
            onStartNextIssue={onStartNextIssue}
            startPending={startPending}
          />
        )}
      </div>
    )
  }

  if (group === 'waitingBlocked') {
    return <EpicProgressBody progress={progress} />
  }

  if (group === 'idleEmpty') {
    return <EpicProgressBody progress={progress} />
  }

  if (group === 'paused') {
    const next = progress.nextIssue
    if (next && onStartNextIssue) {
      return (
        <div className="flex flex-col gap-1" data-testid="epic-card-paused-next">
          <EpicProgressBody progress={progress} />
          <StartNextIssueButton
            issueNumber={next.number}
            onStartNextIssue={onStartNextIssue}
            startPending={startPending}
          />
        </div>
      )
    }
    return <EpicProgressBody progress={progress} />
  }

  return null
}

function EpicCard({
  epic,
  group,
  onStartNextIssue,
  startPending,
}: {
  epic: EpicWithProgress
  group: CardGroup
  onStartNextIssue?: (issueNumber: number) => void
  startPending?: boolean
}) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { progress } = epic
  const isPaused = epic.status === EpicStatus.Paused

  return (
    <Card
      className={`p-4 transition-colors cursor-pointer ${
        isPaused
          ? 'opacity-60 hover:opacity-80'
          : 'hover:border-muted-foreground/30'
      }`}
      onClick={() => navigate(toProjectPath(`/epics/${epic.number}`))}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex flex-wrap items-center gap-2 mb-1">
            <span
              className="text-sm font-medium text-muted-foreground"
              data-testid="epic-number"
            >
              #{epic.number}
            </span>
            <StatusBadge status={epic.status} />
            <PriorityBadge priority={epic.priority} />
          </div>
          <h3 className="text-base font-semibold text-foreground break-words">{epic.title}</h3>
        </div>
      </div>

      <div className="mt-3">
        <div className="flex flex-wrap items-center justify-between gap-2 text-sm mb-1">
          <span className="text-muted-foreground">Progress</span>
          <span className="font-medium text-foreground">
            {progress.deliveredCount} / {progress.totalIssueCount} completed
          </span>
        </div>
        <div
          className="w-full bg-muted rounded-full h-1.5"
          data-testid="epic-progress-bar"
        >
          <div
            className="bg-blue-600 h-1.5 rounded-full transition-all"
            style={{
              width: progress.totalIssueCount > 0
                ? `${(progress.deliveredCount / progress.totalIssueCount) * 100}%`
                : '0%'
            }}
          />
        </div>
      </div>

      <div className="mt-3 flex items-center justify-between">
        <div className="text-sm min-w-0 flex-1 break-words">
          <EpicCardBody
            epic={epic}
            group={group}
            onStartNextIssue={group === 'readyToStart' || group === 'paused' ? onStartNextIssue : undefined}
            startPending={group === 'readyToStart' || group === 'paused' ? startPending : undefined}
          />
        </div>
      </div>
    </Card>
  )
}

type CardGroup = ActiveGroupKey | 'done' | 'closed' | 'paused'

interface EpicSectionProps {
  title: string
  epics: EpicWithProgress[]
  defaultExpanded: boolean
  testIdPrefix: string
  group?: CardGroup
  onStartNextIssue?: (issueNumber: number) => void
  pendingStartIssueNumber?: number | null
}

function EpicSection({
  title,
  epics,
  defaultExpanded,
  testIdPrefix,
  group,
  onStartNextIssue,
  pendingStartIssueNumber,
}: EpicSectionProps) {
  const [expanded, setExpanded] = useState(defaultExpanded)

  return (
    <section data-testid={testIdPrefix}>
      <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
        <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">
          {title} ({epics.length})
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
          {epics.map(epic => (
            <EpicCard
              key={epic.number}
              epic={epic}
              group={group ?? (epic.status === EpicStatus.Done ? 'done' : epic.status === EpicStatus.Closed ? 'closed' : 'paused')}
              onStartNextIssue={onStartNextIssue}
              startPending={
                group === 'readyToStart' &&
                pendingStartIssueNumber != null &&
                epic.progress.nextIssue?.number === pendingStartIssueNumber
              }
            />
          ))}
        </div>
      )}
    </section>
  )
}

type EpicSortField = 'priority' | 'updated'
type EpicSortDir = 'asc' | 'desc'

const EPIC_SORT_OPTIONS: { value: EpicSortField; label: string }[] = [
  { value: 'priority', label: 'Priority' },
  { value: 'updated', label: 'Updated' },
]

const EPIC_DIR_OPTIONS: { value: EpicSortDir; label: string }[] = [
  { value: 'asc', label: 'Ascending' },
  { value: 'desc', label: 'Descending' },
]

function normalizeSort(value: string | null | undefined): EpicSortField | null {
  if (!value) return null
  const lower = value.toLowerCase()
  return EPIC_SORT_OPTIONS.some((option) => option.value === lower) ? (lower as EpicSortField) : null
}

function normalizeDir(value: string | null | undefined): EpicSortDir | null {
  if (!value) return null
  const lower = value.toLowerCase()
  return EPIC_DIR_OPTIONS.some((option) => option.value === lower) ? (lower as EpicSortDir) : null
}

export function EpicListPage() {
  const [searchInput, setSearchInput] = useState('')
  const [sortField, setSortField] = useState<EpicSortField | null>(null)
  const [sortDir, setSortDir] = useState<EpicSortDir | null>(null)
  const trimmedSearch = searchInput.trim()
  const effectiveSortDir = sortField ? (sortDir ?? 'asc') : sortDir
  const hasActiveQuery = trimmedSearch.length > 0 || sortField !== null || sortDir !== null
  const { data: epics, isLoading } = useEpics({
    search: trimmedSearch || undefined,
    sort: sortField ?? undefined,
    dir: effectiveSortDir ?? undefined,
  })
  const startIssue = useStartIssue()
  const [pendingStartIssueNumber, setPendingStartIssueNumber] = useState<number | null>(null)
  const [showCreate, setShowCreate] = useState(false)

  function handleSortFieldChange(next: string) {
    const normalized = normalizeSort(next)
    setSortField(normalized)
    if (!normalized) setSortDir(null)
  }

  function handleSortDirChange(next: string) {
    const normalized = normalizeDir(next)
    setSortDir(normalized)
    if (normalized && !sortField) setSortField('priority')
  }

  const activeEpics = epics?.filter(e => e.status === EpicStatus.Idle || e.status === EpicStatus.Running) ?? []
  const pausedEpics = epics?.filter(e => e.status === EpicStatus.Paused) ?? []
  const doneEpics = epics?.filter(e => e.status === EpicStatus.Done) ?? []
  const closedEpics = epics?.filter(e => e.status === EpicStatus.Closed) ?? []

  const groups: ActiveEpicGroups = groupActiveEpics(activeEpics)

  const activeSections: ActiveGroupDescriptor[] = [
    { key: 'running', title: 'Running', testIdPrefix: 'epic-section-running', epics: groups.running },
    { key: 'readyToStart', title: 'Ready to start', testIdPrefix: 'epic-section-ready', epics: groups.readyToStart },
    { key: 'waitingBlocked', title: 'Waiting / Blocked', testIdPrefix: 'epic-section-waiting', epics: groups.waitingBlocked },
    { key: 'idleEmpty', title: 'Idle / Empty', testIdPrefix: 'epic-section-idle', epics: groups.idleEmpty },
  ]

  function handleStartNextIssue(issueNumber: number) {
    setPendingStartIssueNumber(issueNumber)
    startIssue.mutate(issueNumber, {
      onSettled: () => setPendingStartIssueNumber(null),
    })
  }

  return (
    <div className="max-w-4xl mx-auto p-6">
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-bold text-foreground">Epics</h1>
        <Button
          onClick={() => setShowCreate(true)}
        >
          <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
            <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
          </svg>
          New Epic
        </Button>
      </div>

      <div
        className="flex flex-wrap items-end gap-3 mb-6"
        data-testid="epic-list-toolbar"
      >
        <div className="flex flex-col gap-1 grow min-w-[12rem]">
          <label
            htmlFor="epic-search"
            className="text-xs font-medium text-muted-foreground uppercase tracking-wide"
          >
            Search
          </label>
          <input
            id="epic-search"
            type="search"
            value={searchInput}
            onChange={(event) => setSearchInput(event.target.value)}
            placeholder="Filter epics by title"
            aria-label="Search epics by title"
            data-testid="epic-search-input"
            className="w-full rounded-md border border-input bg-background px-3 py-1.5 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring"
          />
        </div>
        <div className="flex flex-col gap-1">
          <label
            htmlFor="epic-sort-field"
            className="text-xs font-medium text-muted-foreground uppercase tracking-wide"
          >
            Sort by
          </label>
          <select
            id="epic-sort-field"
            value={sortField ?? ''}
            onChange={(event) => handleSortFieldChange(event.target.value)}
            data-testid="epic-sort-field"
            className="rounded-md border border-input bg-background px-3 py-1.5 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring"
          >
            <option value="">Default</option>
            {EPIC_SORT_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>
        <div className="flex flex-col gap-1">
          <label
            htmlFor="epic-sort-dir"
            className="text-xs font-medium text-muted-foreground uppercase tracking-wide"
          >
            Direction
          </label>
          <select
            id="epic-sort-dir"
            value={effectiveSortDir ?? ''}
            onChange={(event) => handleSortDirChange(event.target.value)}
            data-testid="epic-sort-dir"
            className="rounded-md border border-input bg-background px-3 py-1.5 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring"
          >
            <option value="">Default</option>
            {EPIC_DIR_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>
      </div>

      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="text-muted-foreground">Loading...</div>
        </div>
      ) : epics && epics.length === 0 ? (
        <div className="text-center py-12">
          {hasActiveQuery ? (
            <div className="text-muted-foreground text-lg mb-4">No epics match this view</div>
          ) : (
            <>
              <div className="text-muted-foreground text-lg mb-4">No epics yet</div>
              <Button
                onClick={() => setShowCreate(true)}
              >
                Create your first Epic
              </Button>
            </>
          )}
        </div>
      ) : (
        <div className="space-y-8">
          {activeSections
            .filter(section => section.epics.length > 0)
            .map(section => (
              <EpicSection
                key={section.key}
                title={section.title}
                epics={section.epics}
                defaultExpanded={true}
                testIdPrefix={section.testIdPrefix}
                group={section.key}
                onStartNextIssue={handleStartNextIssue}
                pendingStartIssueNumber={pendingStartIssueNumber}
              />
            ))}

          {pausedEpics.length > 0 && (
            <section data-testid="epic-section-paused">
              <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide mb-3">Paused</h2>
              <div className="grid gap-4">
                {pausedEpics.map(epic => (
                  <EpicCard
                    key={epic.number}
                    epic={epic}
                    group="paused"
                    onStartNextIssue={handleStartNextIssue}
                    startPending={
                      pendingStartIssueNumber != null &&
                      epic.progress.nextIssue?.number === pendingStartIssueNumber
                    }
                  />
                ))}
              </div>
            </section>
          )}

          {doneEpics.length > 0 && (
            <EpicSection
              title="Done"
              epics={doneEpics}
              defaultExpanded={false}
              testIdPrefix="epic-section-done"
              group="done"
              onStartNextIssue={handleStartNextIssue}
              pendingStartIssueNumber={pendingStartIssueNumber}
            />
          )}

          {closedEpics.length > 0 && (
            <EpicSection
              title="Closed"
              epics={closedEpics}
              defaultExpanded={false}
              testIdPrefix="epic-section-closed"
              group="closed"
              onStartNextIssue={handleStartNextIssue}
              pendingStartIssueNumber={pendingStartIssueNumber}
            />
          )}
        </div>
      )}

      <EpicCreateDialog open={showCreate} onClose={() => setShowCreate(false)} />
    </div>
  )
}
