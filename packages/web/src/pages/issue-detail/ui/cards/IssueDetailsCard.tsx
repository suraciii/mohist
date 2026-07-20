import { Link } from 'react-router-dom'
import { CardSection } from '@/shared/ui/components/card-section'
import type { Issue } from '../../../../entities/issue'
import { useProjectPath } from '../../../../entities/project'
import { formatStageName } from '../../model/format'

export type IssueDetailsCardIssue = Pick<
  Issue,
  'status' | 'projectName' | 'repository' | 'workflowStage' | 'parentIssueRef' | 'childIssuesSummary' | 'repositoryName'
>

export interface IssueDetailsCardProps {
  issue: IssueDetailsCardIssue
  unframed?: boolean
}

function resolveRepositoryName(issue: IssueDetailsCardIssue): string | null {
  const resolved = issue.repository?.name
  if (resolved && resolved.length > 0) return resolved
  const persisted = issue.repositoryName
  if (persisted && persisted.length > 0) return persisted
  return null
}

export function IssueDetailsCard({ issue, unframed = false }: IssueDetailsCardProps) {
  const workflowStage = issue.workflowStage ?? null
  const toProjectPath = useProjectPath()
  const repositoryName = resolveRepositoryName(issue)
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
              <Link
                to={toProjectPath(`/issues/${issue.parentIssueRef.number}`)}
                data-testid="parent-issue-backlink"
                data-parent-number={issue.parentIssueRef.number}
                className="hover:underline"
              >
                #{issue.parentIssueRef.number} {issue.parentIssueRef.title}
              </Link>
            </dd>
          </div>
        )}
        {issue.childIssuesSummary?.hasChildren && (
          <div className="flex min-w-0 justify-between gap-3" data-testid="child-issues-metadata-row">
            <dt className="text-muted-foreground">Parent Issue</dt>
            <dd className="min-w-0 text-foreground font-medium text-right">
              is a parent ({issue.childIssuesSummary.count} child issue{issue.childIssuesSummary.count === 1 ? '' : 's'})
            </dd>
          </div>
        )}
        {issue.childIssuesSummary?.hasChildren && (
          <div className="flex min-w-0 justify-between gap-3" data-testid="child-issues-progress-row">
            <dt className="text-muted-foreground">Children</dt>
            <dd className="min-w-0 text-foreground font-medium text-right">
              {issue.childIssuesSummary.doneCount} done / {issue.childIssuesSummary.inProgressCount} in-progress / {issue.childIssuesSummary.cancelledCount} cancelled / {issue.childIssuesSummary.backlogCount} backlog / {issue.childIssuesSummary.count} total
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
        {(repositoryName || issue.repository) && (
          <div className="flex min-w-0 justify-between gap-3" data-testid="repository-metadata-row">
            <dt className="shrink-0 text-muted-foreground">Repository</dt>
            <dd className="min-w-0 text-foreground text-right" data-testid="repository-metadata-value">
              <span className="block min-w-0 break-words" data-testid="repository-name">
                {repositoryName ?? issue.repository?.name ?? ''}
              </span>
              {issue.repository?.baseBranch && (
                <span className="block min-w-0 text-xs text-muted-foreground/80 break-words" data-testid="repository-base-branch">
                  {issue.repository.baseBranch}
                </span>
              )}
              {issue.repository?.gitUrl && (
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