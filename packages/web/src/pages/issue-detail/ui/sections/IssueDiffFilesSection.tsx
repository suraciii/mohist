import { Button } from '@/shared/ui/components/button'
import type { IssueDiffResponse } from '../../../../entities/issue/model/git-changes'

export interface IssueDiffFilesSectionProps {
  diffData: IssueDiffResponse | undefined
  onViewFiles: () => void
}

export function IssueDiffFilesSection({ diffData, onViewFiles }: IssueDiffFilesSectionProps) {
  if (diffData?.available !== true) return null
  return (
    <div className="min-w-0 rounded-lg bg-white p-4" data-testid="diff-files-section">
      <div className="flex min-w-0 flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-sm text-gray-500">
          <span className="min-w-0 break-words">
            <span className="font-medium text-gray-700 break-all" title={diffData.head}>{diffData.head}</span>
            {' → '}
            <span className="font-medium text-gray-700 break-all" title={diffData.base}>{diffData.base}</span>
          </span>
          <span className="text-gray-300">·</span>
          <span>{diffData.summary.filesChanged} files changed · +{diffData.summary.additions} -{diffData.summary.deletions}</span>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={onViewFiles}
          className="border-blue-200 text-blue-600 hover:border-blue-300 hover:text-blue-700"
        >
          View files
        </Button>
      </div>
    </div>
  )
}
