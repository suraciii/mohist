import { useState, useMemo, useEffect, useRef, useCallback } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { statusBadge, statusLabel, useIssue, useIssueDiff, useIssueCommits, useCommitDiff } from '../../../entities/issue'
import { ChangedFilesTree, DiffSearchPane, FullFilePane, RawPatchPane, SplitDiffPane, UnifiedDiffPane } from '../../../widgets/issue-changed-files'
import { getFileBlockIdentity, parseDiff, parseDiffFiles, selectFirstReadableFile, type FileBlock } from '../../../shared/lib/diff-model'
import type { IssueCommitsResponse, CommitEntry } from '../../../entities/issue'
import { Button } from '@/shared/ui/components/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'
import { useProjectPath } from '../../../entities/project'

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

function BackToIssueButton({ issueNumber }: { issueNumber: number }) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  return (
    <Button
      variant="link"
      onClick={() => navigate(toProjectPath(`/issues/${issueNumber}`))}
      className="mb-4 h-auto p-0 inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 transition-colors"
    >
      <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
        <path
          fillRule="evenodd"
          d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
          clipRule="evenodd"
        />
      </svg>
      Back to issue
    </Button>
  )
}

function PageHeader({
  issue,
  diffData,
  unavailableMessage,
  isBehind,
}: {
  issue: NonNullable<ReturnType<typeof useIssue>['data']>
  diffData: NonNullable<ReturnType<typeof useIssueDiff>['data']> | undefined
  unavailableMessage: string | null
  isBehind: boolean
}) {
  return (
    <div className="max-w-4xl mx-auto px-4 sm:px-6 py-4 w-full">
      <BackToIssueButton issueNumber={issue.number} />
      <div className="mb-4">
        <div className="flex items-center gap-2 mb-1">
          <span className="text-sm font-mono text-gray-400">#{issue.number}</span>
          <span className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${statusBadge(issue.health)}`}>
            {statusLabel(issue.health)}
          </span>
        </div>
        <h1 className="text-2xl font-bold text-gray-900">{issue.title}</h1>
      </div>
      <DiffSummaryCard issue={issue} diffData={diffData} unavailableMessage={unavailableMessage} />
      {isBehind && diffData?.available && (
        <div className="rounded-lg border border-blue-200 bg-blue-50 p-4 mb-4">
          <p className="text-sm text-blue-700">
            This branch is {diffData.behind} commit{diffData.behind > 1 ? 's' : ''} behind {diffData.base}.
            Files changed shows only changes introduced by {diffData.head} from the merge base, matching GitHub PR behavior.
          </p>
        </div>
      )}
    </div>
  )
}

function DiffSummaryCard({
  issue,
  diffData,
  unavailableMessage,
}: {
  issue: NonNullable<ReturnType<typeof useIssue>['data']>
  diffData: NonNullable<ReturnType<typeof useIssueDiff>['data']> | undefined
  unavailableMessage: string | null
}) {
  if (unavailableMessage) {
    return (
      <div className="rounded-lg border border-orange-200 bg-orange-50 p-6">
        <p className="text-sm text-orange-700">{unavailableMessage}</p>
      </div>
    )
  }
  if (!diffData?.available) return null
  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 mb-4">
      <div className="flex items-center gap-3 text-sm">
        <span className="text-gray-500">
          <span className="font-medium text-gray-700">{diffData.head}</span>
          {' wants to merge into '}
          <span className="font-medium text-gray-700">{diffData.base}</span>
        </span>
        <span className="text-gray-300">·</span>
        <span className="text-gray-500"><span className="font-medium text-gray-700">{diffData.ahead}</span> commits ahead</span>
        {diffData.behind > 0 && (
          <>
            <span className="text-gray-300">·</span>
            <span className="text-gray-500"><span className="font-medium text-gray-700">{diffData.behind}</span> behind</span>
          </>
        )}
        <span className="text-gray-300">·</span>
        <span className="text-gray-500"><span className="font-medium text-gray-700">{diffData.summary.filesChanged}</span> files changed</span>
        <span className="text-gray-300">·</span>
        <span className="text-green-600">+{diffData.summary.additions}</span>
        <span className="text-red-500">-{diffData.summary.deletions}</span>
        <span className="text-gray-300">·</span>
        <span className="text-xs text-gray-400 capitalize">{issue.status} · {issue.health}</span>
      </div>
      <div className="mt-2 flex items-center gap-3 text-xs text-gray-400">
        <span>showing merge-base → {diffData.head}</span>
      </div>
    </div>
  )
}

function ErrorState({ issueNumber, issueError, diffError }: { issueNumber: number; issueError: boolean; diffError: boolean }) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  return (
    <div className="flex-1 overflow-hidden flex flex-col">
      <div className="max-w-4xl mx-auto px-4 sm:px-6 py-4 w-full">
        <BackToIssueButton issueNumber={issueNumber} />
        <div className="rounded-lg border border-red-200 bg-red-50 p-6">
          <p className="text-sm text-red-700 mb-4">
            {issueError ? 'Failed to load issue details.' : diffError ? 'Failed to load issue diff.' : 'Failed to load issue commits.'}
          </p>
          <Button variant="link" onClick={() => navigate(toProjectPath(`/issues/${issueNumber}`))} className="h-auto p-0 text-sm text-blue-600 hover:text-blue-700">
            View issue detail
          </Button>
        </div>
      </div>
    </div>
  )
}

function InvalidIssueState() {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  return (
    <div className="flex-1 overflow-hidden flex flex-col">
      <div className="max-w-4xl mx-auto px-4 sm:px-6 py-4 w-full">
        <div className="rounded-lg border border-red-200 bg-red-50 p-6">
          <p className="text-sm text-red-700 mb-4">Invalid issue number</p>
          <Button variant="link" onClick={() => navigate(toProjectPath())} className="h-auto p-0 text-sm text-blue-600 hover:text-blue-700">
            Back to board
          </Button>
        </div>
      </div>
    </div>
  )
}

interface ReaderToolbarProps {
  diffMode: DiffMode
  readerMode: ReaderMode
  selectedFile: FileBlock | null
  totalHunks: number
  activeHunkIndex: number
  commits: CommitEntry[]
  commitMode: boolean
  onExpandAll: () => void
  onCollapseAll: () => void
  onToggleDiffMode: () => void
  onReaderModeChange: (mode: ReaderMode) => void
  onPrevHunk: () => void
  onNextHunk: () => void
  onSelectCommit: (commit: CommitEntry) => void
  onExitCommitMode: () => void
}

const readerModeLabels: Record<ReaderMode, string> = {
  diff: 'Diff',
  raw: 'Raw',
  full: 'Full file',
  search: 'Search',
}

function ReaderToolbar(props: ReaderToolbarProps) {
  return (
    <div className="flex items-center gap-2 mb-2 flex-wrap">
      <Button variant="outline" size="xs" onClick={props.onExpandAll}>Expand all</Button>
      <Button variant="outline" size="xs" onClick={props.onCollapseAll}>Collapse all</Button>
      <div className="h-4 w-px bg-gray-200 mx-1" />
      <Button variant="outline" size="xs" onClick={props.onToggleDiffMode}>
        {props.diffMode === 'unified' ? 'Split' : 'Unified'} view
      </Button>
      <div className="h-4 w-px bg-gray-200 mx-1" />
      <label className="flex items-center gap-1 text-xs text-gray-500">
        <span>Mode:</span>
        <Select value={props.readerMode} onValueChange={(value) => value && props.onReaderModeChange(value as ReaderMode)}>
          <SelectTrigger aria-label="Reader mode" className="h-7 w-[110px] text-xs">
            <SelectValue>{readerModeLabels[props.readerMode]}</SelectValue>
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="diff">Diff</SelectItem>
            <SelectItem value="raw">Raw</SelectItem>
            <SelectItem value="full">Full file</SelectItem>
            <SelectItem value="search">Search</SelectItem>
          </SelectContent>
        </Select>
      </label>
      {props.selectedFile && props.totalHunks > 1 && (
        <>
          <div className="h-4 w-px bg-gray-200 mx-1" />
          <Button variant="outline" size="xs" onClick={props.onPrevHunk} disabled={props.activeHunkIndex <= 0}>Prev hunk</Button>
          <span className="text-xs text-gray-500">{props.activeHunkIndex + 1} / {props.totalHunks}</span>
          <Button variant="outline" size="xs" onClick={props.onNextHunk} disabled={props.activeHunkIndex >= props.totalHunks - 1}>Next hunk</Button>
        </>
      )}
      <CommitSelector {...props} />
    </div>
  )
}

function CommitSelector({ commitMode, commits, onSelectCommit, onExitCommitMode }: Pick<ReaderToolbarProps, 'commitMode' | 'commits' | 'onSelectCommit' | 'onExitCommitMode'>) {
  if (commitMode) {
    return (
      <>
        <div className="h-4 w-px bg-gray-200 mx-1" />
        <Button variant="outline" size="xs" onClick={onExitCommitMode} className="border-blue-200 bg-blue-100 text-blue-700 hover:bg-blue-200 hover:text-blue-700">
          Exit commit mode
        </Button>
      </>
    )
  }
  if (commits.length === 0) return null
  return (
    <>
      <div className="h-4 w-px bg-gray-200 mx-1" />
      <Select
        value=""
        onValueChange={(value) => {
          const commit = commits.find(c => c.hash === value)
          if (commit) onSelectCommit(commit)
        }}
      >
        <SelectTrigger aria-label="Commit view" className="h-7 w-[180px] text-xs">
          <SelectValue placeholder="View commit..." />
        </SelectTrigger>
        <SelectContent>
        {commits.slice(0, 10).map(commit => (
          <SelectItem key={commit.hash} value={commit.hash}>{commit.shortHash}: {commit.message.split('\n')[0].slice(0, 40)}</SelectItem>
        ))}
        </SelectContent>
      </Select>
    </>
  )
}

interface ReaderPaneProps {
  selectedFile: FileBlock | null
  readerMode: ReaderMode
  diffMode: DiffMode
  activeHunkIndex: number
  totalHunks: number
  issueNumber: number
  parsedBlocks: FileBlock[]
  hasFileEntries: boolean
  isCommitDiffLoading: boolean
  isCommitDiffError: boolean
  renderAnyway: boolean
  onActiveHunkChange: (index: number) => void
  onExitCommitMode: () => void
  onRenderAnyway: () => void
}

function ReaderPane(props: ReaderPaneProps) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  if (props.isCommitDiffLoading) return <CenteredReaderMessage>Loading commit diff...</CenteredReaderMessage>
  if (props.isCommitDiffError) {
    return (
      <div className="flex flex-col items-center justify-center h-full text-gray-500 text-sm gap-3 px-4 text-center">
        <div>Failed to load commit diff.</div>
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={props.onExitCommitMode}>Exit commit mode</Button>
          <Button variant="link" onClick={() => navigate(toProjectPath(`/issues/${props.issueNumber}`))} className="h-auto p-0 text-sm text-blue-600 hover:text-blue-700">Back to issue</Button>
        </div>
      </div>
    )
  }
  if (!props.hasFileEntries) return <CenteredReaderMessage>No files to display</CenteredReaderMessage>
  if (props.readerMode === 'raw' && props.selectedFile) return <RawPatchPane block={props.selectedFile} rawPatch={props.selectedFile.rawPatch ?? ''} renderAnyway={props.renderAnyway} onRenderAnyway={props.onRenderAnyway} />
  if (props.readerMode === 'full' && props.selectedFile) return <FullFilePane block={props.selectedFile} issueNumber={props.issueNumber} renderAnyway={props.renderAnyway} onRenderAnyway={props.onRenderAnyway} />
  if (props.readerMode === 'search' && props.selectedFile) {
    return <DiffSearchPane block={props.selectedFile} activeHunkIndex={props.activeHunkIndex} onActiveHunkChange={props.onActiveHunkChange} totalHunks={props.totalHunks} renderAnyway={props.renderAnyway} onRenderAnyway={props.onRenderAnyway} />
  }
  return (
    <div className="h-full overflow-auto">
      {props.selectedFile ? (
        props.diffMode === 'unified' ? (
          <UnifiedDiffPane block={props.selectedFile} activeHunkIndex={props.activeHunkIndex} onActiveHunkChange={props.onActiveHunkChange} totalHunks={props.totalHunks} renderAnyway={props.renderAnyway} onRenderAnyway={props.onRenderAnyway} />
        ) : (
          <SplitDiffPane block={props.selectedFile} activeHunkIndex={props.activeHunkIndex} totalHunks={props.totalHunks} renderAnyway={props.renderAnyway} onRenderAnyway={props.onRenderAnyway} />
        )
      ) : (
        <div className="flex flex-col items-center justify-center h-full text-gray-400 text-sm">
          <div className="mb-2">Select a file from the tree to read its diff</div>
          <div className="text-xs text-gray-400">{props.parsedBlocks.length} file{props.parsedBlocks.length !== 1 ? 's' : ''} changed</div>
        </div>
      )}
    </div>
  )
}

function CenteredReaderMessage({ children }: { children: string }) {
  return <div className="flex items-center justify-center h-full text-gray-400 text-sm">{children}</div>
}

interface ReaderWorkspaceProps {
  issueNumber: number
  parsedBlocks: FileBlock[]
  hasFileEntries: boolean
  commits: CommitEntry[]
  commitMode: boolean
  selectedCommit: CommitEntry | null
  isCommitDiffLoading: boolean
  isCommitDiffError: boolean
  reader: ReturnType<typeof useChangedFilesReaderState>
  onExpandAll: () => void
  onCollapseAll: () => void
  onToggleDiffMode: () => void
  onPrevHunk: () => void
  onNextHunk: () => void
  onSelectCommit: (commit: CommitEntry) => void
  onExitCommitMode: () => void
}

function ReaderWorkspace(props: ReaderWorkspaceProps) {
  const totalHunks = props.reader.selectedFile?.hunkCount ?? 0

  return (
    <div className="flex-1 overflow-hidden flex flex-col px-4 sm:px-6 pb-4">
      <ReaderToolbar
        diffMode={props.reader.diffMode}
        readerMode={props.reader.readerMode}
        selectedFile={props.reader.selectedFile}
        totalHunks={totalHunks}
        activeHunkIndex={props.reader.activeHunkIndex}
        commits={props.commits}
        commitMode={props.commitMode}
        onExpandAll={props.onExpandAll}
        onCollapseAll={props.onCollapseAll}
        onToggleDiffMode={props.onToggleDiffMode}
        onReaderModeChange={props.reader.setReaderMode}
        onPrevHunk={props.onPrevHunk}
        onNextHunk={props.onNextHunk}
        onSelectCommit={props.onSelectCommit}
        onExitCommitMode={props.onExitCommitMode}
      />

      <div className="flex-1 overflow-hidden rounded-lg border border-gray-200 bg-white">
        <div className="flex h-full">
          <FilesSidebar
            blocks={props.parsedBlocks}
            selectedFile={props.reader.selectedFile}
            expandState={props.reader.expandState}
            commitMode={props.commitMode}
            selectedCommit={props.selectedCommit}
            onSelectFile={props.reader.handleSelectFile}
          />
          <div className="flex-1 overflow-hidden" ref={props.reader.diffPaneRef}>
            <ReaderPane
              selectedFile={props.reader.selectedFile}
              readerMode={props.reader.readerMode}
              diffMode={props.reader.diffMode}
              activeHunkIndex={props.reader.activeHunkIndex}
              totalHunks={totalHunks}
              issueNumber={props.issueNumber}
              parsedBlocks={props.parsedBlocks}
              hasFileEntries={props.hasFileEntries}
              isCommitDiffLoading={props.isCommitDiffLoading}
              isCommitDiffError={props.isCommitDiffError}
              renderAnyway={props.reader.selectedFileRendered}
              onActiveHunkChange={props.reader.setActiveHunkIndex}
              onExitCommitMode={props.onExitCommitMode}
              onRenderAnyway={props.reader.handleRenderAnyway}
            />
          </div>
        </div>
      </div>
    </div>
  )
}

function FilesSidebar({
  blocks,
  selectedFile,
  expandState,
  commitMode,
  selectedCommit,
  onSelectFile,
}: {
  blocks: FileBlock[]
  selectedFile: FileBlock | null
  expandState: ExpandState
  commitMode: boolean
  selectedCommit: CommitEntry | null
  onSelectFile: (block: FileBlock) => void
}) {
  return (
    <div className="w-64 border-r border-gray-200 overflow-hidden flex flex-col">
      {commitMode && selectedCommit ? <CommitHeader commit={selectedCommit} /> : null}
      <ChangedFilesTree blocks={blocks} selectedFile={selectedFile} onSelectFile={onSelectFile} expandState={expandState} />
    </div>
  )
}

function CommitHeader({ commit }: { commit: CommitEntry }) {
  return (
    <div className="px-3 py-2 border-b border-gray-200 bg-blue-50">
      <div className="text-xs font-mono text-blue-700">{commit.shortHash}</div>
      <div className="text-xs text-blue-600 truncate">{commit.message.split('\n')[0]}</div>
      <div className="text-xs text-blue-500 mt-1">{formatRelativeTime(commit.date)}</div>
    </div>
  )
}

function useChangedFilesReaderState({
  issueNumber,
  parsedBlocks,
  commitMode,
}: {
  issueNumber: number
  parsedBlocks: FileBlock[]
  commitMode: boolean
}) {
  const [selectedFile, setSelectedFile] = useState<FileBlock | null>(null)
  const [expandState, setExpandState] = useState<ExpandState>('none')
  const [diffMode, setDiffMode] = useState<DiffMode>('unified')
  const [readerMode, setReaderMode] = useState<ReaderMode>('diff')
  const [activeHunkIndex, setActiveHunkIndex] = useState(0)
  const [renderedFileIdentities, setRenderedFileIdentities] = useState<Set<string>>(new Set())
  const diffPaneRef = useRef<HTMLDivElement>(null)
  const savedState = useMemo(() => loadReaderState(issueNumber), [issueNumber])

  useEffect(() => {
    if (!commitMode) {
      setDiffMode(savedState.diffMode)
      setReaderMode(savedState.readerMode)
      setActiveHunkIndex(savedState.activeHunkIndex)
    }
  }, [savedState, commitMode])

  useEffect(() => {
    setRenderedFileIdentities(new Set())
  }, [issueNumber])

  useEffect(() => {
    if (parsedBlocks.length === 0) {
      setSelectedFile(null)
      return
    }
    const currentPath = selectedFile ? getFileBlockIdentity(selectedFile) : null
    const preferredPath = commitMode ? currentPath : savedState.selectedFilePath ?? currentPath
    const found = preferredPath ? parsedBlocks.find(block => getFileBlockIdentity(block) === preferredPath) : null
    if (found) {
      if (currentPath !== getFileBlockIdentity(found)) setSelectedFile(found)
      if (!commitMode) requestAnimationFrame(() => {
        if (diffPaneRef.current) diffPaneRef.current.scrollTop = savedState.scrollTop
      })
      return
    }
    const firstReadable = selectFirstReadableFile(parsedBlocks)
    if (firstReadable) {
      if (currentPath !== getFileBlockIdentity(firstReadable)) setSelectedFile(firstReadable)
      return
    }
    if (selectedFile !== null) setSelectedFile(null)
  }, [parsedBlocks, savedState.selectedFilePath, savedState.scrollTop, commitMode, selectedFile])

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
    if (!commitMode) setActiveHunkIndex(0)
  }, [selectedFile, commitMode])

  const selectedFileIdentity = selectedFile ? getFileBlockIdentity(selectedFile) : null
  const selectedFileRendered = selectedFileIdentity ? renderedFileIdentities.has(selectedFileIdentity) : false
  const handleSelectFile = useCallback((block: FileBlock) => {
    setSelectedFile(block)
    setActiveHunkIndex(0)
  }, [])
  const clearSelection = useCallback(() => {
    setSelectedFile(null)
    setActiveHunkIndex(0)
  }, [])
  const handleRenderAnyway = useCallback(() => {
    if (!selectedFileIdentity) return
    setRenderedFileIdentities(prev => new Set(prev).add(selectedFileIdentity))
  }, [selectedFileIdentity])

  return {
    selectedFile,
    expandState,
    diffMode,
    readerMode,
    activeHunkIndex,
    diffPaneRef,
    selectedFileRendered,
    setExpandState,
    setDiffMode,
    setReaderMode,
    setActiveHunkIndex,
    handleSelectFile,
    clearSelection,
    handleRenderAnyway,
  }
}

function useChangedFilesData(issueNumber: number, commitMode: boolean, commitHash: string) {
  const issueQuery = useIssue(issueNumber)
  const diffQuery = useIssueDiff(issueNumber)
  const commitsQuery = useIssueCommits(issueNumber)
  const commitDiffQuery = useCommitDiff(issueNumber, commitHash, commitMode && !!commitHash)

  const parsedBlocks = useMemo(() => {
    if (commitMode && commitDiffQuery.data?.diff) return parseDiff(commitDiffQuery.data.diff)
    if (!diffQuery.data?.files?.length) return []
    return parseDiffFiles(diffQuery.data.files)
  }, [diffQuery.data?.files, commitDiffQuery.data?.diff, commitMode])

  return {
    issue: issueQuery.data,
    diffData: diffQuery.data,
    commitsData: commitsQuery.data,
    parsedBlocks,
    hasFileEntries: commitMode ? parsedBlocks.length > 0 : (diffQuery.data?.files?.length ?? 0) > 0,
    isLoading: issueQuery.isLoading || diffQuery.isLoading || commitsQuery.isLoading,
    hasQueryError: issueQuery.isError || diffQuery.isError || commitsQuery.isError,
    issueError: issueQuery.isError,
    diffError: diffQuery.isError,
    isCommitDiffLoading: commitMode && !!commitHash && commitDiffQuery.isLoading,
    isCommitDiffError: commitMode && !!commitHash && commitDiffQuery.isError,
  }
}

function getDiffAvailability(
  diffData: ReturnType<typeof useChangedFilesData>['diffData'],
  commitsData: ReturnType<typeof useChangedFilesData>['commitsData'],
) {
  const workspaceRemoved = diffData?.reason === 'workspace_removed' || commitsData?.reason === 'workspace_removed'
  const branchMissing = diffData?.reason === 'branch_missing' || commitsData?.reason === 'branch_missing'
  const notStarted = diffData?.reason === 'not_started' || commitsData?.reason === 'not_started'
  const runnerUnavailable = diffData?.reason === 'runner_unavailable' || commitsData?.reason === 'runner_unavailable'
  const unavailableMessage = notStarted
    ? 'No changes yet'
    : runnerUnavailable
      ? 'Changes unavailable — runner not connected'
      : workspaceRemoved
        ? 'Changes unavailable — workspace removed'
        : branchMissing
          ? 'Changes unavailable — branch missing'
          : diffData?.available === false
            ? diffData.message ?? 'Failed to load changes'
            : null

  return {
    diffAvailable: diffData?.available === true,
    isBehind: !!diffData && diffData.available && diffData.behind > 0,
    unavailableMessage,
  }
}

function useCommitModeActions(
  reader: ReturnType<typeof useChangedFilesReaderState>,
  setCommitMode: (commitMode: boolean) => void,
  setSelectedCommit: (commit: CommitEntry | null) => void,
) {
  const exitCommitMode = useCallback(() => {
    setCommitMode(false)
    setSelectedCommit(null)
    reader.setActiveHunkIndex(0)
  }, [reader])

  const selectCommit = useCallback((commit: CommitEntry) => {
    setSelectedCommit(commit)
    setCommitMode(true)
    reader.clearSelection()
  }, [reader])

  return {
    exitCommitMode,
    selectCommit,
  }
}

function ChangedFilesContent({
  issueNumber,
  data,
  reader,
  commitState,
  unavailableMessage,
  isBehind,
}: {
  issueNumber: number
  data: ReturnType<typeof useChangedFilesData>
  reader: ReturnType<typeof useChangedFilesReaderState>
  commitState: {
    commitMode: boolean
    selectedCommit: CommitEntry | null
    exitCommitMode: () => void
    selectCommit: (commit: CommitEntry) => void
  }
  unavailableMessage: string | null
  isBehind: boolean
}) {
  const commits = (data.commitsData as IssueCommitsResponse | undefined)?.commits ?? []
  const handleExpandAll = () => reader.setExpandState('all')
  const handleCollapseAll = () => reader.setExpandState('none')
  const handlePrevHunk = () => reader.setActiveHunkIndex(prev => Math.max(0, prev - 1))
  const handleNextHunk = () => reader.setActiveHunkIndex(prev => Math.min((reader.selectedFile?.hunkCount ?? 1) - 1, prev + 1))
  const handleToggleDiffMode = () => {
    reader.setDiffMode(prev => prev === 'unified' ? 'split' : 'unified')
    reader.setActiveHunkIndex(0)
  }

  return (
    <div className="flex-1 overflow-hidden flex flex-col">
      <PageHeader issue={data.issue!} diffData={data.diffData} unavailableMessage={unavailableMessage} isBehind={isBehind} />
      {data.hasFileEntries ? (
        <ReaderWorkspace
          issueNumber={issueNumber}
          parsedBlocks={data.parsedBlocks}
          hasFileEntries={data.hasFileEntries}
          commits={commits}
          commitMode={commitState.commitMode}
          selectedCommit={commitState.selectedCommit}
          isCommitDiffLoading={data.isCommitDiffLoading}
          isCommitDiffError={data.isCommitDiffError}
          reader={reader}
          onExpandAll={handleExpandAll}
          onCollapseAll={handleCollapseAll}
          onToggleDiffMode={handleToggleDiffMode}
          onPrevHunk={handlePrevHunk}
          onNextHunk={handleNextHunk}
          onSelectCommit={commitState.selectCommit}
          onExitCommitMode={commitState.exitCommitMode}
        />
      ) : (
        <NoFileChangesState />
      )}
    </div>
  )
}

function NoFileChangesState() {
  return (
    <div className="flex-1 flex items-center justify-center">
      <div className="text-center text-gray-400">
        <div className="text-lg mb-2">No file changes yet</div>
        <div className="text-sm">This issue's workflow workspace has no diff entries</div>
      </div>
    </div>
  )
}

export function IssueChangedFilesPage() {
  const { number } = useParams<{ number: string }>()
  const issueNumber = parseInt(number ?? '0', 10)
  const [commitMode, setCommitMode] = useState(false)
  const [selectedCommit, setSelectedCommit] = useState<CommitEntry | null>(null)
  const data = useChangedFilesData(issueNumber, commitMode, selectedCommit?.hash ?? '')
  const availability = getDiffAvailability(data.diffData, data.commitsData)
  const reader = useChangedFilesReaderState({ issueNumber, parsedBlocks: data.parsedBlocks, commitMode })
  const commitActions = useCommitModeActions(reader, setCommitMode, setSelectedCommit)
  const commitState = { commitMode, selectedCommit, ...commitActions }

  if (!issueNumber || isNaN(issueNumber) || issueNumber === 0) {
    return <InvalidIssueState />
  }

  if (data.hasQueryError) {
    return <ErrorState issueNumber={issueNumber} issueError={data.issueError} diffError={data.diffError} />
  }

  if (data.isLoading || !data.issue) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading...</div>
      </div>
    )
  }

  if (!availability.diffAvailable) return (
    <div className="flex-1 overflow-hidden flex flex-col">
      <PageHeader issue={data.issue} diffData={data.diffData} unavailableMessage={availability.unavailableMessage} isBehind={availability.isBehind} />
    </div>
  )

  return <ChangedFilesContent issueNumber={issueNumber} data={data} reader={reader} commitState={commitState} unavailableMessage={availability.unavailableMessage} isBehind={availability.isBehind} />
}
