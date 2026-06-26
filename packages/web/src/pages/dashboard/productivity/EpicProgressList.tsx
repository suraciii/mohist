import { useMemo } from 'react'
import { EpicStatus, type EpicPriority, type EpicWithProgress } from '../../../entities/epic'
import { useEpics } from '../../../entities/epic'

const PRIORITY_RANK: Record<EpicPriority, number> = {
  p0: 0,
  p1: 1,
  p2: 2,
  p3: 3,
  p4: 4,
}

const VISIBLE_CAP = 3

function ratioSortKey(epic: EpicWithProgress): number {
  const { deliveredCount, totalIssueCount } = epic.progress
  if (totalIssueCount <= 0) return 0
  return deliveredCount / totalIssueCount
}

function sortActiveEpics(epics: EpicWithProgress[]): EpicWithProgress[] {
  return [...epics].sort((a, b) => {
    const priorityDiff = PRIORITY_RANK[a.priority] - PRIORITY_RANK[b.priority]
    if (priorityDiff !== 0) return priorityDiff
    return ratioSortKey(a) - ratioSortKey(b)
  })
}

function isInProgressEpic(epic: EpicWithProgress): boolean {
  return epic.status === EpicStatus.Idle || epic.status === EpicStatus.Running
}

interface ProgressBarProps {
  deliveredCount: number
  totalIssueCount: number
  testId: string
}

function ProgressBar({ deliveredCount, totalIssueCount, testId }: ProgressBarProps) {
  return (
    <div className="w-full bg-muted rounded-full h-1.5" data-testid={testId}>
      <div
        className="bg-blue-600 h-1.5 rounded-full transition-all"
        style={{
          width: totalIssueCount > 0
            ? `${(deliveredCount / totalIssueCount) * 100}%`
            : '0%',
        }}
      />
    </div>
  )
}

interface EpicRowProps {
  epic: EpicWithProgress
  index: number
}

function EpicRow({ epic, index }: EpicRowProps) {
  const { deliveredCount, totalIssueCount } = epic.progress
  const numberLabel = epic.number != null ? `#${epic.number}` : `#${epic.id.slice(0, 8)}`

  return (
    <div className="flex flex-col gap-1" data-testid={`productivity-epic-list-item-${index}`}>
      <div className="flex items-center justify-between text-sm">
        <span className="font-medium text-foreground truncate">
          <span className="text-muted-foreground mr-1.5">{numberLabel}</span>
          {epic.title}
        </span>
        <span className="font-medium text-foreground tabular-nums">
          {deliveredCount} / {totalIssueCount}
        </span>
      </div>
      <ProgressBar
        deliveredCount={deliveredCount}
        totalIssueCount={totalIssueCount}
        testId={`productivity-epic-list-bar-${index}`}
      />
    </div>
  )
}

export function EpicProgressList() {
  const { data: epics } = useEpics()

  const { visible, remaining } = useMemo(() => {
    const active = (epics ?? []).filter(isInProgressEpic)
    const sorted = sortActiveEpics(active)
    return {
      visible: sorted.slice(0, VISIBLE_CAP),
      remaining: sorted.length - VISIBLE_CAP,
    }
  }, [epics])

  if (visible.length < 2) {
    return (
      <section
        data-testid="productivity-epic-list"
        data-state="empty"
        aria-label="In-progress Epic progress"
        className="rounded-lg border border-border bg-card/50 p-4"
      >
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            In-progress Epics
          </h3>
        </div>
        <p
          data-testid="productivity-epic-list-empty"
          className="text-sm text-muted-foreground"
        >
          No active Epics yet — progress bars appear once at least two Epics are in progress.
        </p>
      </section>
    )
  }

  return (
    <section
      data-testid="productivity-epic-list"
      aria-label="In-progress Epic progress"
      className="rounded-lg border border-border bg-card/50 p-4"
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          In-progress Epics
        </h3>
        {remaining > 0 && (
          <span
            data-testid="productivity-epic-list-more"
            className="text-xs text-muted-foreground"
          >
            +{remaining} more
          </span>
        )}
      </div>
      <div className="flex flex-col gap-3">
        {visible.map((epic, index) => (
          <EpicRow key={epic.id} epic={epic} index={index} />
        ))}
      </div>
    </section>
  )
}
