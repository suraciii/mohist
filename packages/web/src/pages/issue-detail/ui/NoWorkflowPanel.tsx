import { IssueStatus, type Issue } from '../../../entities/issue'

export function NoWorkflowPanel({ issue }: { issue: Issue }) {
  return (
    <section data-testid="no-workflow-panel" className="rounded-lg border border-border bg-card px-5 py-4">
      <h2 className="text-sm font-semibold text-foreground">No workflow</h2>
      <p className="mt-1 text-sm text-muted-foreground">
        This Issue is tracked by Mohist but is not run by a Mohist Workflow.
      </p>
      <p className="mt-3 text-xs text-muted-foreground">
        {issue.status === IssueStatus.Backlog
          ? 'Start it to mark the work in progress.'
          : issue.status === IssueStatus.InProgress
            ? 'Mark it done when the work has been delivered, or close it when it will not be completed.'
            : 'The Issue is terminal and keeps its delivery history.'}
      </p>
    </section>
  )
}
