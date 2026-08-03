import type { TimelineEntry, TimelineFact } from '@/entities/session'
import type { SessionTimelineCurrentActivity } from '../model/useSessionTimeline'
import { RawTimelineView } from './RawTimelineView'
import { TimelineItemList } from './TimelineItemList'
import type { TimelineReferenceResolver } from './TimelineItemRow'

export type SessionTimelineView = 'summary' | 'raw'

interface SessionTranscriptLayoutProps {
  entries: TimelineEntry[]
  facts: TimelineFact[]
  currentActivity: SessionTimelineCurrentActivity
  viewMode?: SessionTimelineView
  resolveReference?: TimelineReferenceResolver
}

export function TranscriptEmptyState({
  currentActivity,
}: {
  currentActivity: SessionTimelineCurrentActivity
}) {
  const stateKind = currentActivity.state === 'active' || currentActivity.state === 'queued'
    ? 'active-no-content'
    : `${currentActivity.state}-no-content`

  return (
    <div
      className="flex items-center justify-center py-12"
      data-testid="session-empty-state"
      data-state-kind={stateKind}
      data-tone={currentActivity.state === 'unknown' ? 'warning' : currentActivity.state === 'idle' ? 'neutral' : 'info'}
    >
      <div className="text-center space-y-2">
        <div className="text-sm font-medium">{currentActivity.label}</div>
        <p className="text-sm text-muted-foreground">
          {currentActivity.state === 'unknown'
            ? 'Mohist cannot confirm whether execution is still active.'
            : currentActivity.state === 'idle'
              ? 'No activity recorded for this session.'
              : 'Waiting for runtime activity.'}
        </p>
      </div>
    </div>
  )
}

function CurrentActivity({ activity }: { activity: SessionTimelineCurrentActivity }) {
  return (
    <div
      className="mb-3 flex items-center gap-2 border-b border-border px-1 pb-2 text-xs text-muted-foreground"
      data-testid="timeline-current-activity"
      data-activity-state={activity.state}
      role="status"
      aria-live="polite"
    >
      <span className="font-medium text-foreground">Current activity</span>
      <span data-testid="timeline-current-activity-label">{activity.label}</span>
    </div>
  )
}

export function SessionTranscriptLayout({
  entries,
  facts,
  currentActivity,
  viewMode = 'summary',
  resolveReference,
}: SessionTranscriptLayoutProps) {
  return (
    <div className="block px-4 py-6 min-w-0" data-scrollable="" data-testid="session-timeline-layout" data-timeline-view={viewMode}>
      <div className="min-w-0">
        <CurrentActivity activity={currentActivity} />
        {entries.length === 0 ? (
          <TranscriptEmptyState currentActivity={currentActivity} />
        ) : viewMode === 'raw' ? (
          <RawTimelineView facts={facts} />
        ) : (
          <TimelineItemList entries={entries} resolveReference={resolveReference} />
        )}
      </div>
    </div>
  )
}
