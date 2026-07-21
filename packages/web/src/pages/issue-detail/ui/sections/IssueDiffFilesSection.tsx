import { Button } from '@/shared/ui/components/button'
import type { IssueDiffResponse } from '../../../../entities/issue'

export interface IssueDiffFilesSectionProps {
  diffData: IssueDiffResponse | undefined
  isLoading: boolean
  error: unknown
  commitsUnavailable: boolean
  onViewFiles: () => void
}

export function IssueDiffFilesSection({
  diffData,
  isLoading,
  error,
  commitsUnavailable,
  onViewFiles,
}: IssueDiffFilesSectionProps) {
  let content

  if (isLoading) {
    content = <p className="text-sm text-muted-foreground">Loading changes...</p>
  } else if (error) {
    content = (
      <p className="text-sm text-danger">
        Changes could not be loaded. Changed files and diff cannot be inspected.
      </p>
    )
  } else if (!diffData || diffData.available === false) {
    const availabilityMessage = diffData?.message?.replace(/[.!?]+$/, '')
    content = (
      <p className="text-sm text-muted-foreground" data-testid="changes-unavailable">
        {availabilityMessage ? `${availabilityMessage}. ` : ''}
        Changed files and diff cannot be inspected{commitsUnavailable ? ', and commits cannot be inspected' : ''}.
      </p>
    )
  } else {
    const fileCount = diffData.summary.filesChanged
    const additions = diffData.summary.additions
    const deletions = diffData.summary.deletions
    const scaleLabel = fileCount === 0
      ? 'No files changed yet'
      : `${fileCount} file${fileCount === 1 ? '' : 's'} changed · +${additions} −${deletions}`
    content = (
      <div
        data-testid="diff-files-summary"
        className="flex min-w-0 flex-wrap items-center justify-between gap-3"
      >
        <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
          <span className="min-w-0 break-words">
            <span className="font-medium text-foreground break-all" title={diffData.head} data-testid="changes-head">{diffData.head}</span>
            {' → '}
            <span className="font-medium text-foreground break-all" title={diffData.base} data-testid="changes-base">{diffData.base}</span>
          </span>
          <span className="text-muted-foreground/40" aria-hidden="true">·</span>
          <span data-testid="diff-files-scale">{scaleLabel}</span>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={onViewFiles}
          className="border-info-border text-info hover:border-info hover:text-info"
        >
          View files
        </Button>
      </div>
    )
  }

  return (
    <section
      className="min-w-0"
      data-testid="diff-files-section"
      data-tier-weight="reading-flow"
      data-collapsed="true"
      aria-label="Change and diff summary"
    >
      <h2 className="text-sm font-semibold text-foreground mb-2">Changes</h2>
      {content}
    </section>
  )
}
