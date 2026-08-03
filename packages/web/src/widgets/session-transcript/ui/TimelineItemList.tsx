import { isTimelineGroup, type TimelineEntry } from '@/entities/session'
import { TimelineGroupRow } from './TimelineGroupRow'
import { TimelineItemRow, type TimelineReferenceResolver } from './TimelineItemRow'

export interface TimelineItemListProps {
  entries: TimelineEntry[]
  resolveReference?: TimelineReferenceResolver
}

export function TimelineItemList({ entries, resolveReference }: TimelineItemListProps) {
  return (
    <div className="space-y-2" data-testid="timeline-item-list" role="list">
      {entries.map((entry) => (
        <div key={entry.id} role="listitem">
          {isTimelineGroup(entry) ? (
            <TimelineGroupRow group={entry} resolveReference={resolveReference} />
          ) : (
            <TimelineItemRow item={entry} resolveReference={resolveReference} />
          )}
        </div>
      ))}
    </div>
  )
}
