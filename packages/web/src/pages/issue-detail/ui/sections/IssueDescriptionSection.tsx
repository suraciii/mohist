import { MarkdownReader } from '@/shared/ui'
import type { MarkdownAttachment } from '@/shared/ui/markdown-reader/MarkdownReader'

export interface IssueDescriptionSectionProps {
  description: string
  resolveIssueAttachment: (id: string) => MarkdownAttachment | null
}

const PREVIEW_MIN_CHARS = 320
const PREVIEW_MAX_CHARS = 220

function buildPreviewHint(body: string): string | null {
  const stripped = body
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/`[^`]*`/g, ' ')
    .replace(/!\[[^\]]*]\([^)]*\)/g, ' ')
    .replace(/\[([^\]]*)]\([^)]*\)/g, '$1')
    .replace(/^#{1,6}\s+/gm, '')
    .replace(/^>\s?/gm, '')
    .replace(/\s+/g, ' ')
    .trim()
  if (stripped.length <= PREVIEW_MIN_CHARS) return null
  const truncated = stripped.slice(0, PREVIEW_MAX_CHARS)
  const lastSpace = truncated.lastIndexOf(' ')
  return `${(lastSpace > 80 ? truncated.slice(0, lastSpace) : truncated).trimEnd()}…`
}

export function IssueDescriptionSection({ description, resolveIssueAttachment }: IssueDescriptionSectionProps) {
  if (!description.trim()) return null
  const previewHint = buildPreviewHint(description)
  return (
    <section
      data-testid="description-section"
      data-tier-weight="reading-flow"
      aria-label="Issue description"
    >
      <h2 className="text-sm font-semibold text-foreground mb-3">Description</h2>
      {previewHint && (
        <p
          data-testid="description-preview-hint"
          data-collapsed-hint="true"
          className="mb-3 text-sm text-muted-foreground leading-relaxed"
        >
          {previewHint}
        </p>
      )}
      <MarkdownReader
        content={description}
        mode="collapsible"
        collapsedHeight={600}
        baseHeadingLevel={2}
        resolveAttachment={resolveIssueAttachment}
      />
    </section>
  )
}
