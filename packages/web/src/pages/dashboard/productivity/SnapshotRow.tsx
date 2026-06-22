import { useCompletionSnapshot, useIssues } from '../../../entities/issue'

interface CountBlockProps {
  label: string
  count: number
  tone: 'completed' | 'failed' | 'new'
  testId: string
}

const toneClasses: Record<CountBlockProps['tone'], string> = {
  completed: 'text-green-600',
  failed: 'text-red-600',
  new: 'text-blue-600',
}

function CountBlock({ label, count, tone, testId }: CountBlockProps) {
  return (
    <div className="flex flex-col items-start gap-0.5" data-testid={testId}>
      <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {label}
      </span>
      <span className={`text-2xl font-semibold tabular-nums ${toneClasses[tone]}`}>
        {count}
      </span>
    </div>
  )
}

export function SnapshotRow() {
  const { completed, failed, new: newly } = useCompletionSnapshot()
  const { data: issues } = useIssues()

  const hasNoIssues = !issues || issues.length === 0

  if (hasNoIssues) {
    return (
      <section
        data-testid="productivity-snapshot-row"
        data-state="empty"
        aria-label="Weekly completion snapshot"
        className="rounded-lg border border-border bg-card/50 p-4"
      >
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            This week
          </h3>
        </div>
        <p
          data-testid="productivity-snapshot-empty"
          className="text-sm text-muted-foreground"
        >
          No issues this week — counts will appear once issues are created.
        </p>
      </section>
    )
  }

  return (
    <section
      data-testid="productivity-snapshot-row"
      aria-label="Weekly completion snapshot"
      className="rounded-lg border border-border bg-card/50 p-4"
    >
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          This week
        </h3>
      </div>
      <div className="flex items-end gap-6">
        <CountBlock
          label="Completed"
          count={completed}
          tone="completed"
          testId="productivity-snapshot-completed"
        />
        <CountBlock
          label="Failed"
          count={failed}
          tone="failed"
          testId="productivity-snapshot-failed"
        />
        <CountBlock
          label="New"
          count={newly}
          tone="new"
          testId="productivity-snapshot-new"
        />
      </div>
    </section>
  )
}