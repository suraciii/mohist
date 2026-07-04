import { Button } from '@/shared/ui/components/button'
import type { IssueCommitsResponse } from '../../../../entities/issue/model/git-changes'
import { formatRelativeTime } from '../../model/format'

export interface IssueCommitsSectionProps {
  commitsData: IssueCommitsResponse | undefined
  onViewAllCommits: () => void
}

export function IssueCommitsSection({ commitsData, onViewAllCommits }: IssueCommitsSectionProps) {
  if (commitsData?.available !== true) return null
  return (
    <div className="rounded-lg bg-card p-4" data-testid="commits-section">
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-sm font-semibold text-card-foreground">
          Commits ({commitsData.summary.commits})
        </h2>
        <Button
          variant="outline"
          size="sm"
          onClick={onViewAllCommits}
          className="border-info-border text-info hover:border-info hover:text-info"
        >
          View all commits
        </Button>
      </div>
      {commitsData.commits.length === 0 ? (
        <p className="text-sm text-muted-foreground">No commits yet.</p>
      ) : (
        <div className="space-y-2">
          {commitsData.commits.slice(0, 5).map((commit) => (
            <div
              key={commit.hash}
              className="flex items-center justify-between text-sm group"
            >
              <div className="flex items-center gap-3 flex-1 min-w-0">
                <code className="text-xs text-muted-foreground font-mono shrink-0">{commit.shortHash}</code>
                <span className="text-card-foreground truncate">{commit.message}</span>
              </div>
              <span className="text-xs text-muted-foreground ml-3 shrink-0">{formatRelativeTime(commit.date)}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
