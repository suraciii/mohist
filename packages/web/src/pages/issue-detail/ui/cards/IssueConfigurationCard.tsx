import { useState } from 'react'
import { CardSection } from '@/shared/ui/components/card-section'
import { Input } from '@/shared/ui/components/input'
import { Button } from '@/shared/ui/components/button'
import { IssueModelSelector } from '../../../../features/select-issue-model'
import type { Issue, IssuePrerequisiteSummary } from '../../../../entities/issue'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'

export type IssueConfigurationCardIssue = Pick<
  Issue,
  'number' | 'model' | 'stageModels' | 'prerequisites'
> & {
  isBacklog: boolean
}

export interface IssueConfigurationCardProps {
  issue: IssueConfigurationCardIssue
  mutations: Pick<
    IssueDetailMutations,
    'addPrerequisiteMutation' | 'removePrerequisiteMutation'
  >
}

export function IssueConfigurationCard({ issue, mutations }: IssueConfigurationCardProps) {
  const { addPrerequisiteMutation, removePrerequisiteMutation } = mutations
  const [prereqInput, setPrereqInput] = useState('')
  const [prereqError, setPrereqError] = useState<string | null>(null)

  const handleAdd = () => {
    const num = parseInt(prereqInput, 10)
    if (isNaN(num) || num === issue.number) {
      setPrereqError('Enter a valid issue number')
      return
    }
    setPrereqError(null)
    addPrerequisiteMutation.mutate(num)
    setPrereqInput('')
  }

  return (
    <CardSection title="Configuration">
      <div className="space-y-4">
        <IssueModelSelector issueNumber={issue.number} currentModel={issue.model} currentStageModels={issue.stageModels} />

        {issue.isBacklog && (
          <div className="border-t border-border/60 pt-4" data-testid="prerequisite-configuration-controls">
            <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Prerequisites</h3>
            <div className="flex gap-2">
              <Input
                type="number"
                value={prereqInput}
                onChange={(e) => {
                  setPrereqInput(e.target.value)
                  setPrereqError(null)
                }}
                placeholder="Issue #"
                className="min-w-0 flex-1"
              />
              <Button
                onClick={handleAdd}
                disabled={!prereqInput || addPrerequisiteMutation.isPending}
              >
                {addPrerequisiteMutation.isPending ? 'Adding...' : 'Add'}
              </Button>
            </div>
            {prereqError && (
              <p className="mt-1 text-xs text-red-600">{prereqError}</p>
            )}
            {addPrerequisiteMutation.error && (
              <p className="mt-1 text-xs text-red-600">
                {(addPrerequisiteMutation.error as Error).message?.includes('circular')
                  ? 'Circular prerequisite: this would create a cycle'
                  : (addPrerequisiteMutation.error as Error).message}
              </p>
            )}
            {issue.prerequisites && issue.prerequisites.length > 0 && (
              <div className="mt-3 pt-3 border-t border-border/60">
                <p className="text-xs text-muted-foreground mb-2">Remove prerequisite:</p>
                <div className="flex flex-wrap gap-1">
                  {issue.prerequisites.map((prereq: IssuePrerequisiteSummary) => (
                    <Button
                      key={prereq.number}
                      variant="secondary"
                      size="xs"
                      onClick={() => removePrerequisiteMutation.mutate(prereq.number)}
                      disabled={removePrerequisiteMutation.isPending}
                    >
                      #{prereq.number}
                      <span className="text-muted-foreground">×</span>
                    </Button>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </div>
    </CardSection>
  )
}