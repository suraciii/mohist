import { useState, useEffect, useRef } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import Markdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { IssueStatus, IssueHealth, type RecoveryProjection } from '../../../entities/issue'
import { addComment, addPrerequisite, closeIssue, deleteComment, forceStopIssue, removePrerequisite, reopenIssue, rerunIssue, resumeIssue, retryIssue, startIssue } from '../../../entities/issue'
import { useIssue, useIssueDiff, useIssueCommits, useWorkflowTimeline, useWorkflowYaml } from '../../../entities/issue'
import { useAgentStatus } from '../../../entities/agent'
import { EditIssueDialog } from '../../../features/edit-issue'
import { WorkflowConvergencePanel } from '../../../widgets/issue-workflow'
import { NotFoundPage } from '../../not-found/ui/NotFoundPage'
import { IssueModelSelector } from '../../../features/select-issue-model'
import { BranchBar, WorkflowView, TaskProgressPanel } from '../../../widgets/issue-workflow'
import { SessionList } from '../../../widgets/coder-session'
import { formatTime } from '../../../shared/lib/format-time'
import { statusBadge, statusLabel } from '../../../entities/issue/lib/status-badge'
import { useProject } from '../../../entities/project'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Textarea } from '@/shared/ui/components/textarea'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/shared/ui/components/dialog'

import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

