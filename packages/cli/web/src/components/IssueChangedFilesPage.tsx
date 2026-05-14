import { useState, useMemo, useEffect, useRef, useCallback } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useIssue, useIssueDiff, useIssueCommits, useCommitDiff } from '../hooks/useQueries'
import { NotFoundPage } from './NotFoundPage'
import { statusBadge, statusLabel } from '../lib/status-badge'
import { parseDiff } from '../lib/diffModel'
import { ChangedFilesTree, UnifiedDiffPane, SplitDiffPane, RawPatchPane, FullFilePane, DiffSearchPane, FileStatusBadge } from './issue-changed-files'
import type { FileBlock } from '../lib/diffModel'
import type { IssueCommitsResponse, CommitEntry } from '../lib/types'

type ExpandState = 'all' | 'none' | 'mixed'
type DiffMode = 'unified' | 'split'
type ReaderMode = 'diff' | 'raw' | 'full' | 'search'

const STORAGE_KEY = (issueNumber: number) => `issue-files-reader-${issueNumber}`

interface ReaderState {
  selectedFilePath: string | null
  diffMode: DiffMode
  readerMode: ReaderMode
  activeHunkIndex: number
  scrollTop: number
}

function loadReaderState(issueNumber: number): ReaderState {
  try {
    const stored = sessionStorage.getItem(STORAGE_KEY(issueNumber))
    if (stored) {
      return JSON.parse(stored)
    }
  } catch {}
  return {
    selectedFilePath: null,
    diffMode: 'unified',
    readerMode: 'diff',
    activeHunkIndex: 0,
    scrollTop: 0,
  }
}

function saveReaderState(issueNumber: number, state: ReaderState) {
  try {
    sessionStorage.setItem(STORAGE_KEY(issueNumber), JSON.stringify(state))
  } catch {}
}

