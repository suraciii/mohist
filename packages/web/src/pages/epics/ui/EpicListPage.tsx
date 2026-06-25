import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useEpics, useStartIssue } from '../../../entities/epic'
import { EpicStatus, type EpicProgress, type EpicWithProgress } from '../../../entities/epic'
import { EpicCreateDialog } from '../../../features/create-epic'
import { Button } from '@/shared/ui/components/button'
import { Badge } from '@/shared/ui/components/badge'
import { Card } from '@/shared/ui/components/card'
import { useProjectPath } from '../../../entities/project'

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

function statusText(
  status: EpicStatus,
  progress: EpicProgress,
  onStart?: (issueNumber: number) => void,
  startPending?: boolean,
): React.ReactNode {
  if (status === EpicStatus.Done) {
    return <span className="text-blue-700 font-medium">Completed</span>
  }
  if (status === EpicStatus.Closed) {
    return <span className="text-muted-foreground font-medium">Closed</span>
  }
  const inProgress = progress.activeIssues[0]
  const next = progress.nextIssue
  const nextReason = progress.nextIssueReason

  return (
    <div className="flex flex-col gap-0.5">
      {inProgress && (
        <span className="text-muted-foreground" data-testid="epic-card-in-progress">
          In progress: <span className="text-foreground/80 font-medium">#{inProgress.number}</span>
          <span className="text-muted-foreground ml-1">{inProgress.title}</span>
        </span>
      )}
      {next ? (
        <div className="flex items-center justify-between gap-2">
          <span className="text-muted-foreground min-w-0" data-testid="epic-card-next">
            Next: <span className="text-foreground/80 font-medium">#{next.number}</span>
            <span className="text-muted-foreground ml-1">{next.title}</span>
          </span>
          {onStart && (
            <Button
              type="button"
              size="sm"
              onClick={(e) => {
                e.stopPropagation()
                onStart(next.number)
              }}
              disabled={startPending}
              data-testid="epic-card-start"
            >
              {startPending ? 'Starting...' : 'Start'}
            </Button>
          )}
        </div>
      ) : nextReason ? (
        <span className="text-muted-foreground" data-testid="epic-card-next">{nextReason}</span>
      ) : progress.readyToMarkDone ? (
        <span className="text-green-600 font-medium" data-testid="epic-card-ready">Ready to mark done</span>
      ) : (
        <span className="text-muted-foreground">No linked issues</span>
      )}
    </div>
  )
}

function EpicCard({
  epic,
  onStart,
  startPending,
}: {
  epic: EpicWithProgress
  onStart: (issueNumber: number) => void
  startPending: boolean
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
      onClick={() => navigate(toProjectPath(`/epics/${epic.id}`))}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <span
              className="text-sm font-medium text-muted-foreground"
              data-testid="epic-number"
            >
              {epic.number != null ? `#${epic.number}` : `#${epic.id.slice(0, 8)}`}
            </span>
            <StatusBadge status={epic.status} />
            <PriorityBadge priority={epic.priority} />
          </div>
          <h3 className="text-base font-semibold text-foreground truncate">{epic.title}</h3>
        </div>
      </div>

      <div className="mt-3">
        <div className="flex items-center justify-between text-sm mb-1">
          <span className="text-muted-foreground">Progress</span>
          <span className="font-medium text-foreground">
            {progress.deliveredCount} / {progress.totalIssueCount} completed
          </span>
        </div>
        <div className="w-full bg-muted rounded-full h-1.5">
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
        <div className="text-sm min-w-0 flex-1">
          {statusText(epic.status, progress, onStart, startPending)}
        </div>
      </div>
    </Card>
  )
}

interface EpicSectionProps {
  title: string
  epics: EpicWithProgress[]
  defaultExpanded: boolean
  testIdPrefix: string
  onStart: (issueNumber: number) => void
  pendingStartIssueNumber: number | null
}

function EpicSection({ title, epics, defaultExpanded, testIdPrefix, onStart, pendingStartIssueNumber }: EpicSectionProps) {
  const [expanded, setExpanded] = useState(defaultExpanded)

  return (
    <section data-testid={testIdPrefix}>
      <div className="flex items-center justify-between mb-3">
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
              key={epic.id}
              epic={epic}
              onStart={onStart}
              startPending={pendingStartIssueNumber === epic.progress.nextIssue?.number}
            />
          ))}
        </div>
      )}
    </section>
  )
}

export function EpicListPage() {
  const { data: epics, isLoading } = useEpics()
  const startIssue = useStartIssue()
  const [pendingStartIssueNumber, setPendingStartIssueNumber] = useState<number | null>(null)
  const [showCreate, setShowCreate] = useState(false)

  const activeEpics = epics?.filter(e => e.status === EpicStatus.Idle || e.status === EpicStatus.Running) ?? []
  const pausedEpics = epics?.filter(e => e.status === EpicStatus.Paused) ?? []
  const doneEpics = epics?.filter(e => e.status === EpicStatus.Done) ?? []
  const closedEpics = epics?.filter(e => e.status === EpicStatus.Closed) ?? []

  function handleStartIssue(issueNumber: number) {
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

      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <div className="text-muted-foreground">Loading...</div>
        </div>
      ) : epics && epics.length === 0 ? (
        <div className="text-center py-12">
          <div className="text-muted-foreground text-lg mb-4">No epics yet</div>
          <Button
            onClick={() => setShowCreate(true)}
          >
            Create your first Epic
          </Button>
        </div>
      ) : (
        <div className="space-y-8">
          {activeEpics.length > 0 && (
            <EpicSection
              title="Active"
              epics={activeEpics}
              defaultExpanded={true}
              testIdPrefix="epic-section-active"
              onStart={handleStartIssue}
              pendingStartIssueNumber={pendingStartIssueNumber}
            />
          )}

          {pausedEpics.length > 0 && (
            <section>
              <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wide mb-3">Paused</h2>
              <div className="grid gap-4">
                {pausedEpics.map(epic => (
                  <EpicCard
                    key={epic.id}
                    epic={epic}
                    onStart={handleStartIssue}
                    startPending={pendingStartIssueNumber === epic.progress.nextIssue?.number}
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
              onStart={handleStartIssue}
              pendingStartIssueNumber={pendingStartIssueNumber}
            />
          )}

          {closedEpics.length > 0 && (
            <EpicSection
              title="Closed"
              epics={closedEpics}
              defaultExpanded={false}
              testIdPrefix="epic-section-closed"
              onStart={handleStartIssue}
              pendingStartIssueNumber={pendingStartIssueNumber}
            />
          )}
        </div>
      )}

      <EpicCreateDialog open={showCreate} onClose={() => setShowCreate(false)} />
    </div>
  )
}
