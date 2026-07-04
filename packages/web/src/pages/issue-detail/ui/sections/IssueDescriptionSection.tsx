import { MarkdownReader } from '@/shared/ui'
import type { Issue } from '../../../../entities/issue'
import type { MarkdownAttachment } from '@/shared/ui/markdown-reader/MarkdownReader'

export interface IssueDescriptionSectionProps {
  issue: Pick<Issue, 'body'>
  resolveIssueAttachment: (id: string) => MarkdownAttachment | null
}

export function IssueDescriptionSection({ issue, resolveIssueAttachment }: IssueDescriptionSectionProps) {
  if (!issue.body) return null
  return (
    <div className="rounded-lg bg-card p-4" data-testid="description-section">
      <h2 className="text-sm font-semibold text-card-foreground mb-2">Description</h2>
      <MarkdownReader
        content={issue.body}
        mode="collapsible"
        collapsedHeight={600}
        baseHeadingLevel={2}
        resolveAttachment={resolveIssueAttachment}
      />
    </div>
  )
}