function formatRelativeTime(iso: string): string {
  const diff = Math.max(0, Date.now() - new Date(iso).getTime())
  const seconds = Math.floor(diff / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  return `${hours}h ago`
}

export function IssueChangedFilesPage() {
  const { number } = useParams<{ number: string }>()
  const navigate = useNavigate()
  const issueNumber = parseInt(number ?? '0', 10)

  const { data: issue, isLoading: issueLoading, isError: issueError } = useIssue(issueNumber)
  const { data: diffData } = useIssueDiff(issueNumber)
  const { data: commitsData } = useIssueCommits(issueNumber)

  const [selectedFile, setSelectedFile] = useState<FileBlock | null>(null)
  const [expandState, setExpandState] = useState<ExpandState>('none')
  const [diffMode, setDiffMode] = useState<DiffMode>('unified')
  const [readerMode, setReaderMode] = useState<ReaderMode>('diff')
  const [activeHunkIndex, setActiveHunkIndex] = useState(0)
  const [commitMode, setCommitMode] = useState(false)
  const [selectedCommit, setSelectedCommit] = useState<CommitEntry | null>(null)
  const diffPaneRef = useRef<HTMLDivElement>(null)

  const commitHash = selectedCommit?.hash ?? ''
  const { data: commitDiffData } = useCommitDiff(issueNumber, commitHash, commitMode && !!commitHash)

  const parsedBlocks = useMemo(() => {
    if (commitMode && commitDiffData?.diff) {
      return parseDiff(commitDiffData.diff)
    }
    if (!diffData?.files?.length) return []
    return parseDiff(diffData.files.map(f => f.diff).join('\n'))
  }, [diffData?.files, commitDiffData?.diff, commitMode])

  const savedState = useMemo(() => loadReaderState(issueNumber), [issueNumber])

  useEffect(() => {
    if (!commitMode) {
      setDiffMode(savedState.diffMode)
      setReaderMode(savedState.readerMode)
      setActiveHunkIndex(savedState.activeHunkIndex)
    }
  }, [savedState, commitMode])

  useEffect(() => {
    if (!commitMode && parsedBlocks.length > 0 && savedState.selectedFilePath) {
      const found = parsedBlocks.find(b => b.newPath === savedState.selectedFilePath || b.oldPath === savedState.selectedFilePath)
      if (found) {
        setSelectedFile(found)
        requestAnimationFrame(() => {
          if (diffPaneRef.current) {
            diffPaneRef.current.scrollTop = savedState.scrollTop
          }
        })
      }
    }
  }, [parsedBlocks, savedState.selectedFilePath, commitMode])

  useEffect(() => {
    if (!commitMode) {
      saveReaderState(issueNumber, {
        selectedFilePath: selectedFile?.newPath ?? selectedFile?.oldPath ?? null,
        diffMode,
        readerMode,
        activeHunkIndex,
        scrollTop: diffPaneRef.current?.scrollTop ?? 0,
      })
    }
  }, [selectedFile, diffMode, readerMode, activeHunkIndex, issueNumber, commitMode])

  useEffect(() => {
    if (!commitMode) {
      setActiveHunkIndex(0)
    }
  }, [selectedFile, commitMode])

  if (issueError) {
    return <NotFoundPage />
  }

  if (issueLoading || !issue) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading...</div>
      </div>
    )
  }

  const diffAvailable = diffData?.available === true
  const worktreeRemoved = diffData?.reason === 'worktree_removed' || commitsData?.reason === 'worktree_removed'
  const branchMissing = diffData?.reason === 'branch_missing' || commitsData?.reason === 'branch_missing'
  const notStarted = diffData?.reason === 'not_started' || commitsData?.reason === 'not_started'
  const isBehind = !!diffData && diffData.available && diffData.behind > 0

  const unavailableMessage = notStarted
    ? 'No changes yet'
    : worktreeRemoved
      ? 'Changes unavailable — workspace removed'
      : branchMissing
        ? 'Changes unavailable — branch missing'
        : diffData?.available === false
          ? diffData.message ?? 'Failed to load changes'
          : null

  const handleExpandAll = () => setExpandState('all')
  const handleCollapseAll = () => setExpandState('none')

  const handleSelectFile = (block: FileBlock) => {
    setSelectedFile(block)
    setActiveHunkIndex(0)
  }

  const handlePrevHunk = useCallback(() => {
    setActiveHunkIndex(prev => Math.max(0, prev - 1))
  }, [])

  const handleNextHunk = useCallback(() => {
    setActiveHunkIndex(prev => Math.min((selectedFile?.hunkCount ?? 1) - 1, prev + 1))
  }, [selectedFile?.hunkCount])

  const handleToggleDiffMode = () => {
    setDiffMode(prev => prev === 'unified' ? 'split' : 'unified')
    setActiveHunkIndex(0)
  }

  const handleExitCommitMode = () => {
    setCommitMode(false)
    setSelectedCommit(null)
    setActiveHunkIndex(0)
  }

  const handleSelectCommit = (commit: CommitEntry) => {
    setSelectedCommit(commit)
    setCommitMode(true)
    setActiveHunkIndex(0)
  }

  const totalHunks = selectedFile?.hunkCount ?? 0
  const commits = (commitsData as IssueCommitsResponse | undefined)?.commits ?? []

  return (
    <div className="flex-1 overflow-hidden flex flex-col">
      <div className="max-w-4xl mx-auto px-4 sm:px-6 py-4 w-full">
        <button
          onClick={() => navigate(`/issue/${issueNumber}`)}
          className="mb-4 inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 transition-colors"
        >
          <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
            <path
              fillRule="evenodd"
              d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
              clipRule="evenodd"
            />
          </svg>
          Back to issue
        </button>

        <div className="mb-4">
          <div className="flex items-center gap-2 mb-1">
            <span className="text-sm font-mono text-gray-400">#{issue.number}</span>
            <span
              className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${statusBadge(issue.status)}`}
            >
              {statusLabel(issue.status)}
            </span>
          </div>
          <h1 className="text-2xl font-bold text-gray-900">{issue.title}</h1>
        </div>

        {unavailableMessage ? (
          <div className="rounded-lg border border-orange-200 bg-orange-50 p-6">
            <p className="text-sm text-orange-700">{unavailableMessage}</p>
          </div>
        ) : diffAvailable && diffData ? (
          <div className="rounded-lg border border-gray-200 bg-white p-4 mb-4">
            <div className="flex items-center gap-3 text-sm">
              <span className="text-gray-500">
                <span className="font-medium text-gray-700">{diffData.head}</span>
                {' wants to merge into '}
                <span className="font-medium text-gray-700">{diffData.base}</span>
              </span>
              <span className="text-gray-300">·</span>
              <span className="text-gray-500">
                <span className="font-medium text-gray-700">{diffData.ahead}</span> commits ahead
              </span>
              {diffData.behind > 0 && (
                <>
                  <span className="text-gray-300">·</span>
                  <span className="text-gray-500">
                    <span className="font-medium text-gray-700">{diffData.behind}</span> behind
                  </span>
                </>
              )}
              <span className="text-gray-300">·</span>
              <span className="text-gray-500">
                <span className="font-medium text-gray-700">{diffData.summary.filesChanged}</span> files changed
              </span>
              <span className="text-gray-300">·</span>
              <span className="text-green-600">+{diffData.summary.additions}</span>
              <span className="text-red-500">-{diffData.summary.deletions}</span>
              <span className="text-gray-300">·</span>
              <span className="text-xs text-gray-400 capitalize">{issue.stage} · {issue.status}</span>
            </div>
            <div className="mt-2 flex items-center gap-3 text-xs text-gray-400">
              <span>showing merge-base → {diffData.head}</span>
            </div>
          </div>
        ) : null}

        {isBehind && (
          <div className="rounded-lg border border-blue-200 bg-blue-50 p-4 mb-4">
            <p className="text-sm text-blue-700">
              This branch is {diffData!.behind} commit{(diffData!.behind as number) > 1 ? 's' : ''} behind {diffData!.base}.
              Files changed shows only changes introduced by {diffData!.head} from the merge base, matching GitHub PR behavior.
            </p>
          </div>
        )}
      </div>

      {diffAvailable && parsedBlocks.length > 0 && (
        <div className="flex-1 overflow-hidden flex flex-col px-4 sm:px-6 pb-4">
          <div className="flex items-center gap-2 mb-2 flex-wrap">
            <button
              onClick={handleExpandAll}
              className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
            >
              Expand all
            </button>
            <button
              onClick={handleCollapseAll}
              className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
            >
              Collapse all
            </button>
            <div className="h-4 w-px bg-gray-200 mx-1" />
            <button
              onClick={handleToggleDiffMode}
              className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
            >
              {diffMode === 'unified' ? 'Split' : 'Unified'} view
            </button>
            <div className="h-4 w-px bg-gray-200 mx-1" />
            <label className="flex items-center gap-1 text-xs text-gray-500">
              <span>Mode:</span>
              <select
                value={readerMode}
                onChange={(e) => setReaderMode(e.target.value as ReaderMode)}
                className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
              >
                <option value="diff">Diff</option>
                <option value="raw">Raw</option>
                <option value="full">Full file</option>
                <option value="search">Search</option>
              </select>
            </label>
            {selectedFile && totalHunks > 1 && (
              <>
                <div className="h-4 w-px bg-gray-200 mx-1" />
                <button
                  onClick={handlePrevHunk}
                  disabled={activeHunkIndex <= 0}
                  className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
                >
                  Prev hunk
                </button>
                <span className="text-xs text-gray-500">
                  {activeHunkIndex + 1} / {totalHunks}
                </span>
                <button
                  onClick={handleNextHunk}
                  disabled={activeHunkIndex >= totalHunks - 1}
                  className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
                >
                  Next hunk
                </button>
              </>
            )}
            <div className="h-4 w-px bg-gray-200 mx-1" />
            {!commitMode && commits.length > 0 && (
              <div className="flex items-center gap-1">
                <select
                  value=""
                  onChange={(e) => {
                    const commit = commits.find(c => c.hash === e.target.value)
                    if (commit) handleSelectCommit(commit)
                  }}
                  className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
                >
                  <option value="">View commit...</option>
                  {commits.slice(0, 10).map(commit => (
                    <option key={commit.hash} value={commit.hash}>
                      {commit.shortHash}: {commit.message.split('\n')[0].slice(0, 40)}
                    </option>
                  ))}
                </select>
              </div>
            )}
            {commitMode && (
              <button
                onClick={handleExitCommitMode}
                className="px-2 py-1 text-xs bg-blue-100 hover:bg-blue-200 rounded border border-blue-200 transition-colors text-blue-700"
              >
                Exit commit mode
              </button>
            )}
          </div>

          <div className="flex-1 overflow-hidden rounded-lg border border-gray-200 bg-white">
            <div className="flex h-full">
              <div className="w-64 border-r border-gray-200 overflow-hidden flex flex-col">
                {commitMode && selectedCommit ? (
                  <div className="px-3 py-2 border-b border-gray-200 bg-blue-50">
                    <div className="text-xs font-mono text-blue-700">{selectedCommit.shortHash}</div>
                    <div className="text-xs text-blue-600 truncate">{selectedCommit.message.split('\n')[0]}</div>
                    <div className="text-xs text-blue-500 mt-1">{formatRelativeTime(selectedCommit.date)}</div>
                  </div>
                ) : null}
                <ChangedFilesTree
                  blocks={parsedBlocks}
                  selectedFile={selectedFile}
                  onSelectFile={handleSelectFile}
                  expandState={expandState}
                />
              </div>
              <div className="flex-1 overflow-hidden" ref={diffPaneRef}>
                {parsedBlocks.length === 0 ? (
                  <div className="flex items-center justify-center h-full text-gray-400 text-sm">
                    No files to display
                  </div>
                ) : readerMode === 'raw' && selectedFile ? (
                  <RawPatchPane rawPatch={selectedFile.rawPatch ?? ''} />
                ) : readerMode === 'full' && selectedFile ? (
                  <FullFilePane block={selectedFile} issueNumber={issueNumber} />
                ) : readerMode === 'search' && selectedFile ? (
                  <DiffSearchPane
                    block={selectedFile}
                    activeHunkIndex={activeHunkIndex}
                    onActiveHunkChange={setActiveHunkIndex}
                    totalHunks={totalHunks}
                  />
                ) : (
                  <div className="h-full overflow-auto">
                    {parsedBlocks.map((block, index) => (
                      <div key={block.newPath || index} className="border-b border-gray-100 last:border-b-0">
                        <div className="sticky top-0 z-10 bg-gray-50 border-b border-gray-200 px-4 py-2 flex items-center gap-3 text-xs font-mono">
                          <span className="font-medium text-gray-800 truncate flex-1">{block.newPath || block.oldPath}</span>
                          <FileStatusBadge status={block.status} />
                          <span className="text-green-600">+{block.additions}</span>
                          <span className="text-red-500">-{block.deletions}</span>
                        </div>
                        {diffMode === 'unified' ? (
                          <UnifiedDiffPane
                            block={block}
                            activeHunkIndex={0}
                            onActiveHunkChange={() => {}}
                            totalHunks={0}
                          />
                        ) : (
                          <SplitDiffPane
                            block={block}
                            activeHunkIndex={0}
                            totalHunks={0}
                          />
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>
      )}

      {diffAvailable && parsedBlocks.length === 0 && !unavailableMessage && (
        <div className="flex-1 flex items-center justify-center">
          <div className="text-center text-gray-400">
            <div className="text-lg mb-2">No file changes yet</div>
            <div className="text-sm">This issue's worktree has no diff entries</div>
          </div>
        </div>
      )}
    </div>
  )
}