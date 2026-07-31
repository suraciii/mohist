import { cn } from '@/shared/lib/utils'

export interface AttachmentResultAccepted {
  id: string
  name: string
  contentType?: string | null
  size: number
}

export interface AttachmentResultRejected {
  id: string
  name?: string
  reason: string
  message: string
}

export interface AttachmentResultsValue {
  accepted?: readonly AttachmentResultAccepted[] | null
  rejected?: readonly AttachmentResultRejected[] | null
}

export function AttachmentResults({ accepted = [], rejected = [], className }: AttachmentResultsValue & { className?: string }) {
  const acceptedItems = accepted ?? []
  const rejectedItems = rejected ?? []
  if (acceptedItems.length === 0 && rejectedItems.length === 0) return null

  return (
    <div data-testid="attachment-results" aria-label="Attachment results" className={cn('space-y-1.5', className)}>
      {acceptedItems.map((attachment) => (
        <div
          key={`accepted-${attachment.id}`}
          data-testid={`attachment-result-accepted-${attachment.id}`}
          className="flex min-w-0 items-center justify-between gap-3 rounded-md border border-success-border bg-success-subtle px-3 py-2 text-xs"
        >
          <span className="min-w-0 truncate font-medium text-success">{attachment.name}</span>
          <span className="shrink-0 text-success/80">Accepted - {formatSize(attachment.size)}</span>
        </div>
      ))}
      {rejectedItems.map((attachment) => (
        <div
          key={`rejected-${attachment.id}`}
          data-testid={`attachment-result-rejected-${attachment.id}`}
          className="flex min-w-0 items-start justify-between gap-3 rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs"
        >
          <span className="min-w-0">
            <span className="block truncate font-medium text-danger">{attachment.name || attachment.id}</span>
            <span className="block text-danger/80">{attachment.message}</span>
          </span>
          <span className="shrink-0 font-medium text-danger">Rejected: {attachment.reason}</span>
        </div>
      ))}
    </div>
  )
}

function formatSize(size: number) {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`
  return `${(size / 1024 / 1024).toFixed(1)} MB`
}
