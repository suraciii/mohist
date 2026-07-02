import { IssueStatus, WorkflowStage, type AttachmentInfo } from '../../../entities/issue'
import type { MarkdownAttachment } from '@/shared/ui/markdown-reader/MarkdownReader'

export const WORKFLOW_STAGE_LABELS: Partial<Record<WorkflowStage, string>> = {
  [WorkflowStage.Plan]: 'Plan',
  [WorkflowStage.Build]: 'Build',
  [WorkflowStage.Check]: 'Check',
  [WorkflowStage.Integrate]: 'Integrate',
}

export function stageToIssueStatus(stage: WorkflowStage | undefined): IssueStatus {
  if (!stage) return IssueStatus.Backlog
  return IssueStatus.InProgress
}

export function formatRelativeTime(iso: string): string {
  const diff = Math.max(0, Date.now() - new Date(iso).getTime())
  const seconds = Math.floor(diff / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  return `${hours}h ago`
}

export function formatStageName(stage: string | null | undefined): string {
  if (!stage) return '-'
  return stage
    .split(/[_-]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ')
}

export function attachmentFromMetadata(
  id: string,
  attachments: AttachmentInfo[] | undefined,
  url: string,
): MarkdownAttachment | null {
  const attachment = attachments?.find((item) => item.id === id)
  if (!attachment) return null
  return {
    url,
    contentType: attachment.contentType || 'application/octet-stream',
    fileName: attachment.fileName,
    size: attachment.size,
  }
}
