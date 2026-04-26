import { Fragment, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Stage, IssueStatus } from '../lib/types'
import type { DiffFile } from '../lib/types'
import { api } from '../lib/api'
import { useIssue, useIssueDiff, useAgentStatus, useSendMessage, useExploreSessions, useCreateExploreSession } from '../hooks/useQueries'
import { useSessionTimeline } from '../hooks/useSessionTimeline'
import { EditIssueDialog } from './EditIssueDialog'
import { MergeStatePanel } from './MergeStatePanel'
import { QuestionPanel } from './QuestionPanel'
import { SessionTimeline } from './SessionTimeline'

const STAGES = [Stage.Draft, Stage.Explore, Stage.Plan, Stage.Build, Stage.Review, Stage.Done]

const STAGE_LABELS: Record<string, string> = {
  [Stage.Draft]: 'Draft',
  [Stage.Explore]: 'Explore',
  [Stage.Plan]: 'Plan',
  [Stage.Build]: 'Build',
  [Stage.Review]: 'Review',
  [Stage.Done]: 'Done',
}

const DIFF_STAGES = new Set<string>([Stage.Build, Stage.Review, Stage.Done])

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString()
}

function statusBadge(status: IssueStatus): string {
  switch (status) {
    case IssueStatus.Active:
      return 'text-green-700 bg-green-50'
    case IssueStatus.Paused:
      return 'text-amber-700 bg-amber-50'
    case IssueStatus.Blocked:
      return 'text-red-700 bg-red-50'
    case IssueStatus.Interrupted:
      return 'text-orange-700 bg-orange-50'
    case IssueStatus.Closed:
      return 'text-gray-600 bg-gray-100'
    case IssueStatus.Completed:
      return 'text-green-800 bg-green-100'
    default:
      return 'text-gray-700 bg-gray-50'
  }
}

