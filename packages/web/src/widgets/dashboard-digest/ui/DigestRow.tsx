import { Link } from 'react-router-dom'
import { useProjectPath } from '../../../entities/project'
import { formatTimeAgo } from '../../../shared/lib/format-time'
import type { Issue } from '../../../entities/issue'

interface DigestRowProps {
  issue: Issue
  timestamp: string
  now?: number
}

export function DigestRow({ issue, timestamp, now }: DigestRowProps) {
  const toProjectPath = useProjectPath()
  const date = new Date(timestamp)
  const relative = Number.isNaN(date.getTime()) ? 'Unknown time' : formatTimeAgo(date, now)

  return (
    <Link
      to={toProjectPath(`/issues/${issue.number}`)}
      data-testid="digest-row"
      data-issue-number={issue.number}
      className="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 text-sm hover:bg-muted/50 transition-colors"
    >
      <span className="flex items-center gap-2 min-w-0">
        <span className="font-mono text-xs text-muted-foreground tabular-nums shrink-0">
          #{issue.number}
        </span>
        <span
          className="truncate text-foreground"
          title={issue.title}
        >
          {issue.title}
        </span>
      </span>
      <span className="shrink-0 text-xs text-muted-foreground/70 tabular-nums">{relative}</span>
    </Link>
  )
}
