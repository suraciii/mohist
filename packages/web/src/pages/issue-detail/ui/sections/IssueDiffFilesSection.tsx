import { Button } from '@/shared/ui/components/button'
import type { IssueDiffResponse } from '../../../../entities/issue/model/git-changes'

export interface IssueDiffFilesSectionProps {
  diffData: IssueDiffResponse | undefined
  onViewFiles: () => void
}

export function IssueDiffFilesSection({ diffData, onViewFiles }: IssueDiffFilesSectionProps) {
  if (diffData?.available !== true) return null
  return (
    <div className="min-w-0 rounded-lg bg-card p-4" data-testid="diff-files-section">
      <div className="flex min-w-0 flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-foreground">
          <span className="min-w-0 break-words">
            <span className="font-medium text-card-foreground break-all" title={diffData.head}>{diffData.head}</span>
            {' → '}
            <span className="font-medium text-card-foreground break-all" title={diffData.base}>{diffData.base}</span>
          </span>
          <span className="text-muted-foreground/40">·</span>
          <span>{diffData.summary.filesChanged} files changed · +{diffData.summary.additions} -{diffData.summary.deletions}</span>
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
    </div>
  )
}