export function IssueDetailPage() {
  const { number } = useParams<{ number: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const issueNumber = parseInt(number ?? '0', 10)
  const [editOpen, setEditOpen] = useState(false)
  const [commentText, setCommentText] = useState('')
  const [messageText, setMessageText] = useState('')

  const { data: issue, isLoading } = useIssue(issueNumber)
  const { data: agentStatus } = useAgentStatus()
  const { data: diffData } = useIssueDiff(issueNumber)
  const {
    rounds,
    isStreaming,
    isLoading: sessionLoading,
  } = useSessionTimeline(issueNumber)

  const approveMutation = useMutation({
    mutationFn: () => api.approveIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

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

  const reopenMutation = useMutation({
    mutationFn: () => api.reopenIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const addCommentMutation = useMutation({
    mutationFn: (body: string) => api.addComment(issueNumber, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      setCommentText('')
    },
  })

  const sendMessageMutation = useSendMessage(issueNumber)

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

  if (isLoading || !issue) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading...</div>
      </div>
    )
  }

  const stageIndex = STAGES.indexOf(issue.stage)
  const activeAgents = agentStatus?.activeAgents ?? []
  const maxConcurrent = agentStatus?.maxConcurrentAgents ?? Infinity
  const isAgentRunningOnThis = activeAgents.some(a => a.issueNumber === issueNumber)
  const isCapacityFull = activeAgents.length >= maxConcurrent
  const isApprovalGate =
    issue.approvalState?.status === 'awaiting' &&
    (issue.status === IssueStatus.Active || issue.status === IssueStatus.Blocked) &&
    !isAgentRunningOnThis
  const isDraft = issue.stage === Stage.Draft
  const showDiff = DIFF_STAGES.has(issue.stage)
  const comments = [...(issue.comments ?? [])].sort(
    (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
  )
  const reviewOutput = (() => {
    const output = issue.approvalState?.output
    if (output) {
      const extracted = output.selfReviewNotes || output.reviewReport
      if (typeof extracted === 'string' && extracted.trim()) return extracted
      const json = JSON.stringify(output, null, 2)
      if (json !== '{}') return json
    }
    const lastComment = [...(issue.comments ?? [])].sort(
      (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
    )[0]
    return lastComment?.body || ''
  })()

  return (
    <>
      <div className="flex-1 overflow-y-auto">
        <div className="max-w-4xl mx-auto px-6 py-6">
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
              {issue.status === IssueStatus.Completed ? (
                <span className="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium text-green-800 bg-green-100">
                  <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor">
                    <path fillRule="evenodd" d="M16.704 4.153a.75.75 0 01.143 1.052l-8 10.5a.75.75 0 01-1.127.075l-4.5-4.5a.75.75 0 011.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 011.05-.143z" clipRule="evenodd" />
                  </svg>
                  Completed
                </span>
              ) : (
                <span
                  className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium capitalize ${statusBadge(issue.status)}`}
                >
                  {issue.status}
                </span>
              )}
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

          <div className="mb-6">
            <div className="flex items-center gap-1">
              {STAGES.map((stage, i) => (
                <Fragment key={stage}>
                  {i > 0 && (
                    <div
                      className={`h-0.5 flex-1 ${i <= stageIndex ? 'bg-blue-500' : 'bg-gray-200'}`}
                    />
                  )}
                  <div className="flex flex-col items-center">
                    <div
                      className={`h-3 w-3 rounded-full ${
                        i < stageIndex
                          ? 'bg-blue-500'
                          : i === stageIndex
                            ? 'bg-blue-500 ring-4 ring-blue-100'
                            : 'bg-gray-200'
                      }`}
                    />
                    <span
                      className={`mt-1 text-xs ${i <= stageIndex ? 'text-blue-600 font-medium' : 'text-gray-400'}`}
                    >
                      {STAGE_LABELS[stage]}
                    </span>
                  </div>
                </Fragment>
              ))}
            </div>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            <div className="lg:col-span-2 space-y-6">
              {issue.body && (
                <div className="rounded-lg border border-gray-200 bg-white p-4">
                  <h2 className="text-sm font-semibold text-gray-700 mb-2">Description</h2>
                  <div className="text-sm text-gray-600 whitespace-pre-wrap">{issue.body}</div>
                </div>
              )}

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

              {showDiff && diffData && diffData.files.length > 0 && (
                <div className="rounded-lg border border-gray-200 bg-white p-4">
                  <h2 className="text-sm font-semibold text-gray-700 mb-3">Changed Files</h2>
                  <div className="space-y-1">
                    {diffData.files.map((f: DiffFile, i: number) => (
                      <div key={i} className="flex items-center gap-2 text-sm">
                        <span className="text-gray-700 font-mono text-xs truncate flex-1">
                          {f.file}
                        </span>
                        <span className="text-green-600 text-xs font-medium">+{f.additions}</span>
                        <span className="text-red-500 text-xs font-medium">-{f.deletions}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}
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
                  {issue.status === IssueStatus.Closed && (
                    <button
                      onClick={() => reopenMutation.mutate()}
                      disabled={reopenMutation.isPending}
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                    >
                      {reopenMutation.isPending ? 'Reopening...' : 'Reopen'}
                    </button>
                  )}

                  {issue.status === IssueStatus.Completed && (
                    <p className="text-sm text-green-700 text-center py-2">
                      ✓ This issue has been completed.
                    </p>
                  )}

                  {issue.status === IssueStatus.Paused && (
                    <>
                      <button
                        onClick={() => reopenMutation.mutate()}
                        disabled={reopenMutation.isPending}
                        className="w-full rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                      >
                        {reopenMutation.isPending ? 'Resuming...' : 'Resume'}
                      </button>
                      <button
                        onClick={() => closeMutation.mutate()}
                        disabled={closeMutation.isPending}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                      >
                        {closeMutation.isPending ? 'Closing...' : 'Close'}
                      </button>
                    </>
                  )}

                  {issue.status === IssueStatus.Blocked && (
                    <>
                      <button
                        onClick={() => reopenMutation.mutate()}
                        disabled={reopenMutation.isPending}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                      >
                        {reopenMutation.isPending ? 'Reopening...' : 'Reopen'}
                      </button>
                      <button
                        onClick={() => closeMutation.mutate()}
                        disabled={closeMutation.isPending}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                      >
                        {closeMutation.isPending ? 'Closing...' : 'Close'}
                      </button>
                    </>
                  )}

                  {issue.status === IssueStatus.Interrupted && (
                    <>
                      <button
                        onClick={() => reopenMutation.mutate()}
                        disabled={reopenMutation.isPending}
                        className="w-full rounded-md bg-orange-500 px-3 py-2 text-sm font-medium text-white hover:bg-orange-600 disabled:opacity-50 transition-colors"
                      >
                        {reopenMutation.isPending ? 'Resuming...' : 'Resume Pipeline'}
                      </button>
                      <button
                        onClick={() => closeMutation.mutate()}
                        disabled={closeMutation.isPending}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                      >
                        {closeMutation.isPending ? 'Closing...' : 'Close'}
                      </button>
                    </>
                  )}

                  {issue.status === IssueStatus.Active && isDraft && (
                    <>
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
                      <button
                        onClick={handleExplore}
                        disabled={createExploreMutation.isPending}
                        className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                      >
                        {createExploreMutation.isPending ? 'Opening...' : 'Explore'}
                      </button>
                    </>
                  )}

                  {issue.status === IssueStatus.Active && !isDraft && (
                    <button
                      onClick={() => closeMutation.mutate()}
                      disabled={closeMutation.isPending}
                      className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
                    >
                      {closeMutation.isPending ? 'Closing...' : 'Close'}
                    </button>
                  )}

                  {(closeMutation.error || reopenMutation.error || startMutation.error) && (
                    <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                      {closeMutation.error?.message ||
                        reopenMutation.error?.message ||
                        startMutation.error?.message}
                    </div>
                  )}

                  {exploreError && (
                    <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                      {exploreError}
                    </div>
                  )}

                  {!isAgentRunningOnThis && activeAgents.length > 0 && !isDraft && (
                    <div className="text-xs text-gray-400 text-center">
                      {activeAgents.length} agent{activeAgents.length > 1 ? 's' : ''} running on other issues
                    </div>
                  )}
                </div>
              </div>

              <MergeStatePanel issueNumber={issue.number} mergeState={issue.mergeState} />

              {isApprovalGate && reviewOutput && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-4">
                  <h2 className="text-sm font-semibold text-amber-800 mb-2">
                    Review Report
                  </h2>
                  <div className="rounded bg-white p-3 max-h-64 overflow-y-auto">
                    <div className="text-sm text-gray-700 whitespace-pre-wrap">
                      {reviewOutput}
                    </div>
                  </div>
                </div>
              )}

              {isApprovalGate && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-4">
                  <h2 className="text-sm font-semibold text-amber-800 mb-2">
                    Approval Required
                  </h2>
                  <p className="text-xs text-amber-600 mb-3">
                    The agent completed the previous stage. Review the output above and approve
                    to continue.
                  </p>
                  <div className="flex gap-2">
                    <button
                      onClick={() => approveMutation.mutate()}
                      disabled={approveMutation.isPending || isAgentRunningOnThis}
                      className="flex-1 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                    >
                      {approveMutation.isPending
                        ? 'Approving...'
                        : isAgentRunningOnThis
                          ? 'Agent running...'
                          : 'Approve & Continue'}
                    </button>
                  </div>
                  {approveMutation.error && (
                    <div className="mt-2 rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                      {approveMutation.error.message}
                    </div>
                  )}
                </div>
              )}

              {isApprovalGate && (
                <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
                  <h2 className="text-sm font-semibold text-blue-800 mb-2">Send Message</h2>
                  <p className="text-xs text-blue-600 mb-3">
                    Send a free-text message to the agent. The agent will decide the next step
                    based on your message.
                  </p>
                  <textarea
                    value={messageText}
                    onChange={(e) => setMessageText(e.target.value)}
                    placeholder="Type a message to the agent..."
                    rows={3}
                    className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                  />
                  <div className="flex items-center justify-between mt-2">
                    {sendMessageMutation.error && (
                      <span className="text-xs text-red-500">
                        {sendMessageMutation.error.message}
                      </span>
                    )}
                    <div className="ml-auto">
                      <button
                        onClick={() => {
                          sendMessageMutation.mutate(messageText, {
                            onSuccess: () => setMessageText(''),
                          })
                        }}
                        disabled={!messageText.trim() || sendMessageMutation.isPending}
                        className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                      >
                        {sendMessageMutation.isPending ? 'Sending...' : 'Send'}
                      </button>
                    </div>
                  </div>
                </div>
              )}

              {isAgentRunningOnThis && (
                <QuestionPanel issueId={issue.id} />
              )}

              {(isAgentRunningOnThis || (!isDraft && rounds.length > 0)) && (
                <SessionTimeline
                  rounds={rounds}
                  isStreaming={isStreaming}
                  isLoading={sessionLoading}
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
