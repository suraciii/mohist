import type { PrDeliveryMetadata } from '../../../shared/lib/pr-delivery'
import { extractPrDeliveryMetadata } from '../../../shared/lib/pr-delivery'
import type { WorkflowTimeline, WorkflowTimelineTask } from '../../../entities/issue'

export interface PrDeliveryIndicatorProps {
  metadata: PrDeliveryMetadata
  className?: string
}

export function PrDeliveryIndicator({ metadata, className }: PrDeliveryIndicatorProps) {
  return (
    <a
      href={metadata.prUrl}
      target="_blank"
      rel="noopener noreferrer"
      data-testid="pr-delivery-indicator"
      data-pr-number={metadata.prNumber}
      data-pr-url={metadata.prUrl}
      className={
        'inline-flex items-center gap-1.5 rounded-md border border-purple-200 bg-purple-50 px-2.5 py-1 text-xs font-medium text-purple-800 hover:border-purple-300 hover:bg-purple-100 transition-colors' +
        (className ? ` ${className}` : '')
      }
    >
      <svg
        className="h-3.5 w-3.5 flex-shrink-0"
        viewBox="0 0 16 16"
        fill="currentColor"
        aria-hidden="true"
      >
        <path
          fillRule="evenodd"
          d="M7.177 3.073a.75.75 0 0 1 .646 0l6.5 3.25a.75.75 0 0 1 0 1.354l-6.5 3.25a.75.75 0 0 1-.646 0L1.177 7.677a.75.75 0 0 1 0-1.354l6.5-3.25ZM8 4.42 3.063 6.75 8 9.08l4.938-2.33L8 4.42ZM2.75 8.83l4.5 2.25v3.92l-4.5-2.25V8.83Zm6 6.17V11.08l4.5-2.25v3.92l-4.5 2.25Z"
          clipRule="evenodd"
        />
      </svg>
      <span>
        Merged via PR <span className="font-mono">#{metadata.prNumber}</span>
      </span>
      <svg
        className="h-3 w-3 flex-shrink-0 opacity-70"
        viewBox="0 0 16 16"
        fill="currentColor"
        aria-hidden="true"
      >
        <path
          fillRule="evenodd"
          d="M10.604 1h4.146a.25.25 0 0 1 .25.25v4.146a.25.25 0 0 1-.427.177L13.03 4.03 8.28 8.78a.75.75 0 0 1-1.06-1.06l4.75-4.75-1.543-1.543A.25.25 0 0 1 10.604 1ZM3.75 2A1.75 1.75 0 0 0 2 3.75v8.5C2 13.216 2.784 14 3.75 14h8.5A1.75 1.75 0 0 0 14 12.25v-3.5a.75.75 0 0 0-1.5 0v3.5a.25.25 0 0 1-.25.25h-8.5a.25.25 0 0 1-.25-.25v-8.5a.25.25 0 0 1 .25-.25h3.5a.75.75 0 0 0 0-1.5h-3.5Z"
          clipRule="evenodd"
        />
      </svg>
    </a>
  )
}

export interface PrDeliverySummaryProps {
  timeline: WorkflowTimeline | null | undefined
}

export function PrDeliverySummary({ timeline }: PrDeliverySummaryProps) {
  const metadata = findMergedPullRequestDeliveryMetadata(timeline)
  if (!metadata) return null
  return <PrDeliveryIndicator metadata={metadata} />
}

export function findPullRequestDeliveryMetadata(
  timeline: WorkflowTimeline | null | undefined,
): PrDeliveryMetadata | null {
  return findMergedPullRequestDeliveryMetadata(timeline)
}

export function findMergedPullRequestDeliveryMetadata(
  timeline: WorkflowTimeline | null | undefined,
): PrDeliveryMetadata | null {
  if (!timeline) return null
  let lastDeliveryMetadata: PrDeliveryMetadata | null = null
  let mergedMetadata: PrDeliveryMetadata | null = null
  for (const stage of timeline.stages) {
    for (const task of stage.tasks) {
      if (!isCompletedMergedPullRequestDeliveryTask(task)) continue
      const metadata = extractPrDeliveryMetadata(task.output)
      if (!metadata) continue
      lastDeliveryMetadata = metadata
      if (task.uses === 'mohist/merge-pull-request') {
        mergedMetadata = metadata
      }
    }
  }
  return mergedMetadata ?? lastDeliveryMetadata
}

export function findPublishViaPrMetadata(
  timeline: WorkflowTimeline | null | undefined,
): PrDeliveryMetadata | null {
  return findMergedPullRequestDeliveryMetadata(timeline)
}

export function isCompletedPullRequestDeliveryTask(task: WorkflowTimelineTask): boolean {
  return isCompletedMergedPullRequestDeliveryTask(task)
}

export function isCompletedMergedPullRequestDeliveryTask(task: WorkflowTimelineTask): boolean {
  return task.status === 'completed' && (
    task.uses === 'mohist/publish-via-pr' ||
    task.uses === 'mohist/merge-pull-request' ||
    task.uses === 'mohist/create-pull-request'
  )
}

export function isCompletedPublishViaPrTask(task: WorkflowTimelineTask): boolean {
  return task.status === 'completed' && task.uses === 'mohist/publish-via-pr'
}
