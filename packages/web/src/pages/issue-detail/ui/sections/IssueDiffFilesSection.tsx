import { Button } from '@/shared/ui/components/button'
import type { IssueDiffResponse } from '../../../../entities/issue'

export interface IssueDiffFilesSectionProps {
  diffData: IssueDiffResponse | undefined
  onViewFiles: () => void
}

export function IssueDiffFilesSection({ diffData, onViewFiles }: IssueDiffFilesSectionProps) {
  if (diffData?.available !== true) return null
  const fileCount = diffData.summary.filesChanged
  const additions = diffData.summary.additions
  const deletions = diffData.summary.deletions
  const scaleLabel = fileCount === 0
    ? 'No files changed yet'
    : `${fileCount} file${fileCount === 1 ? '' : 's'} changed · +${additions} −${deletions}`
  return (
    <section
      data-testid="diff-files-section"
      data-tier-weight="reading-flow"
      data-collapsed="true"
      aria-label="Change and diff summary"
    >
      <h2 className="text-sm font-semibold text-foreground mb-2">Changes</h2>
      <div
        data-testid="diff-files-summary"
        className="flex min-w-0 flex-wrap items-center justify-between gap-3"
      >
        <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
          <span className="min-w-0 break-words">
            <span className="font-medium text-foreground break-all" title={diffData.head}>{diffData.head}</span>
            {' → '}
            <span className="font-medium text-foreground break-all" title={diffData.base}>{diffData.base}</span>
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
    </section>
  )
}
