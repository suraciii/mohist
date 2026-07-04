import { CardSection } from '@/shared/ui/components/card-section'
import type { IssuePrerequisiteSummary } from '../../../../entities/issue'

export interface IssuePrerequisitesCardProps {
  prerequisites: IssuePrerequisiteSummary[]
}

export function IssuePrerequisitesCard({ prerequisites }: IssuePrerequisitesCardProps) {
  return (
    <CardSection title="Start Prerequisites" tone="amber">
      <div className="space-y-2">
        {prerequisites.map((prereq) => (
          <div key={prereq.number} className="flex items-center justify-between text-sm gap-2">
            <span className="text-warning truncate">
              <span className="font-mono">#{prereq.number}</span> {prereq.title}
            </span>
            {prereq.completed ? (
              <span className="inline-flex items-center gap-1 text-xs font-medium text-success bg-success-subtle border border-success-border px-1.5 py-0.5 rounded shrink-0">
                Completed
              </span>
            ) : (
              <span className="inline-flex items-center gap-1 text-xs font-medium text-warning bg-warning-subtle border border-warning-border px-1.5 py-0.5 rounded shrink-0">
                Waiting
              </span>
            )}
          </div>
        ))}
      </div>
    </CardSection>
  )
}
