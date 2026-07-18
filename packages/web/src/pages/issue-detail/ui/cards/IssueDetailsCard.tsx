import { CardSection } from '@/shared/ui/components/card-section'
import type { Issue } from '../../../../entities/issue'
import { formatStageName } from '../../model/format'

export type IssueDetailsCardIssue = Pick<
  Issue,
  'status' | 'projectName' | 'repository' | 'workflowStage' | 'parentIssueRef' | 'childIssuesSummary'
>

export interface IssueDetailsCardProps {
  issue: IssueDetailsCardIssue
  unframed?: boolean
}

export function IssueDetailsCard({ issue, unframed = false }: IssueDetailsCardProps) {
  const workflowStage = issue.workflowStage ?? null
  const content = (
    <dl className="min-w-0 space-y-2 text-sm" data-testid="issue-detail-details-metadata">
        <div className="flex min-w-0 justify-between gap-3">
          <dt className="text-muted-foreground">Issue Stage</dt>
          <dd className="min-w-0 text-foreground font-medium text-right">
            {formatStageName(issue.status)}
          </dd>
        </div>
        {workflowStage && (
          <div className="flex min-w-0 justify-between gap-3">
            <dt className="text-muted-foreground">Workflow Stage</dt>
            <dd className="min-w-0 text-foreground font-medium text-right">
              {formatStageName(workflowStage)}
            </dd>
          </div>
        )}
        {issue.parentIssueRef && (
          <div className="flex min-w-0 justify-between gap-3" data-testid="parent-issue-metadata-row">
            <dt className="text-muted-foreground">Parent Issue</dt>
            <dd className="min-w-0 text-foreground font-medium text-right break-words">
              #{issue.parentIssueRef.number} {issue.parentIssueRef.title}
            </dd>
          </div>
        )}
        {issue.childIssuesSummary?.hasChildren && (
          <div className="flex min-w-0 justify-between gap-3" data-testid="child-issues-metadata-row">
            <dt className="text-muted-foreground">Parent Issue</dt>
            <dd className="min-w-0 text-foreground font-medium text-right">
              {issue.childIssuesSummary.count} child issue{issue.childIssuesSummary.count === 1 ? '' : 's'}
            </dd>
          </div>
        )}
        {issue.projectName && (
          <div className="flex min-w-0 justify-between gap-3">
            <dt className="text-muted-foreground">Project</dt>
            <dd className="min-w-0 text-foreground text-right break-words">
              {issue.projectName}
            </dd>
          </div>
        )}
        {issue.repository && (
          <div className="flex min-w-0 justify-between gap-3" data-testid="repository-metadata-row">
            <dt className="shrink-0 text-muted-foreground">Repository</dt>
            <dd className="min-w-0 text-foreground text-right" data-testid="repository-metadata-value">
              <span className="block min-w-0 break-words" data-testid="repository-name">
                {issue.repository.name}
              </span>
              {issue.repository.baseBranch && (
                <span className="block min-w-0 text-xs text-muted-foreground/80 break-words" data-testid="repository-base-branch">
                  {issue.repository.baseBranch}
                </span>
              )}
              {issue.repository.gitUrl && (
                <span
                  className="block min-w-0 break-all text-xs text-muted-foreground/70"
                  title={issue.repository.gitUrl}
                  data-testid="repository-git-url"
                >
                  {issue.repository.gitUrl}
                </span>
              )}
            </dd>
          </div>
        )}
    </dl>
  )
  if (unframed) return content
  return <CardSection title="Details">{content}</CardSection>
}
