import { MarkdownReader } from '@/shared/ui'

export interface ArtifactTextContentProps {
  content: string
  contentType?: string | null
}

export function ArtifactTextContent({ content, contentType }: ArtifactTextContentProps) {
  if (contentType === 'text/markdown') {
    return <MarkdownReader content={content} baseHeadingLevel={2} />
  }

  return (
    <pre className="min-w-0 max-w-full whitespace-pre-wrap break-words [overflow-wrap:anywhere] rounded-md border bg-gray-50 p-3 font-mono text-xs text-gray-700">
      {content}
    </pre>
  )
}