function MarkdownContent({ content }: { content: string }) {
  return (
    <Markdown
      remarkPlugins={[remarkGfm]}
      components={{
        code({ children, className }) {
          const match = /language-(\w+)/.exec(className ?? '')
          const isInline = !match && !className
          if (isInline) {
            return <code className="px-1 py-0.5 bg-gray-100 rounded text-gray-800 text-xs font-mono">{children}</code>
          }
          return (
            <code className={`${className ?? ''} block overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono`}>
              {children}
            </code>
          )
        },
        pre({ children }) {
          return <pre className="overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono">{children}</pre>
        },
      }}
    >
      {content}
    </Markdown>
  )
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

function formatStageName(stage: string | null | undefined): string {
  if (!stage) return '-'
  return stage
    .split(/[_-]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ')
}

function WorkflowYamlDialog({ issueNumber }: { issueNumber: number }) {
  const [open, setOpen] = useState(false)
  const { data, isLoading } = useWorkflowYaml(issueNumber, open)

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        className="w-full text-left rounded-lg border border-gray-200 bg-white p-3 hover:bg-gray-50 transition-colors flex items-center justify-between"
      >
        <span className="text-sm text-gray-600">Workflow Definition (YAML)</span>
        <span className="text-xs text-blue-600">View</span>
      </button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-4xl max-h-[80vh] overflow-hidden flex flex-col p-0">
          <DialogHeader>
            <DialogTitle>Workflow Definition</DialogTitle>
          </DialogHeader>
          <div className="flex-1 overflow-auto px-4 pb-4">
            {isLoading ? (
              <div className="space-y-2">
                {[1, 2, 3, 4, 5].map((i) => (
                  <div key={i} className="h-4 bg-gray-100 rounded animate-pulse" />
                ))}
              </div>
            ) : data?.yaml ? (
              <pre className="text-xs font-mono leading-relaxed text-gray-800 whitespace-pre-wrap break-all bg-gray-50 rounded-md p-4 border">
                {data.yaml}
              </pre>
            ) : (
              <p className="text-sm text-gray-400">No workflow YAML available.</p>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}

export function IssueDetailPage() {
  const { number } = useParams<{ number: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const issueNumber = parseInt(number ?? '0', 10)
  const [editOpen, setEditOpen] = useState(false)
  const [commentText, setCommentText] = useState('')
  const [forceStopConfirming, setForceStopConfirming] = useState(false)
  const forceStopPanelRef = useRef<HTMLDivElement>(null)

  const [descriptionExpanded, setDescriptionExpanded] = useState(false)
  const descriptionBodyRef = useRef<HTMLDivElement>(null)
  const [isOverflowing, setIsOverflowing] = useState(false)

  const [prereqInput, setPrereqInput] = useState('')
  const [prereqError, setPrereqError] = useState<string | null>(null)

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
  const { data: workflowTimeline } = useWorkflowTimeline(issueNumber, !!issue && issue.status !== IssueStatus.Backlog)

  useEffect(() => {
    if (descriptionBodyRef.current) {
      setIsOverflowing(descriptionBodyRef.current.scrollHeight > 600)
    }
  }, [issue?.body])

  const activeAgents = agentStatus?.activeAgents ?? []
  const isAgentRunningOnThis = activeAgents.some(a => a.issueNumber === issueNumber)
  const recovery: RecoveryProjection | null | undefined = issue?.recovery
  const recoveryAllowedActions = recovery?.allowedActions ?? []
  const recoveryAttemptState = recovery?.latestAttemptState
  const recoveryCanWait = recoveryAllowedActions.includes('wait')
  const recoveryCanStop = recoveryAllowedActions.includes('stop')

  useDocumentTitle(`Issue #${issueNumber} — Mohist`, isAgentRunningOnThis)

  const { data: commitsData } = useIssueCommits(issueNumber)
  const showCheckRepairActions = false

  const startMutation = useMutation({
    mutationFn: () => startIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
    onError: (err: Error) => {
      if (err.message.includes('waiting for')) {
        queryClient.invalidateQueries({ queryKey: ['issues'] })
      }
    },
  })

  const addPrerequisiteMutation = useMutation({
    mutationFn: (prerequisiteNumber: number) => addPrerequisite(issueNumber, prerequisiteNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  })

  const removePrerequisiteMutation = useMutation({
    mutationFn: (prerequisiteNumber: number) => removePrerequisite(issueNumber, prerequisiteNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  })

  const closeMutation = useMutation({
    mutationFn: () => closeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const forceStopMutation = useMutation({
    mutationFn: () => forceStopIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      setForceStopConfirming(false)
    },
  })

  const reopenMutation = useMutation({
    mutationFn: () => reopenIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const resumeMutation = useMutation({
    mutationFn: () => resumeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const retryMutation = useMutation({
    mutationFn: () => retryIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const rerunMutation = useMutation({
    mutationFn: () => rerunIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const addCommentMutation = useMutation({
    mutationFn: (body: string) => addComment(issueNumber, body, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      setCommentText('')
    },
  })

  const [deletingCommentId, setDeletingCommentId] = useState<string | null>(null)
  const [deleteCommentError, setDeleteCommentError] = useState<string | null>(null)

  const deleteCommentMutation = useMutation({
    mutationFn: (commentId: string) => deleteComment(issueNumber, commentId, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      setDeletingCommentId(null)
      setDeleteCommentError(null)
    },
    onError: (err) => {
      setDeleteCommentError(err instanceof Error ? err.message : 'Failed to delete comment')
      setDeletingCommentId(null)
    },
  })

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

  const maxConcurrent = agentStatus?.capacity?.max ?? Infinity
  const thisAgent = activeAgents.find(a => a.issueNumber === issueNumber)
  const agentProgress = thisAgent?.progress
  const isCapacityFull = activeAgents.length >= maxConcurrent
  const runnerUnavailable = agentStatus?.runnerAvailable === false
  const isBacklog = issue.status === IssueStatus.Backlog
  const workflowStage = issue.workflowStage ?? null
  const workflowAllowedActions = workflowTimeline?.availableActions.map((action) => action.name) ?? []
  const allowedActions = Array.from(new Set([...recoveryAllowedActions, ...workflowAllowedActions]))
  const canRetryWorkflow = allowedActions.includes('retry')
  const canResumeWorkflow = allowedActions.includes('resume')
  const canRerunWorkflow = allowedActions.includes('rerun')
  const comments = [...(issue.comments ?? [])].sort(
    (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
  )

  return (
    <>
      <div className="flex-1 overflow-y-auto">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 py-6">
          <Button
            variant="link"
            onClick={() => navigate('/')}
            className="mb-4 inline-flex h-auto gap-1 px-0 text-muted-foreground"
          >
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path
                fillRule="evenodd"
                d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
                clipRule="evenodd"
              />
            </svg>
            Back to board
          </Button>

          <div className="mb-6">
            <div className="flex items-center gap-2 mb-1">
              <span className="text-sm font-mono text-gray-400">#{issue.number}</span>
              <span
                className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${statusBadge(issue.health)}`}
              >
                {statusLabel(issue.health)}
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
              <Button
                variant="ghost"
                size="icon-sm"
                onClick={() => setEditOpen(true)}
                title="Edit issue"
              >
                <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
                  <path d="M2.695 14.763l-1.262 3.154a.5.5 0 00.65.65l3.155-1.262a4 4 0 001.343-.885L17.5 5.5a2.121 2.121 0 00-3-3L3.58 13.42a4 4 0 00-.885 1.343z" />
                </svg>
              </Button>
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
            {issue.primaryEpic && (
              <Button
                variant="link"
                onClick={() => navigate(`/epic/${issue.primaryEpic!.id}`)}
                className="mt-3 inline-flex h-auto gap-2 px-0"
              >
                <span className="text-gray-400">Part of Epic:</span>
                <span className="font-medium">#{issue.primaryEpic.id.slice(0, 8)}</span>
                <span>{issue.primaryEpic.title}</span>
              </Button>
            )}
          </div>

          <WorkflowView issue={issue} />

          {diffData?.available === true && (
            <div className="rounded-lg border border-gray-200 bg-white p-4 mb-6">
              <div className="flex items-center gap-4 text-sm">
                <span className="text-gray-500">
                  <span className="font-medium text-gray-700">{diffData.head}</span>
                  {' wants to merge into '}
                  <span className="font-medium text-gray-700">{diffData.base}</span>
                </span>
                <span className="text-gray-300">·</span>
                <span className="text-gray-500">
                  <span className="font-medium text-gray-700">{diffData.ahead}</span> ahead
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
              </div>
              <div className="mt-2 flex items-center gap-3 text-xs text-gray-400">
                <span>showing merge-base → {diffData.head}</span>
                <span>·</span>
                <span>Worktree retained</span>
              </div>
            </div>
          )}

          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            <div className="lg:col-span-2 space-y-6">
              <BranchBar issueNumber={issueNumber} stage={workflowStage} isAgentRunning={isAgentRunningOnThis} />
              {issue.body && (
                  <div className="rounded-lg border border-gray-200 bg-white p-4">
                    <h2 className="text-sm font-semibold text-gray-700 mb-2">Description</h2>
                    <div className="relative">
                      <div ref={descriptionBodyRef} className={descriptionExpanded ? '' : 'max-h-[600px] overflow-hidden'}>
                        <MarkdownContent content={issue.body} />
                      </div>
                      {!descriptionExpanded && (
                        <div className="absolute bottom-0 left-0 right-0 h-20 bg-gradient-to-t from-white to-transparent pointer-events-none" />
                      )}
                    </div>
                    {isOverflowing && (
                      <div className="mt-2">
                        <Button
                          variant="link"
                          size="xs"
                          onClick={() => setDescriptionExpanded(!descriptionExpanded)}
                        >
                          {descriptionExpanded ? 'Collapse' : 'Expand'}
                        </Button>
                      </div>
                    )}
                  </div>
              )}

              {issue.workflowRunId && (
                <WorkflowYamlDialog issueNumber={issueNumber} />
              )}

              {diffData?.available === true && (
                <div className="rounded-lg border border-gray-200 bg-white p-4">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3 text-sm text-gray-500">
                      <span>
                        <span className="font-medium text-gray-700">{diffData.head}</span>
                        {' → '}
                        <span className="font-medium text-gray-700">{diffData.base}</span>
                      </span>
                      <span className="text-gray-300">·</span>
                      <span>{diffData.summary.filesChanged} files changed · +{diffData.summary.additions} -{diffData.summary.deletions}</span>
                    </div>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => navigate(`/issue/${issueNumber}/files`)}
                      className="border-blue-200 text-blue-600 hover:border-blue-300 hover:text-blue-700"
                    >
                      View files
                    </Button>
                  </div>
                </div>
              )}

              {commitsData?.available === true && (
                <div className="rounded-lg border border-gray-200 bg-white p-4">
                  <div className="flex items-center justify-between mb-3">
                    <h2 className="text-sm font-semibold text-gray-700">
                      Commits ({commitsData.summary.commits})
                    </h2>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => navigate(`/issue/${issueNumber}/files`)}
                      className="border-blue-200 text-blue-600 hover:border-blue-300 hover:text-blue-700"
                    >
                      View all commits
                    </Button>
                  </div>
                  {commitsData.commits.length === 0 ? (
                    <p className="text-sm text-gray-400">No commits yet.</p>
                  ) : (
                    <div className="space-y-2">
                      {commitsData.commits.slice(0, 5).map((commit) => (
                        <div
                          key={commit.hash}
                          className="flex items-center justify-between text-sm group"
                        >
                          <div className="flex items-center gap-3 flex-1 min-w-0">
                            <code className="text-xs text-gray-500 font-mono shrink-0">{commit.shortHash}</code>
                            <span className="text-gray-700 truncate">{commit.message}</span>
                          </div>
                          <span className="text-xs text-gray-400 ml-3 shrink-0">{formatRelativeTime(commit.date)}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}

              {(diffData?.available === false || commitsData?.available === false) && (
                <div className="rounded-lg border border-gray-200 bg-white p-4">
                  <p className="text-sm text-gray-400">
                    {diffData?.available === false && diffData.message}
                    {diffData?.available === false && commitsData?.available === false && ' / '}
                    {commitsData?.available === false && commitsData.message}
                  </p>
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
                        <div className="flex items-start justify-between gap-2">
                          <div className="flex-1">
                            <div className="text-xs text-gray-400 mb-1">
                              {formatTime(comment.createdAt)}
                            </div>
                            <div className="text-sm text-gray-700"><MarkdownContent content={comment.body} /></div>
                          </div>
                          <Button
                            variant="ghost"
                            size="xs"
                            onClick={() => {
                              setDeleteCommentError(null)
                              if (window.confirm('Delete this comment?')) {
                                setDeletingCommentId(comment.id)
                                deleteCommentMutation.mutate(comment.id)
                              }
                            }}
                            disabled={deletingCommentId === comment.id}
                            className="text-muted-foreground hover:text-red-500"
                            title="Delete comment"
                          >
                            {deletingCommentId === comment.id ? 'Deleting...' : 'Delete'}
                          </Button>
                        </div>
                        {deleteCommentError && deletingCommentId === null && (
                          <div className="mt-1 text-xs text-red-500">{deleteCommentError}</div>
                        )}
                      </div>
                    ))}
                  </div>
                )}

                <div className="mt-4 pt-3 border-t border-gray-100">
                  <Textarea
                    value={commentText}
                    onChange={(e) => setCommentText(e.target.value)}
                    placeholder="Add a comment..."
                    rows={2}
                    className="resize-none"
                  />
                  <div className="flex items-center justify-between mt-2">
                    {addCommentMutation.error && (
                      <span className="text-xs text-red-500">
                        {addCommentMutation.error.message}
                      </span>
                    )}
                    <div className="ml-auto">
                      <Button
                        onClick={() => addCommentMutation.mutate(commentText)}
                        disabled={!commentText.trim() || addCommentMutation.isPending}
                      >
                        {addCommentMutation.isPending ? 'Sending...' : 'Comment'}
                      </Button>
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
                    <dt className="text-gray-500">Issue Stage</dt>
                    <dd className="text-gray-900 font-medium">{formatStageName(issue.status)}</dd>
                  </div>
                  {issue.workflowProfileId && (
                    <div className="flex justify-between">
                      <dt className="text-gray-500">Workflow Profile</dt>
                      <dd className="text-gray-900 font-mono text-xs">{issue.workflowProfileId}</dd>
                    </div>
                  )}
                  {workflowStage && (
                    <div className="flex justify-between">
                      <dt className="text-gray-500">Workflow Stage</dt>
                      <dd className="text-gray-900 font-medium">{formatStageName(workflowStage)}</dd>
                    </div>
                  )}
                  <div className="flex justify-between">
                    <dt className="text-gray-500">Status</dt>
                    <dd
                      className={`font-medium capitalize ${statusBadge(issue.health)}`}
                    >
                      {statusLabel(issue.health)}
                    </dd>
                  </div>
                  {issue.projectName && (
                    <div className="flex justify-between">
                      <dt className="text-gray-500">Project</dt>
                      <dd className="text-gray-900">{issue.projectName}</dd>
                    </div>
                  )}
                  {issue.repository && (
                    <div className="flex justify-between">
                      <dt className="text-gray-500">Repository</dt>
                      <dd className="text-gray-900">
                        {issue.repository.name}
                        {issue.repository.path && (
                          <span className="text-gray-400 text-xs ml-1">({issue.repository.path})</span>
                        )}
                        {issue.repository.remote && (
                          <span className="text-gray-400 text-xs ml-1">(remote)</span>
                        )}
                      </dd>
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

              {issue.drift?.drifted && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-4">
                  <h2 className="text-sm font-semibold mb-2 text-amber-800">
                    Base Drift Detected
                  </h2>
                  <div className="space-y-1.5 text-xs">
                    {issue.drift.decision && (
                      <div className="flex justify-between">
                        <span className="text-gray-500">Rebase decision:</span>
                        <span className={`font-medium ${issue.drift.decision === 'needs-attention' ? 'text-red-600' : issue.drift.decision === 'defer' ? 'text-orange-600' : 'text-amber-600'}`}>
                          {issue.drift.decision === 'needs-attention' ? 'Needs Attention' :
                           issue.drift.decision === 'defer' ? 'Deferred' :
                           issue.drift.decision === 'suggest' ? 'Suggested' :
                           issue.drift.decision === 'enqueue' ? 'Enqueued' : issue.drift.decision}
                        </span>
                      </div>
                    )}
                    {issue.drift.deferReason && (
                      <div className="flex justify-between">
                        <span className="text-gray-500">Defer reason:</span>
                        <span className="text-orange-600">
                          {issue.drift.deferReason === 'agent-running' ? 'Agent running' :
                           issue.drift.deferReason === 'task-running' ? 'Task running' :
                           issue.drift.deferReason === 'waiting-for-task-boundary' ? 'Waiting for task boundary' :
                           issue.drift.deferReason === 'rebase-already-pending' ? 'Rebase already pending' :
                           issue.drift.deferReason}
                        </span>
                      </div>
                    )}
                    {issue.drift.safeWindow !== null && (
                      <div className="flex justify-between">
                        <span className="text-gray-500">Safe window:</span>
                        <span className={issue.drift.safeWindow ? 'text-green-600' : 'text-gray-600'}>
                          {issue.drift.safeWindow ? 'Yes' : 'No'}
                        </span>
                      </div>
                    )}
                    {issue.drift.observedBaseSha && issue.drift.currentBaseSha && (
                      <div className="flex justify-between">
                        <span className="text-gray-500">Base:</span>
                        <span className="font-mono text-gray-700">
                          {issue.drift.observedBaseSha.slice(0, 7)} → {issue.drift.currentBaseSha.slice(0, 7)}
                        </span>
                      </div>
                    )}
                    {issue.drift.nextAction && (
                      <div className="mt-2 pt-2 border-t border-orange-200 text-orange-700">
                        {issue.drift.nextAction}
                      </div>
                    )}
                    {issue.drift.conflicts && issue.drift.conflicts.length > 0 && (
                      <div className="mt-2 pt-2 border-t border-red-200">
                        <span className="font-medium text-red-800">Conflicts: </span>
                        {issue.drift.conflicts.map((f) => (
                          <span key={f} className="font-mono text-red-700 ml-1">{f}</span>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              )}

              {issue.health === IssueHealth.Interrupted && (
                <div className="rounded-lg border border-orange-200 bg-orange-50 p-4">
                  <h2 className="text-sm font-semibold text-orange-800 mb-2">
                    Workflow Interrupted
                  </h2>
                  <p className="text-xs text-orange-600 mb-3">
                    The workflow was interrupted (e.g. server restart). Your progress has been preserved.
                    Click &quot;Resume Workflow&quot; below to continue from where it left off.
                  </p>
                </div>
              )}

              {(issue.health === IssueHealth.Blocked || issue.convergence) && (
                <WorkflowConvergencePanel convergence={issue.convergence} />
              )}

              <div className="rounded-lg border border-gray-200 bg-white p-4">
                <h2 className="text-sm font-semibold text-gray-700 mb-3">Actions</h2>
                <div className="space-y-2">
                  {isBacklog && (
                    <>
                      {issue.startEligibility?.waitingForCompletion?.length ? (
                        <div className="rounded-md bg-amber-50 border border-amber-200 px-3 py-2 text-sm text-amber-700">
                          {issue.startEligibility.message ?? `Waiting for #${issue.startEligibility.waitingForCompletion[0].number}`}
                        </div>
                      ) : (
                        <div className="space-y-2">
                          {runnerUnavailable && (
                            <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-700">
                              {agentStatus?.runnerMessage ?? 'No runner is connected. Start a runner before starting workflow work.'}
                            </div>
                          )}
                          <Button
                            onClick={() => startMutation.mutate()}
                            disabled={runnerUnavailable || isAgentRunningOnThis || isCapacityFull || startMutation.isPending}
                            className="w-full"
                          >
                            {startMutation.isPending
                              ? 'Starting...'
                              : runnerUnavailable
                                ? 'Runner unavailable'
                                : isAgentRunningOnThis
                                  ? 'Agent running...'
                                  : isCapacityFull
                                    ? 'Capacity full...'
                                    : 'Start'}
                          </Button>
                        </div>
                      )}
                    </>
                  )}

                  {issue.health === IssueHealth.Active && !isBacklog && !isAgentRunningOnThis && (
                    <Button
                      variant="outline"
                      onClick={() => closeMutation.mutate()}
                      disabled={closeMutation.isPending}
                      className="w-full"
                    >
                      {closeMutation.isPending ? 'Closing...' : 'Close'}
                    </Button>
                  )}

                  {(isAgentRunningOnThis || recoveryCanWait || recoveryCanStop) && (
                    <div ref={forceStopPanelRef} className="rounded-lg border border-blue-200 bg-blue-50 p-3 space-y-2">
                      <div className="flex items-center gap-2">
                        <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
                        <span className="text-xs font-semibold text-blue-800">
                          {agentProgress
                            ? `${agentProgress.stage.charAt(0).toUpperCase() + agentProgress.stage.slice(1)} Stage`
                            : recoveryCanWait
                              ? 'Waiting for running work'
                              : 'Running...'}
                        </span>
                      </div>
                      {recoveryAttemptState === 'running' && recovery?.currentWorkItem && (
                        <div className="text-xs text-blue-700">
                          Current: {recovery.currentWorkItem.type} — {recovery.currentWorkItem.title}
                        </div>
                      )}
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
                      {recoveryCanStop && (
                        <Button
                          onClick={() => {
                            if (forceStopConfirming) {
                              forceStopMutation.mutate()
                            } else {
                              setForceStopConfirming(true)
                            }
                          }}
                          disabled={forceStopMutation.isPending}
                          variant={forceStopConfirming ? 'destructive' : 'outline'}
                          className={`w-full ${
                            forceStopConfirming
                              ? ''
                              : 'border-red-300 text-red-600 hover:bg-red-50'
                          }`}
                        >
                          {forceStopMutation.isPending
                            ? 'Stopping...'
                            : forceStopConfirming
                              ? 'Confirm Force Stop'
                              : 'Force Stop'}
                        </Button>
                      )}
                      {forceStopMutation.error && (
                        <div className="text-xs text-red-600">
                          {forceStopMutation.error.message}
                        </div>
                      )}
                    </div>
                  )}

                  {(issue.health === IssueHealth.Blocked || issue.health === IssueHealth.Interrupted) && (() => {
                    const canRetry = canRetryWorkflow
                    const canResume = canResumeWorkflow
                    const canRerun = canRerunWorkflow
                    const canInspect = allowedActions.includes('inspect')
                    const isInterrupted = recoveryAttemptState === 'interrupted'
                    const showProjectedCheckRepairActions = showCheckRepairActions && (canRetry || canRerun)

                    return (
                      <div className="space-y-2">
                        {issue.blockedReason && (
                          <div className="rounded-md bg-red-50 border border-red-200 px-3 py-2 text-sm text-red-700">
                            {issue.blockedReason}
                          </div>
                        )}
                        {isInterrupted && (
                          <div className="rounded-md bg-orange-50 border border-orange-200 px-3 py-2 text-xs text-orange-700">
                            Execution was interrupted. This is not a failed result — the work item can be resumed or rerun.
                          </div>
                        )}
                        {showProjectedCheckRepairActions ? null : (
                          <>
                            {canRetry && (
                              <Button
                                variant="destructive"
                                onClick={() => retryMutation.mutate()}
                                disabled={retryMutation.isPending}
                                className="w-full"
                              >
                                {retryMutation.isPending ? 'Retrying...' : 'Retry'}
                              </Button>
                            )}
                            {canResume && (
                              <Button
                                onClick={() => resumeMutation.mutate()}
                                disabled={resumeMutation.isPending}
                                className="w-full bg-orange-500 hover:bg-orange-600 text-white"
                              >
                                {resumeMutation.isPending ? 'Resuming...' : 'Resume'}
                              </Button>
                            )}
                            {canRerun && (
                              <Button
                                variant="outline"
                                onClick={() => rerunMutation.mutate()}
                                disabled={rerunMutation.isPending}
                                className="w-full"
                              >
                                {rerunMutation.isPending ? 'Rerunning...' : 'Rerun Stage'}
                              </Button>
                            )}
                            {canInspect && recovery?.currentWorkItem && (
                              <div className="text-xs text-gray-500">
                                Current: {recovery.currentWorkItem.type} — {recovery.currentWorkItem.title}
                              </div>
                            )}
                          </>
                        )}
                      </div>
                    )
                  })()}

                  {!isBacklog && issue.status !== IssueStatus.Done && workflowStage && !isAgentRunningOnThis && canRerunWorkflow && issue.health !== IssueHealth.Blocked && issue.health !== IssueHealth.Interrupted && !showCheckRepairActions && (
                    <Button
                      variant="outline"
                      onClick={() => rerunMutation.mutate()}
                      disabled={rerunMutation.isPending}
                      className="w-full"
                    >
                      {rerunMutation.isPending ? 'Rerunning...' : 'Rerun Stage'}
                    </Button>
                  )}

                  {(closeMutation.error || reopenMutation.error || startMutation.error || rerunMutation.error || retryMutation.error) && (
                    <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                      {closeMutation.error?.message ||
                        reopenMutation.error?.message ||
                        startMutation.error?.message ||
                        rerunMutation.error?.message ||
                        retryMutation.error?.message}
                    </div>
                  )}

                  {!isAgentRunningOnThis && activeAgents.length > 0 && !isBacklog && (
                    <div className="text-xs text-gray-400 text-center">
                      {activeAgents.length} agent{activeAgents.length > 1 ? 's' : ''} running on other issues
                    </div>
                  )}

                  <div className="pt-2 border-t border-gray-100">
                    <IssueModelSelector issueNumber={issue.number} currentWorkflowRunId={issue.workflowRunId} currentModel={issue.model} currentAgentConfig={issue.agentConfig} currentStageModels={issue.stageModels} />
                  </div>
                </div>
              </div>

              {issue.prerequisites && issue.prerequisites.length > 0 && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-4">
                  <h2 className="text-sm font-semibold text-amber-800 mb-2">Start Prerequisites</h2>
                  <div className="space-y-2">
                    {issue.prerequisites.map((prereq) => (
                      <div key={prereq.number} className="flex items-center justify-between text-sm">
                        <span className="text-amber-700">
                          #{prereq.number} {prereq.title}
                        </span>
                        {prereq.completed ? (
                          <span className="inline-flex items-center gap-1 text-xs font-medium text-green-600 bg-green-100 px-1.5 py-0.5 rounded">
                            Completed
                          </span>
                        ) : (
                          <span className="inline-flex items-center gap-1 text-xs font-medium text-amber-600 bg-amber-100 px-1.5 py-0.5 rounded">
                            Waiting
                          </span>
                        )}
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {isBacklog && (
                <div className="rounded-lg border border-gray-200 bg-white p-4">
                  <h2 className="text-sm font-semibold text-gray-700 mb-2">Add Prerequisite</h2>
                  <div className="flex gap-2">
                    <Input
                      type="number"
                      value={prereqInput}
                      onChange={(e) => {
                        setPrereqInput(e.target.value)
                        setPrereqError(null)
                      }}
                      placeholder="Issue #"
                      className="flex-1"
                    />
                    <Button
                      onClick={() => {
                        const num = parseInt(prereqInput, 10)
                        if (isNaN(num) || num === issueNumber) {
                          setPrereqError('Enter a valid issue number')
                          return
                        }
                        setPrereqError(null)
                        addPrerequisiteMutation.mutate(num)
                        setPrereqInput('')
                      }}
                      disabled={!prereqInput || addPrerequisiteMutation.isPending}
                      className="bg-amber-600 hover:bg-amber-700 text-white"
                    >
                      {addPrerequisiteMutation.isPending ? 'Adding...' : 'Add'}
                    </Button>
                  </div>
                  {prereqError && (
                    <p className="mt-1 text-xs text-red-600">{prereqError}</p>
                  )}
                  {addPrerequisiteMutation.error && (
                    <p className="mt-1 text-xs text-red-600">
                      {(addPrerequisiteMutation.error as Error).message?.includes('circular')
                        ? 'Circular prerequisite: this would create a cycle'
                        : (addPrerequisiteMutation.error as Error).message}
                    </p>
                  )}
                  {issue.prerequisites && issue.prerequisites.length > 0 && (
                    <div className="mt-3 pt-3 border-t border-gray-100">
                      <p className="text-xs text-gray-500 mb-2">Remove prerequisite:</p>
                      <div className="flex flex-wrap gap-1">
                        {issue.prerequisites.map((prereq) => (
                          <Button
                            key={prereq.number}
                            variant="secondary"
                            size="xs"
                            onClick={() => removePrerequisiteMutation.mutate(prereq.number)}
                            disabled={removePrerequisiteMutation.isPending}
                          >
                            #{prereq.number}
                            <span className="text-gray-400">×</span>
                          </Button>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}

              {!isBacklog && workflowStage && (
                <TaskProgressPanel
                  issueNumber={issueNumber}
                  currentStage={workflowStage}
                  isAgentRunning={isAgentRunningOnThis}
                />
              )}

              {!isBacklog && workflowStage && (
                <SessionList
                  issueNumber={issueNumber}
                  currentStage={workflowStage}
                  isLive={isAgentRunningOnThis}
                />
              )}
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
