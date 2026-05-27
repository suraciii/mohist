import type { DiffFile, CommitEntry, IssueDiffResponse, IssueCommitsResponse } from '../../../shared/api/types'
import { formatTimeAgo } from '../../../shared/lib/format-time'
import { DiffViewer } from '../../issue-changed-files/ui/DiffViewer'
import { useCommitDiff } from '../../../entities/issue/api/queries'

function CommitRow({
  issueNumber,
  commit,
  expanded,
  onToggle,
}: {
  issueNumber: number
  commit: CommitEntry
  expanded: boolean
  onToggle: () => void
}) {
  const { data: diffData, isLoading, isError } = useCommitDiff(issueNumber, commit.hash, expanded)

  return (
    <div>
      <button
        onClick={onToggle}
        className="w-full flex items-center gap-3 text-sm py-1.5 px-2 rounded hover:bg-gray-50 transition-colors text-left"
      >
        <svg
          className={`h-3 w-3 text-gray-400 transition-transform flex-shrink-0 ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path
            fillRule="evenodd"
            d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
            clipRule="evenodd"
          />
        </svg>
        <span className="font-mono text-xs text-blue-600 flex-shrink-0">{commit.hash}</span>
        <span className="text-gray-700 truncate flex-1">{commit.message}</span>
        <span className="text-gray-400 text-xs flex-shrink-0">{formatTimeAgo(new Date(commit.date))}</span>
        <span className="text-green-600 text-xs font-medium flex-shrink-0">+{commit.additions}</span>
        <span className="text-red-500 text-xs font-medium flex-shrink-0">-{commit.deletions}</span>
      </button>
      {commit.files && commit.files.length > 0 && !expanded && (
        <div className="ml-5 flex flex-wrap gap-1 px-2 pb-1">
          {commit.files.slice(0, 5).map((f) => (
            <span key={f} className="inline-block rounded bg-gray-100 px-1.5 py-0.5 font-mono text-[10px] text-gray-500">
              {f}
            </span>
          ))}
          {commit.files.length > 5 && (
            <span className="inline-block rounded bg-gray-100 px-1.5 py-0.5 text-[10px] text-gray-400">
              +{commit.files.length - 5} more
            </span>
          )}
        </div>
      )}
      {expanded && (
        <div className="ml-5">
          {isLoading && (
            <div className="text-xs text-gray-400 py-2">Loading diff...</div>
          )}
          {isError && (
            <div className="text-xs text-red-500 py-2">Failed to load diff</div>
          )}
          {!isLoading && !isError && diffData?.available === false && (
            <div className="text-xs text-orange-500 py-2">{diffData.message}</div>
          )}
          {diffData?.available !== false && diffData?.diff && <DiffViewer diff={diffData.diff} />}
          {diffData?.available !== false && !diffData?.diff && diffData?.diff !== '' && (
            <div className="text-xs text-gray-400 py-2">Empty diff</div>
          )}
        </div>
      )}
    </div>
  )
}

interface ChangesPanelProps {
  diffData?: IssueDiffResponse
  commitsData?: IssueCommitsResponse
  diffTab: 'files' | 'commits'
  setDiffTab: (tab: 'files' | 'commits') => void
  expandedFiles: Set<string>
  setExpandedFiles: React.Dispatch<React.SetStateAction<Set<string>>>
  expandedCommits: Set<string>
  setExpandedCommits: React.Dispatch<React.SetStateAction<Set<string>>>
  issueNumber: number
  onCommitExpand?: (hash: string) => void
}

export function ChangesPanel({
  diffData,
  commitsData,
  diffTab,
  setDiffTab,
  expandedFiles,
  setExpandedFiles,
  expandedCommits,
  setExpandedCommits,
  issueNumber,
  onCommitExpand,
}: ChangesPanelProps) {
  const files = diffData?.available === true ? diffData.files : []
  const commits = commitsData?.available === true ? commitsData.commits : []

  const diffUnavailable = diffData?.available === false

  const worktreeRemoved = diffData?.reason === 'worktree_removed' || commitsData?.reason === 'worktree_removed'
  const branchMissing = diffData?.reason === 'branch_missing' || commitsData?.reason === 'branch_missing'
  const notStarted = diffData?.reason === 'not_started' || commitsData?.reason === 'not_started'

  const showUnavailable = notStarted
    ? <p className="text-sm text-gray-400">No changes yet</p>
    : worktreeRemoved
      ? <p className="text-sm text-orange-600">Changes unavailable — workspace removed</p>
      : branchMissing
        ? <p className="text-sm text-orange-600">Changes unavailable — branch missing</p>
        : diffUnavailable
          ? <p className="text-sm text-orange-600">{diffData?.message ?? 'Failed to load changes'}</p>
          : null

  const available = diffData?.available === true && commitsData?.available === true

  const baseHeadLabel = available && diffData?.base && diffData?.head
    ? `${diffData.base} → ${diffData.head}`
    : null

  const summary = available ? (diffData?.summary || commitsData?.summary) : null
  const commitCount = commitsData?.summary?.commits ?? 0

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4">
      {available && summary ? (
        <div className="flex items-center gap-3 mb-3 text-xs text-gray-500">
          {baseHeadLabel && <span className="font-mono font-medium">{baseHeadLabel}</span>}
          {baseHeadLabel && <span>·</span>}
          <span>{summary.filesChanged} files changed</span>
          <span>·</span>
          <span>{commitCount} commit{commitCount !== 1 ? 's' : ''}</span>
          <span>·</span>
          <span className="text-green-600">+{summary.additions}</span>
          <span className="text-red-500">-{summary.deletions}</span>
          <span>·</span>
          <span className="text-gray-400">Worktree retained</span>
        </div>
      ) : null}

      {showUnavailable || (
        <>
          <div className="flex items-center gap-1 mb-3 border-b border-gray-100">
            <button
              onClick={() => setDiffTab('files')}
              className={`px-3 py-1.5 text-sm font-medium transition-colors border-b-2 -mb-px ${
                diffTab === 'files'
                  ? 'border-blue-600 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700'
              }`}
            >
              Files{files.length > 0 ? ` (${files.length})` : ''}
            </button>
            <button
              onClick={() => setDiffTab('commits')}
              className={`px-3 py-1.5 text-sm font-medium transition-colors border-b-2 -mb-px ${
                diffTab === 'commits'
                  ? 'border-blue-600 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700'
              }`}
            >
              Commits{commits.length > 0 ? ` (${commits.length})` : ''}
            </button>
          </div>

          {diffTab === 'files' && (
            <div className="space-y-1">
              {files.length === 0 ? (
                <p className="text-sm text-gray-400">No file changes yet.</p>
              ) : (
                files.map((f: DiffFile, i: number) => (
                  <div key={i}>
                    <button
                      onClick={() => {
                        setExpandedFiles((prev) => {
                          const next = new Set(prev)
                          if (next.has(f.file)) {
                            next.delete(f.file)
                          } else {
                            next.add(f.file)
                          }
                          return next
                        })
                      }}
                      className="w-full flex items-center gap-2 text-sm py-1 px-2 rounded hover:bg-gray-50 transition-colors text-left"
                    >
                      <svg
                        className={`h-3 w-3 text-gray-400 transition-transform flex-shrink-0 ${expandedFiles.has(f.file) ? 'rotate-90' : ''}`}
                        viewBox="0 0 20 20"
                        fill="currentColor"
                      >
                        <path
                          fillRule="evenodd"
                          d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
                          clipRule="evenodd"
                        />
                      </svg>
                      <span className="text-gray-700 font-mono text-xs truncate flex-1">
                        {f.file}
                      </span>
                      <span className="text-green-600 text-xs font-medium">+{f.additions}</span>
                      <span className="text-red-500 text-xs font-medium">-{f.deletions}</span>
                    </button>
                    {expandedFiles.has(f.file) && f.diff && (
                      <div className="ml-5">
                        <DiffViewer diff={f.diff} />
                      </div>
                    )}
                  </div>
                ))
              )}
            </div>
          )}

          {diffTab === 'commits' && (
            <div className="space-y-1">
              {commits.length === 0 ? (
                <p className="text-sm text-gray-400">No commits yet.</p>
              ) : (
                commits.map((c: CommitEntry) => (
                  <CommitRow
                    key={c.hash}
                    issueNumber={issueNumber}
                    commit={c}
                    expanded={expandedCommits.has(c.hash)}
                    onToggle={() => {
                      setExpandedCommits((prev) => {
                        const next = new Set(prev)
                        if (next.has(c.hash)) {
                          next.delete(c.hash)
                        } else {
                          next.add(c.hash)
                        }
                        return next
                      })
                      onCommitExpand?.(c.hash)
                    }}
                  />
                ))
              )}
            </div>
          )}
        </>
      )}
    </div>
  )
}