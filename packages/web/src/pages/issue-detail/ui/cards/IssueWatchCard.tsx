import { CardSection } from '@/shared/ui/components/card-section'
import type { IssueWatchEntry } from '../../../../entities/issue'

export interface IssueWatchCardProps {
  entries: IssueWatchEntry[]
  variant: 'watching' | 'muted'
  unframed?: boolean
}

export function IssueWatchCard({ entries, variant, unframed = false }: IssueWatchCardProps) {
  const title = variant === 'watching' ? 'Watching' : 'Muted'
  const content = (
    <div className="space-y-2">
      {entries.map((entry) => (
        <div
          key={entry.agentId}
          data-testid={`issue-watch-${variant}-entry`}
          data-agent-id={entry.agentId}
          className="flex items-center justify-between text-sm gap-2"
        >
          <span className="font-mono truncate text-foreground/80">{entry.agentId}</span>
          <span
            className={
              variant === 'watching'
                ? 'inline-flex items-center gap-1 text-xs font-medium text-success bg-success-subtle border border-success-border px-1.5 py-0.5 rounded shrink-0'
                : 'inline-flex items-center gap-1 text-xs font-medium text-warning bg-warning-subtle border border-warning-border px-1.5 py-0.5 rounded shrink-0'
            }
          >
            {title}
          </span>
        </div>
      ))}
    </div>
  )
  if (unframed) return content
  return (
    <CardSection title={title} tone={variant === 'watching' ? 'green' : 'amber'}>
      {content}
    </CardSection>
  )
}
