import { CardSection } from '@/shared/ui/components/card-section'
import { IssueModelSelector } from '../../../../features/select-issue-model'
import { IssuePrerequisitePicker, type IssuePrerequisitePickerProps } from '../../../../entities/issue'
import type { Issue, IssuePrerequisiteSummary, IssueStartBlocker } from '../../../../entities/issue'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'

export type IssueConfigurationCardIssue = Pick<
  Issue,
  'number' | 'model' | 'stageModels' | 'workflowRunId' | 'workflowProfileId' | 'canStart' | 'blocker'
> & {
  prerequisites?: IssuePrerequisiteSummary[]
  isBacklog: boolean
}

export interface IssueConfigurationCardProps {
  issue: IssueConfigurationCardIssue
  projectId: string
  mutations: Pick<
    IssueDetailMutations,
    'addPrerequisiteMutation' | 'removePrerequisiteMutation'
  >
  unframed?: boolean
  prerequisitePickerIssuesHook?: IssuePrerequisitePickerProps['issuesHook']
}

export function IssueConfigurationCard({
  issue,
  projectId,
  mutations,
  unframed = false,
  prerequisitePickerIssuesHook,
}: IssueConfigurationCardProps) {
  const { addPrerequisiteMutation, removePrerequisiteMutation } = mutations
  const prerequisiteNumbers = issue.prerequisites?.map(p => p.number) ?? []
  const blocker: IssueStartBlocker | null = issue.blocker ?? null

  const content = (
    <div className="space-y-4">
      <IssueModelSelector
        issueNumber={issue.number}
        workflowRunId={issue.workflowRunId}
        workflowProfileId={issue.workflowProfileId}
        currentModel={issue.model}
        currentStageModels={issue.stageModels}
      />

      {issue.isBacklog && (
        <div className="border-t border-border/60 pt-4" data-testid="prerequisite-configuration-controls">
          <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Prerequisites</h3>
          <IssuePrerequisitePicker
            projectId={projectId}
            mode="live"
            selected={prerequisiteNumbers}
            selectedIssueSummaries={issue.prerequisites ?? []}
            excludeNumbers={[issue.number, ...prerequisiteNumbers]}
            canStart={issue.canStart}
            blocker={blocker}
            disabled={addPrerequisiteMutation.isPending || removePrerequisiteMutation.isPending}
            issuesHook={prerequisitePickerIssuesHook}
            onAdd={(n) => addPrerequisiteMutation.mutateAsync(n).then(() => undefined)}
            onRemove={(n) => removePrerequisiteMutation.mutateAsync(n).then(() => undefined)}
            errorMessage={
              addPrerequisiteMutation.error
                ? (addPrerequisiteMutation.error as Error).message?.includes('circular')
                  ? 'Circular prerequisite: this would create a cycle'
                  : (addPrerequisiteMutation.error as Error).message
                : removePrerequisiteMutation.error
                  ? (removePrerequisiteMutation.error as Error).message
                  : null
            }
          />
        </div>
      )}
    </div>
  )
  if (unframed) return content
  return <CardSection title="Configuration">{content}</CardSection>
}
