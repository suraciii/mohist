import type { Issue } from '../../../entities/issue'
import { useRecentDigest } from '../../../entities/issue'
import { DigestRow } from './DigestRow'

interface DigestSectionProps {
  testId: string
  label: string
  issues: Issue[]
  timestampFor: (issue: Issue) => string
}

function DigestSection({ testId, label, issues, timestampFor }: DigestSectionProps) {
  if (issues.length === 0) return null
  return (
    <div data-testid={testId} className="space-y-1">
      <h3 className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground/70 px-2">
        {label}
      </h3>
      <div className="space-y-0.5">
        {issues.map((issue) => (
          <DigestRow key={`${issue.projectId}:${issue.number}`} issue={issue} timestamp={timestampFor(issue)} />
        ))}
      </div>
    </div>
  )
}

export function DashboardDigestWidget({
  digestHook = useRecentDigest,
}: {
  digestHook?: typeof useRecentDigest
} = {}) {
  const { completed, failed, archived, isLoading } = digestHook()

  if (isLoading) {
    return (
      <div
        data-testid="dashboard-digest-loading"
        className="flex items-center justify-center min-h-[120px] text-sm text-muted-foreground"
        role="status"
        aria-live="polite"
      >
        Loading digest…
      </div>
    )
  }

  const hasAny = completed.length > 0 || failed.length > 0 || archived.length > 0
  if (!hasAny) {
    return (
      <div
        data-testid="dashboard-digest-empty"
        className="flex items-center justify-center min-h-[120px] text-sm text-muted-foreground"
      >
        No recent activity
      </div>
    )
  }

  return (
    <div data-testid="dashboard-digest-content" className="space-y-4">
      <DigestSection
        testId="dashboard-digest-completed"
        label="Completed"
        issues={completed}
        timestampFor={(issue) => issue.completedAt ?? issue.updatedAt}
      />
      <DigestSection
        testId="dashboard-digest-failed"
        label="Failed"
        issues={failed}
        timestampFor={(issue) => issue.updatedAt}
      />
      <DigestSection
        testId="dashboard-digest-archived"
        label="Archived"
        issues={archived}
        timestampFor={(issue) => issue.archivedAt ?? issue.updatedAt}
      />
    </div>
  )
}
