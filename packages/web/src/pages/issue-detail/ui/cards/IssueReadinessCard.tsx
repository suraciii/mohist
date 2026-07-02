import { CardSection } from '@/shared/ui/components/card-section'
import type { Issue } from '../../../../entities/issue'

export type IssueReadinessCardIssue = Pick<
  Issue,
  'isDraft' | 'canStart' | 'blocker'
>

export interface IssueReadinessCardProps {
  issue: IssueReadinessCardIssue
}

export function IssueReadinessCard({ issue }: IssueReadinessCardProps) {
  return (
    <CardSection
      title="Readiness"
      tone={issue.isDraft ? 'default' : issue.canStart ? 'green' : 'amber'}
    >
      <div className="space-y-2 text-sm" data-testid="readiness-panel">
        <div className="flex items-center justify-between gap-2">
          <span className="text-muted-foreground">Draft</span>
          <span data-testid="readiness-is-draft">
            {issue.isDraft ? 'Yes' : 'No'}
          </span>
        </div>
        <div className="flex items-center justify-between gap-2">
          <span className="text-muted-foreground">Can start</span>
          <span data-testid="readiness-can-start">
            {issue.canStart ? 'Yes' : 'No'}
          </span>
        </div>
        <div className="flex items-center justify-between gap-2">
          <span className="text-muted-foreground">Blocker</span>
          <span
            data-testid="readiness-blocker"
            data-blocker-kind={issue.blocker?.kind ?? 'none'}
            className="text-right"
          >
            {issue.blocker?.kind === 'draft'
              ? 'Still a draft'
              : issue.blocker?.kind === 'waiting-for'
                ? `Waiting for #${issue.blocker.issue.number}`
                : 'None'}
          </span>
        </div>
      </div>
    </CardSection>
  )
}