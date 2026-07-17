import { useMemo } from 'react'
import { useEpicEvents, type StoredCloudEventDto } from '../../../../entities/epic'

const EPIC_EVENT_TYPE = {
  Created: 'com.mohist.epic.created',
  Updated: 'com.mohist.epic.updated',
  PriorityChanged: 'com.mohist.epic.priority-changed',
  IssueLinked: 'com.mohist.epic.issue-linked',
  IssueUnlinked: 'com.mohist.epic.issue-unlinked',
  StatusChanged: 'com.mohist.epic.status-changed',
  Closed: 'com.mohist.epic.closed',
  Reopened: 'com.mohist.epic.reopened',
} as const

type EpicEventType = (typeof EPIC_EVENT_TYPE)[keyof typeof EPIC_EVENT_TYPE]

type TimelineEntry =
  | { kind: 'created'; time: string }
  | { kind: 'updated'; time: string }
  | { kind: 'priority'; time: string; oldPriority: string; newPriority: string }
  | { kind: 'issue-linked'; time: string; issueNumber: number }
  | { kind: 'issue-unlinked'; time: string; issueNumber: number }
  | { kind: 'status'; time: string; oldStatus: string; newStatus: string }
  | { kind: 'closed'; time: string }
  | { kind: 'reopened'; time: string }
  | { kind: 'unknown'; time: string; rawType: string }

function readPayloadNumber(payload: unknown, key: string): number | null {
  if (payload && typeof payload === 'object' && key in (payload as Record<string, unknown>)) {
    const value = (payload as Record<string, unknown>)[key]
    if (typeof value === 'number' && Number.isFinite(value)) return value
  }
  return null
}

function readPayloadString(payload: unknown, key: string): string | null {
  if (payload && typeof payload === 'object' && key in (payload as Record<string, unknown>)) {
    const value = (payload as Record<string, unknown>)[key]
    if (typeof value === 'string' && value.length > 0) return value
  }
  return null
}

function describeEntry(entry: TimelineEntry): { icon: string; text: string } {
  switch (entry.kind) {
    case 'created':
      return { icon: '✨', text: 'Epic created' }
    case 'updated':
      return { icon: '✏️', text: 'Epic updated' }
    case 'priority':
      return { icon: '⚡', text: `Priority changed from ${entry.oldPriority.toUpperCase()} to ${entry.newPriority.toUpperCase()}` }
    case 'issue-linked':
      return { icon: '🔗', text: `Linked issue #${entry.issueNumber}` }
    case 'issue-unlinked':
      return { icon: '🔓', text: `Unlinked issue #${entry.issueNumber}` }
    case 'status': {
      const oldS = entry.oldStatus.charAt(0).toUpperCase() + entry.oldStatus.slice(1)
      const newS = entry.newStatus.charAt(0).toUpperCase() + entry.newStatus.slice(1)
      return { icon: '🔄', text: `Status changed from ${oldS} to ${newS}` }
    }
    case 'closed':
      return { icon: '🗄️', text: 'Epic closed' }
    case 'reopened':
      return { icon: '🔁', text: 'Epic reopened' }
    case 'unknown':
      return { icon: '•', text: entry.rawType }
  }
}

function dataTestIdFor(entry: TimelineEntry): string {
  return `epic-activity-entry-${entry.kind}`
}

function toEntry(event: StoredCloudEventDto): TimelineEntry | null {
  if (!event.time) return null
  const payload = event.data
  switch (event.type as EpicEventType) {
    case EPIC_EVENT_TYPE.Created:
      return { kind: 'created', time: event.time }
    case EPIC_EVENT_TYPE.Updated:
      return { kind: 'updated', time: event.time }
    case EPIC_EVENT_TYPE.PriorityChanged:
      return {
        kind: 'priority',
        time: event.time,
        oldPriority: readPayloadString(payload, 'oldPriority') ?? '?',
        newPriority: readPayloadString(payload, 'newPriority') ?? '?',
      }
    case EPIC_EVENT_TYPE.IssueLinked: {
      const n = readPayloadNumber(payload, 'issueNumber')
      if (n === null) return null
      return { kind: 'issue-linked', time: event.time, issueNumber: n }
    }
    case EPIC_EVENT_TYPE.IssueUnlinked: {
      const n = readPayloadNumber(payload, 'issueNumber')
      if (n === null) return null
      return { kind: 'issue-unlinked', time: event.time, issueNumber: n }
    }
    case EPIC_EVENT_TYPE.StatusChanged: {
      const oldStatus = readPayloadString(payload, 'oldStatus')
      const newStatus = readPayloadString(payload, 'newStatus')
      if (!oldStatus || !newStatus) return null
      return { kind: 'status', time: event.time, oldStatus, newStatus }
    }
    case EPIC_EVENT_TYPE.Closed:
      return { kind: 'closed', time: event.time }
    case EPIC_EVENT_TYPE.Reopened:
      return { kind: 'reopened', time: event.time }
    default:
      return { kind: 'unknown', time: event.time, rawType: event.type }
  }
}

function formatTime(time: string): string {
  const d = new Date(time)
  if (Number.isNaN(d.getTime())) return time
  return d.toLocaleString()
}

export interface EpicActivityTimelineSectionProps {
  epicNumber: number
  eventsHook?: typeof useEpicEvents
}

export function EpicActivityTimelineSection({
  epicNumber,
  eventsHook = useEpicEvents,
}: EpicActivityTimelineSectionProps) {
  const { data: events, isLoading, isError } = eventsHook(epicNumber)

  const entries = useMemo(() => {
    if (!events) return []
    return events
      .map(toEntry)
      .filter((entry): entry is TimelineEntry => entry !== null)
  }, [events])

  if (isLoading) {
    return (
      <section
        className="rounded-lg border bg-card p-6 text-card-foreground shadow-sm"
        data-testid="epic-activity-timeline-loading"
        data-empty="false"
      >
        <h2 className="text-lg font-semibold text-foreground">Activity</h2>
        <p className="mt-2 text-sm text-muted-foreground">Loading activity…</p>
      </section>
    )
  }

  if (isError) {
    return (
      <section
        className="rounded-lg border bg-card p-6 text-card-foreground shadow-sm"
        data-testid="epic-activity-timeline-error"
        data-empty="false"
      >
        <h2 className="text-lg font-semibold text-foreground">Activity</h2>
        <p className="mt-2 text-sm text-muted-foreground">Failed to load activity.</p>
      </section>
    )
  }

  return (
    <section
      className="rounded-lg border bg-card p-6 text-card-foreground shadow-sm"
      data-testid="epic-activity-timeline"
      data-empty={entries.length === 0 ? 'true' : 'false'}
    >
      <h2 className="text-lg font-semibold text-foreground">Activity</h2>
      {entries.length === 0 ? (
        <p
          className="mt-2 text-sm text-muted-foreground"
          data-testid="epic-activity-timeline-empty"
        >
          No activity recorded yet.
        </p>
      ) : (
        <ul className="mt-3 space-y-2" data-testid="epic-activity-timeline-list">
          {entries.map((entry, idx) => {
            const { icon, text } = describeEntry(entry)
            return (
              <li
                key={`${entry.time}-${idx}`}
                className="flex items-start gap-3 text-sm"
                data-testid={dataTestIdFor(entry)}
                data-time={entry.time}
              >
                <span aria-hidden className="select-none text-base">
                  {icon}
                </span>
                <div className="min-w-0 flex-1">
                  <p className="text-foreground">{text}</p>
                  <p className="text-xs text-muted-foreground">{formatTime(entry.time)}</p>
                </div>
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}
