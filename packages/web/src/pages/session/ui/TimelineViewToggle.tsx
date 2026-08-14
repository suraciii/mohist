import { ListIcon, BracesIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'

export function TimelineViewToggle({
  value,
  onChange,
}: {
  value: 'summary' | 'raw'
  onChange: (value: 'summary' | 'raw') => void
}) {
  return (
    <div
      className="flex items-center justify-end gap-1 border-b border-border px-4 py-2"
      data-testid="session-timeline-view-toggle"
      role="group"
      aria-label="Timeline view"
    >
      <Button
        type="button"
        size="sm"
        variant={value === 'summary' ? 'secondary' : 'ghost'}
        aria-pressed={value === 'summary'}
        data-testid="session-timeline-summary-trigger"
        onClick={() => onChange('summary')}
      >
        <ListIcon aria-hidden="true" />
        Summary
      </Button>
      <Button
        type="button"
        size="sm"
        variant={value === 'raw' ? 'secondary' : 'ghost'}
        aria-pressed={value === 'raw'}
        data-testid="session-timeline-raw-trigger"
        onClick={() => onChange('raw')}
      >
        <BracesIcon aria-hidden="true" />
        Raw
      </Button>
    </div>
  )
}
