import { useState } from 'react'
import { AlertTriangleIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import type { TimelineItem } from '../../../entities/session'

interface SessionErrorsEvidenceProps {
  failureCategory: string | null | undefined
  toolErrorCount: number | null | undefined
  failureReason: string | null | undefined
  failedItems: TimelineItem[]
  locate: (sourceId: string) => void
}
const ERROR_SURFACE_CLASS = 'bg-danger-subtle text-danger border-danger-border'

const FAILURE_CATEGORY_LABELS: Record<string, string> = {
  cancelled: 'Cancelled',
  compaction: 'Context compaction',
  context_limit: 'Context limit',
  timeout: 'Timed out',
}

function formatFailureCategory(category: string): string {
  if (isExecutionUnavailableCategory(category)) return 'Execution unavailable'
  return FAILURE_CATEGORY_LABELS[category.toLowerCase()] ?? 'Execution failure'
}

function isExecutionUnavailableCategory(category: string | null | undefined): boolean {
  const normalized = category?.toLowerCase() ?? ''
  return (
    normalized === 'runtime-unavailable' ||
    normalized === 'execution-unavailable' ||
    normalized === 'external-agent-unavailable'
  )
}

export function SessionErrorsEvidence({
  failureCategory,
  toolErrorCount,
  failureReason,
  failedItems,
  locate,
}: SessionErrorsEvidenceProps) {
  const hasFailureCategory = failureCategory != null && failureCategory !== ''
  const hasFailureReason = failureReason != null && failureReason !== ''
  const effectiveToolErrorCount = Math.max(toolErrorCount ?? 0, failedItems.length)
  const hasToolErrors = effectiveToolErrorCount > 0
  const hasFailureEvidence = hasFailureCategory || hasFailureReason
  const executionUnavailable = isExecutionUnavailableCategory(failureCategory)
  const [currentIndex, setCurrentIndex] = useState(0)
  if (!hasFailureEvidence && !hasToolErrors) return null

  const activate = () => {
    const item = failedItems[currentIndex]
    if (item) locate(item.sourceIds[0] ?? item.id)
  }
  const next = () => {
    if (failedItems.length === 0) return
    const nextIndex = (currentIndex + 1) % failedItems.length
    setCurrentIndex(nextIndex)
    const item = failedItems[nextIndex]
    locate(item.sourceIds[0] ?? item.id)
  }

  return (
    <div
      data-testid="session-errors-region"
      data-failure-category={failureCategory ?? ''}
      data-tool-error-count={String(effectiveToolErrorCount)}
      className={`border-b border-border px-4 py-2 ${ERROR_SURFACE_CLASS}`}
      onClick={activate}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          activate()
        }
      }}
      tabIndex={failedItems.length > 0 ? 0 : undefined}
      role={failedItems.length > 0 ? 'button' : hasFailureEvidence ? 'status' : undefined}
      aria-live={failedItems.length > 0 ? undefined : 'polite'}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
        <span className="inline-flex items-center gap-1 font-semibold">
          <AlertTriangleIcon className="h-3.5 w-3.5" aria-hidden="true" />
          {executionUnavailable
            ? 'Execution unavailable'
            : hasFailureEvidence || failedItems.length > 0
              ? 'Execution failed'
              : 'Tool errors detected'}
        </span>
        {hasFailureCategory && (
          <span
            className="inline-flex items-center rounded-full border border-danger-border bg-danger-subtle text-danger px-2 py-0.5 text-[10px] font-semibold"
            data-testid="session-errors-region-category"
          >
            {formatFailureCategory(failureCategory)}
          </span>
        )}
        {hasToolErrors && (
          <span className="inline-flex items-center gap-1" data-testid="session-errors-region-tool-count">
            <span className="text-danger font-medium">{effectiveToolErrorCount}</span>
            <span>tool {effectiveToolErrorCount === 1 ? 'error' : 'errors'}</span>
          </span>
        )}
        {failureReason && (
          <span
            className="text-danger truncate max-w-[300px]"
            title={failureReason}
            data-testid="session-errors-region-reason"
          >
            {failureReason}
          </span>
        )}
        {executionUnavailable && (
          <span data-testid="session-errors-region-next-action" className="text-danger">
            Wait for the configured runtime or provider to recover, then retry the launch from the Agent.
          </span>
        )}
        {failedItems.length > 1 && (
          <Button
            type="button"
            variant="link"
            size="sm"
            data-testid="session-errors-region-next-error"
            onClick={(event) => {
              event.stopPropagation()
              next()
            }}
            className="h-auto p-0 text-xs text-danger"
          >
            Next error
          </Button>
        )}
      </div>
    </div>
  )
}
