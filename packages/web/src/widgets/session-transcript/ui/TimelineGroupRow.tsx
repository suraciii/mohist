import { useState } from 'react'
import { ChevronDownIcon } from 'lucide-react'
import type { TimelineGroup } from '@/entities/session'
import { TimelineItemRow, type TimelineReferenceResolver } from './TimelineItemRow'

const GROUP_STYLES: Record<TimelineGroup['renderClass'], string> = {
  'file-read': 'border-border/60 bg-background',
  shell: 'border-border/60 bg-background',
  tool: 'border-border/60 bg-background text-muted-foreground',
}

const GROUP_MARKERS: Record<TimelineGroup['renderClass'], string> = {
  'file-read': 'bg-muted-foreground/40',
  shell: 'bg-muted-foreground/60',
  tool: 'bg-muted-foreground/40',
}

function groupSourceIds(group: TimelineGroup): string[] {
  return group.sourceIds.length > 0 ? group.sourceIds : [group.id]
}

export interface TimelineGroupRowProps {
  group: TimelineGroup
  resolveReference?: TimelineReferenceResolver
}

export function TimelineGroupRow({ group, resolveReference }: TimelineGroupRowProps) {
  const [expanded, setExpanded] = useState(false)
  const sourceIds = groupSourceIds(group)

  return (
    <section
      className={`relative rounded-md border ${GROUP_STYLES[group.renderClass]}`}
      data-testid="timeline-group-row"
      data-timeline-group-id={group.id}
      data-timeline-render-class={group.renderClass}
      data-timeline-source-id={sourceIds[0]}
    >
      {sourceIds.slice(1).map((sourceId) => (
        <span
          key={sourceId}
          aria-hidden="true"
          className="sr-only"
          data-timeline-source-id={sourceId}
        />
      ))}

      <button
        type="button"
        aria-expanded={expanded}
        className="flex w-full items-center gap-3 px-3 py-2.5 text-left hover:bg-muted/40"
        data-testid="timeline-group-toggle"
        onClick={() => setExpanded((current) => !current)}
      >
        <span
          aria-hidden="true"
          className={`h-2 w-2 shrink-0 rounded-full ${GROUP_MARKERS[group.renderClass]}`}
        />
        <span className="min-w-0 flex-1 text-sm font-medium text-foreground">{group.summary}</span>
        <ChevronDownIcon
          aria-hidden="true"
          className={`size-4 shrink-0 text-muted-foreground transition-transform ${expanded ? 'rotate-180' : ''}`}
        />
      </button>

      {expanded && (
        <div className="space-y-2 border-t border-border p-2" data-testid="timeline-group-items">
          {group.items.map((item) => (
            <TimelineItemRow key={item.id} item={item} resolveReference={resolveReference} />
          ))}
        </div>
      )}
    </section>
  )
}
