import { useState, useEffect, useRef } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Stage, IssueStatus } from '../lib/types'
import { api } from '../lib/api'
import { useIssue, useIssueDiff, useIssueCommits, useAgentStatus, useExploreSessions, useCreateExploreSession } from '../hooks/useQueries'
import { EditIssueDialog } from './EditIssueDialog'
import { NotFoundPage } from './NotFoundPage'
import { IssueModelSelector } from './IssueModelSelector'
import { BranchBar } from './BranchBar'
import { PipelineView } from './PipelineView'
import { MergeStatePanel } from './MergeStatePanel'
import { QuestionPanel } from './QuestionPanel'
import { SessionList } from './SessionList'
import { formatTime } from '../lib/format-time'
import { statusBadge } from '../lib/status-badge'
import { ChangesPanel } from './ChangesPanel'
import { useDocumentTitle } from '../hooks/useDocumentTitle'

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

export function IssueDetailPage() {
  const { number } = useParams<{ number: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const issueNumber = parseInt(number ?? '0', 10)
  const [editOpen, setEditOpen] = useState(false)
  const [commentText, setCommentText] = useState('')
  const [forceStopConfirming, setForceStopConfirming] = useState(false)
  const forceStopPanelRef = useRef<HTMLDivElement>(null)
  const [diffTab, setDiffTab] = useState<'files' | 'commits'>('commits')
  const [expandedCommits, setExpandedCommits] = useState<Set<string>>(new Set())
  const [expandedFiles, setExpandedFiles] = useState<Set<string>>(new Set())


  useEffect(() => {
    if (!forceStopConfirming) return
    const timer = setTimeout(() => setForceStopConfirming(false), 5000)
    const handleClickOutside = (e: MouseEvent) => {
      if (forceStopPanelRef.current && !forceStopPanelRef.current.contains(e.target as Node)) {
        setForceStopConfirming(false)
      }
    }
    document.addEventListener('mousedown', handleClickOutside)
    return () => {
      clearTimeout(timer)
      document.removeEventListener('mousedown', handleClickOutside)
    }
  }, [forceStopConfirming])

  const { data: issue, isLoading, isError } = useIssue(issueNumber)
  const { data: agentStatus } = useAgentStatus()
  const { data: diffData } = useIssueDiff(issueNumber)

  const activeAgents = agentStatus?.activeAgents ?? []
  const isAgentRunningOnThis = activeAgents.some(a => a.issueNumber === issueNumber)

  useDocumentTitle(`Issue #${issueNumber} — Mohist`, isAgentRunningOnThis)

  const { data: commitsData } = useIssueCommits(issueNumber)

  const startMutation = useMutation({
    mutationFn: () => api.startIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const closeMutation = useMutation({
    mutationFn: () => api.closeIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const forceStopMutation = useMutation({
    mutationFn: () => api.forceStopIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      setForceStopConfirming(false)
    },
  })

  const reopenMutation = useMutation({
    mutationFn: () => api.reopenIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const rerunMutation = useMutation({
    mutationFn: () => api.rerunIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const addCommentMutation = useMutation({
    mutationFn: (body: string) => api.addComment(issueNumber, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      setCommentText('')
    },
  })

  const { data: exploreSessions } = useExploreSessions(issue?.projectId ?? '')
  const createExploreMutation = useCreateExploreSession()
  const [exploreError, setExploreError] = useState<string | null>(null)

  const handleExplore = async () => {
    if (!issue) return
    setExploreError(null)
    const existing = exploreSessions?.find((s) => s.issueId === issue.id)
    if (existing) {
      navigate(`/explore/${existing.id}`)
      return
    }
    try {
      const session = await createExploreMutation.mutateAsync({
        projectId: issue.projectId,
        issueId: issue.id,
      })
      navigate(`/explore/${session.id}`)
    } catch (e) {
      setExploreError(e instanceof Error ? e.message : 'Failed to create explore session')
    }
  }

  if (isError) {
    return <NotFoundPage />
  }

  if (isLoading || !issue) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading...</div>
      </div>
    )
  }

  const maxConcurrent = agentStatus?.maxConcurrentAgents ?? Infinity
  const thisAgent = activeAgents.find(a => a.issueNumber === issueNumber)
  const agentProgress = thisAgent?.progress
  const isCapacityFull = activeAgents.length >= maxConcurrent
  const isBacklog = issue.stage === Stage.Backlog
  const comments = [...(issue.comments ?? [])].sort(
    (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
  )

  return (
    <>
      <div className="flex-1 overflow-y-auto">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 py-6">
          <button
            onClick={() => navigate('/')}
            className="mb-4 inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 transition-colors"
          >
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path
                fillRule="evenodd"
                d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
                clipRule="evenodd"
              />
            </svg>
            Back to board
          </button>

          <div className="mb-6">
            <div className="flex items-center gap-2 mb-1">
              <span className="text-sm font-mono text-gray-400">#{issue.number}</span>
              <span
                className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${statusBadge(issue.status)}`}
              >
                {issue.status}
              </span>
              {isAgentRunningOnThis && (
                <span className="inline-flex items-center gap-1 text-xs text-blue-600">
                  <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
                  Running
                </span>
              )}
            </div>
            <div className="flex items-center gap-3">
              <h1 className="text-2xl font-bold text-gray-900">{issue.title}</h1>
              <button
                onClick={() => setEditOpen(true)}
                className="text-gray-400 hover:text-gray-600 transition-colors"
                title="Edit issue"
              >
                <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
                  <path d="M2.695 14.763l-1.262 3.154a.5.5 0 00.65.65l3.155-1.262a4 4 0 001.343-.885L17.5 5.5a2.121 2.121 0 00-3-3L3.58 13.42a4 4 0 00-.885 1.343z" />
                </svg>
              </button>
            </div>
            {issue.labels.length > 0 && (
              <div className="mt-2 flex flex-wrap gap-1">
                {issue.labels.map((label) => (
                  <span
                    key={label}
                    className="inline-block rounded-full bg-gray-100 px-2 py-0.5 text-xs text-gray-600"
                  >
                    {label}
                  </span>
                ))}
              </div>
            )}
          </div>

          <PipelineView issue={issue} />

          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            <div className="lg:col-span-2 space-y-6">
              <BranchBar issueNumber={issueNumber} stage={issue.stage} isAgentRunning={isAgentRunningOnThis} />
              {issue.body && (
                <div className="rounded-lg border border-gray-200 bg-white p-4">
                  <h2 className="text-sm font-semibold text-gray-700 mb-2">Description</h2>
                  <div className="text-sm text-gray-600 whitespace-pre-wrap">{issue.body}</div>
                </div>
              )}

              <ChangesPanel
                files={diffData?.files ?? []}
                commits={commitsData?.commits ?? []}
                diffTab={diffTab}
                setDiffTab={setDiffTab}
                expandedFiles={expandedFiles}
                setExpandedFiles={setExpandedFiles}
                expandedCommits={expandedCommits}
                setExpandedCommits={setExpandedCommits}
                issueNumber={issueNumber}
              />

              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <h2 className="text-sm font-semibold text-gray-700 mb-3">
                  Comments ({comments.length})
                </h2>
                {comments.length === 0 ? (
                  <p className="text-sm text-gray-400">No comments yet.</p>
                ) : (
                  <div className="space-y-3">
                    {comments.map((comment) => (
                      <div
                        key={comment.id}
                        className="border-b border-gray-100 pb-3 last:border-0 last:pb-0"
                      >
                        <div className="text-xs text-gray-400 mb-1">
                          {formatTime(comment.createdAt)}
                        </div>
                        <div className="text-sm text-gray-700 whitespace-pre-wrap">
                          {comment.body}
                        </div>
                      </div>
                    ))}
                  </div>
                )}

                <div className="mt-4 pt-3 border-t border-gray-100">
                  <textarea
                    value={commentText}
                    onChange={(e) => setCommentText(e.target.value)}
                    placeholder="Add a comment..."
                    rows={2}
                    className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                  />
                  <div className="flex items-center justify-between mt-2">
                    {addCommentMutation.error && (
                      <span className="text-xs text-red-500">
                        {addCommentMutation.error.message}
                      </span>
                    )}
                    <div className="ml-auto">
                      <button
                        onClick={() => addCommentMutation.mutate(commentText)}
                        disabled={!commentText.trim() || addCommentMutation.isPending}
                        className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                      >
                        {addCommentMutation.isPending ? 'Sending...' : 'Comment'}
                      </button>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div className="space-y-4">
              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <h2 className="text-sm font-semibold text-gray-700 mb-3">Details</h2>
                <dl className="space-y-2 text-sm">
                  <div className="flex justify-between">
                    <dt className="text-gray-500">Stage</dt>
                    <dd className="text-gray-900 font-medium capitalize">{issue.stage}</dd>
                  </div>
                  <div className="flex justify-between">
                    <dt className="text-gray-500">Status</dt>
                    <dd
                      className={`font-medium capitalize ${statusBadge(issue.status)}`}
                    >
                      {issue.status}
                    </dd>
                  </div>
                  {issue.projectName && (
                    <div className="flex justify-between">
                      <dt className="text-gray-500">Project</dt>
                      <dd className="text-gray-900">{issue.projectName}</dd>
                    </div>
                  )}
                  <div className="flex justify-between">
                    <dt className="text-gray-500">Created</dt>
                    <dd className="text-gray-500">{formatTime(issue.createdAt)}</dd>
                  </div>
                  <div className="flex justify-between">
                    <dt className="text-gray-500">Updated</dt>
                    <dd className="text-gray-500">{formatTime(issue.updatedAt)}</dd>
                  </div>
                </dl>
              </div>

              {issue.status === IssueStatus.Interrupted && (
                <div className="rounded-lg border border-orange-200 bg-orange-50 p-4">
                  <h2 className="text-sm font-semibold text-orange-800 mb-2">
                    Pipeline Interrupted
                  </h2>
                  <p className="text-xs text-orange-600 mb-3">
                    The pipeline was interrupted (e.g. server restart). Your progress has been preserved.
                    Click &quot;Resume Pipeline&quot; below to continue from where it left off.
                  </p>
                </div>
              )}

              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <h2 className="text-sm font-semibold text-gray-700 mb-3">Actions</h2>
                <div className="space-y-2">
                  {isBacklog && (
                    <button
                      onClick={() => startMutation.mutate()}
                      disabled={isAgentRunningOnThis || isCapacityFull || startMutation.isPending}
                      className="w-full rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                    >
                      {startMutation.isPending
                        ? 'Starting...'
                        : isAgentRunningOnThis
                          ? 'Agent running...'
                          : isCapacityFull
                            ? 'Capacity full...'
                            : 'Start'}
                    </button>
                  )}

                  {isBacklog && (
                    <button
                      onClick={handleExplore}
                      disabled={createExploreMutation.isPending}
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                    >
                      {createExploreMutation.isPending ? 'Opening...' : 'Explore'}
                    </button>
                  )}

                  {issue.status === IssueStatus.Active && !isBacklog && !isAgentRunningOnThis && (
                    <button
                      onClick={() => closeMutation.mutate()}
                      disabled={closeMutation.isPending}
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                    >
                      {closeMutation.isPending ? 'Closing...' : 'Close'}
                    </button>
                  )}

                  {isAgentRunningOnThis && (
                    <div ref={forceStopPanelRef} className="rounded-lg border border-blue-200 bg-blue-50 p-3 space-y-2">
                      <div className="flex items-center gap-2">
                        <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
                        <span className="text-xs font-semibold text-blue-800">
                          {agentProgress
                            ? `${agentProgress.stage.charAt(0).toUpperCase() + agentProgress.stage.slice(1)} Stage`
                            : 'Running...'}
                        </span>
                      </div>
                      {agentProgress?.roundType && (
                        <div className="text-xs text-blue-700">
                          Round: {agentProgress.roundType} #{(agentProgress.roundIndex ?? 0) + 1}
                        </div>
                      )}
                      {agentProgress?.taskProgress && (
                        <div className="text-xs text-blue-700">
                          Tasks: {agentProgress.taskProgress.completed}/{agentProgress.taskProgress.total}
                        </div>
                      )}
                      {agentProgress?.lastActivityAt && (
                        <div className="text-xs text-blue-600">
                          Last activity: {formatRelativeTime(agentProgress.lastActivityAt)}
                        </div>
                      )}
                      <button
                        onClick={() => {
                          if (forceStopConfirming) {
                            forceStopMutation.mutate()
                          } else {
                            setForceStopConfirming(true)
                          }
                        }}
                        disabled={forceStopMutation.isPending}
                        className={`w-full rounded-md px-3 py-1.5 text-xs font-medium transition-colors ${
                          forceStopConfirming
                            ? 'bg-red-600 text-white hover:bg-red-700'
                            : 'border border-red-300 bg-white text-red-600 hover:bg-red-50'
                        } disabled:opacity-50`}
                      >
                        {forceStopMutation.isPending
                          ? 'Stopping...'
                          : forceStopConfirming
                            ? 'Confirm Force Stop'
                            : 'Force Stop'}
                      </button>
                      {forceStopMutation.error && (
                        <div className="text-xs text-red-600">
                          {forceStopMutation.error.message}
                        </div>
                      )}
                    </div>
                  )}

                  {issue.status === IssueStatus.Blocked && (
                    <div className="space-y-2">
                      {issue.blockedReason && (
                        <div className="rounded-md bg-red-50 border border-red-200 px-3 py-2 text-sm text-red-700">
                          {issue.blockedReason}
                        </div>
                      )}
                      <button
                        onClick={() => reopenMutation.mutate()}
                        disabled={reopenMutation.isPending}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                      >
                        {reopenMutation.isPending ? 'Reopening...' : 'Reopen'}
                      </button>
                    </div>
                  )}

                  {issue.status === IssueStatus.Interrupted && (
                    <button
                      onClick={() => reopenMutation.mutate()}
                      disabled={reopenMutation.isPending}
                      className="w-full rounded-md bg-orange-500 px-3 py-2 text-sm font-medium text-white hover:bg-orange-600 disabled:opacity-50 transition-colors"
                    >
                      {reopenMutation.isPending ? 'Resuming...' : 'Resume Pipeline'}
                    </button>
                  )}

                  {!isBacklog && issue.stage !== Stage.Done && !isAgentRunningOnThis && (
                    <button
                      onClick={() => rerunMutation.mutate()}
                      disabled={rerunMutation.isPending}
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                    >
                      {rerunMutation.isPending ? 'Rerunning...' : 'Rerun Stage'}
                    </button>
                  )}

                  {(closeMutation.error || reopenMutation.error || startMutation.error || rerunMutation.error) && (
                    <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                      {closeMutation.error?.message ||
                        reopenMutation.error?.message ||
                        startMutation.error?.message ||
                        rerunMutation.error?.message}
                    </div>
                  )}

                  {exploreError && (
                    <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                      {exploreError}
                    </div>
                  )}

                  {!isAgentRunningOnThis && activeAgents.length > 0 && !isBacklog && (
                    <div className="text-xs text-gray-400 text-center">
                      {activeAgents.length} agent{activeAgents.length > 1 ? 's' : ''} running on other issues
                    </div>
                  )}

                  <div className="pt-2 border-t border-gray-100">
                    <IssueModelSelector issueNumber={issue.number} currentModel={issue.model} />
                  </div>
                </div>
              </div>

              <MergeStatePanel issueNumber={issue.number} mergeState={issue.mergeState} />

              {isAgentRunningOnThis && (
                <QuestionPanel issueId={issue.id} />
              )}

              {!isBacklog && (
                <SessionList
                  issueNumber={issueNumber}
                  currentStage={issue.stage}
                  isLive={isAgentRunningOnThis}
                />
              )}
            </div>
          </div>
        </div>
      </div>

      {issue && (
        <EditIssueDialog
          open={editOpen}
          onClose={() => setEditOpen(false)}
          issue={issue}
        />
      )}
    </>
  )
}
