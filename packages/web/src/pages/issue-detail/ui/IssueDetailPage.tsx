import { useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { ArrowLeftIcon, PencilIcon } from 'lucide-react'
import { IssueStatus, IssueHealth } from '../../../entities/issue'
import { commentAttachmentContentPath, issueAttachmentContentPath } from '../../../entities/issue'
import { useIssue, useIssueDiff, useIssueCommits, useWorkflowTimeline } from '../../../entities/issue'
import { useAgentStatus } from '../../../entities/agent'
import { EditIssueDialog } from '../../../features/edit-issue'
import { WorkflowConvergencePanel } from '../../../widgets/issue-workflow'
import { NotFoundPage } from '../../not-found/ui/NotFoundPage'
import { BranchBar, RuntimeDecisionSurface, WorkflowView, TaskProgressPanel, WorkflowSessionsPanel, IssueWorkflowProfileEditor, LatestArtifactsPanel, PrDeliverySummary, findPublishViaPrMetadata, WorkflowProfileControl } from '../../../widgets/issue-workflow'
import { ActivityDialog } from '../../../widgets/issue-event-timeline'
import { formatTime } from '../../../shared/lib/format-time'
import { useProject, useProjectPath } from '../../../entities/project'
import { Button } from '@/shared/ui/components/button'
import { AttachmentComposer, MarkdownReader } from '@/shared/ui'
import { getLabelStyle, sortLabels } from '../../../shared/lib/label-colors'
import { CardSection } from '@/shared/ui/components/card-section'

import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { attachmentFromMetadata, formatRelativeTime } from '../model/format'
import { useIssueDetailMutations } from '../model/useIssueDetailMutations'
import { computeActionsState } from '../model/actionsState'
import { ArchivedPill, DraftPill, HealthPill, PriorityChip, WorkflowStagePill } from './pills'
import { WorkflowYamlDialog } from './WorkflowYamlDialog'
import { IssueActionsCard, extractActionsErrorMessages } from './cards/IssueActionsCard'
import { IssueDetailsCard } from './cards/IssueDetailsCard'
import { IssueDriftCard } from './cards/IssueDriftCard'
import { IssueConfigurationCard } from './cards/IssueConfigurationCard'
import { IssuePrerequisitesCard } from './cards/IssuePrerequisitesCard'
import { IssueReadinessCard } from './cards/IssueReadinessCard'

export function IssueDetailPage() {
  const { number } = useParams<{ number: string }>()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { projectId } = useProject()
  const issueNumber = parseInt(number ?? '0', 10)
  const [editOpen, setEditOpen] = useState(false)
  const [commentText, setCommentText] = useState('')
  const [forceStopConfirming, setForceStopConfirming] = useState(false)
  const [stopConfirming, setStopConfirming] = useState(false)
  const [deletingCommentId, setDeletingCommentId] = useState<string | null>(null)
  const [deleteCommentError, setDeleteCommentError] = useState<string | null>(null)

  const {
    startMutation,
    markReadyMutation,
    addPrerequisiteMutation,
    removePrerequisiteMutation,
    closeMutation,
    forceStopMutation,
    stopMutation,
    reopenMutation,
    resumeMutation,
    retryMutation,
    rerunMutation,
    addCommentMutation,
    deleteCommentMutation,
  } = useIssueDetailMutations({
    issueNumber,
    projectId,
    onForceStopSuccess: () => setForceStopConfirming(false),
    onStopSuccess: () => setStopConfirming(false),
    onAddCommentSuccess: () => setCommentText(''),
    onDeleteCommentSuccess: () => {
      setDeletingCommentId(null)
      setDeleteCommentError(null)
    },
    onDeleteCommentError: (err) => {
      setDeleteCommentError(err.message)
      setDeletingCommentId(null)
    },
  })

  const { data: issue, isLoading, isError } = useIssue(issueNumber)
  const { data: agentStatus } = useAgentStatus()
  const { data: diffData } = useIssueDiff(issueNumber)
  const { data: workflowTimeline } = useWorkflowTimeline(issueNumber, !!issue && issue.status !== IssueStatus.Backlog)

  const activeAgents = agentStatus?.activeAgents ?? []
  const isAgentRunningOnThis = activeAgents.some(a => a.issueNumber === issueNumber)

  useDocumentTitle(`Issue #${issueNumber} — Mohist`, isAgentRunningOnThis)

  const { data: commitsData } = useIssueCommits(issueNumber)

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

  const isBacklog = issue.status === IssueStatus.Backlog
  const isArchived = !!issue.archivedAt
  const workflowStage = issue.workflowStage ?? null
  const prDeliveryMetadata = findPublishViaPrMetadata(workflowTimeline)
  const actionsState = computeActionsState({
    issue,
    agentStatus: agentStatus ?? null,
    workflowTimeline: workflowTimeline ?? null,
    errorMessages: extractActionsErrorMessages({
      startMutation,
      markReadyMutation,
      closeMutation,
      forceStopMutation,
      stopMutation,
      reopenMutation,
      resumeMutation,
      retryMutation,
      rerunMutation,
    }),
  })
  const comments = [...(issue.comments ?? [])].sort(
    (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
  )
  const issueProjectId = projectId ?? issue.projectId
  const resolveIssueAttachment = (id: string) => attachmentFromMetadata(
    id,
    issue.attachments,
    `/api${issueAttachmentContentPath(issueNumber, id, issueProjectId)}`,
  )

  return (
    <>
      <div className="flex-1 min-w-0 overflow-y-auto" data-testid="issue-detail-page-container">
        <div className="max-w-4xl min-w-0 mx-auto px-4 sm:px-6 py-6">
          <button
            type="button"
            onClick={() => navigate(isArchived ? toProjectPath('/archived') : toProjectPath())}
            data-testid={isArchived ? 'back-to-archived' : 'back-to-board'}
            className="mb-4 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
          >
            <ArrowLeftIcon className="size-3.5" />
            <span>{isArchived ? 'Back to archived' : 'Back to board'}</span>
          </button>

          <div className="mb-8" data-testid="issue-detail-header">
            <div className="flex flex-wrap items-center gap-1.5 mb-2">
              <span className="text-sm font-mono text-muted-foreground/70 tabular-nums">
                #{issue.number}
              </span>
              <PriorityChip priority={issue.priority} />
              {issue.isDraft && <DraftPill />}
              {isArchived && <ArchivedPill archivedAt={issue.archivedAt} />}
              <WorkflowStagePill stage={issue.workflowStage ?? undefined} />
              <HealthPill health={issue.health} />
              {!isArchived && isAgentRunningOnThis && (
                <span
                  data-testid="running-pill"
                  className="inline-flex items-center gap-1 rounded-full bg-blue-100 text-blue-700 px-2 py-0.5 text-[10px] font-semibold"
                >
                  <span className="inline-block h-1.5 w-1.5 rounded-full bg-blue-500 animate-pulse" />
                  Running
                </span>
              )}
              {issue.approvalState?.status === 'awaiting' && (
                <span className="inline-flex items-center gap-1 rounded-full bg-amber-100 text-amber-700 px-2 py-0.5 text-[10px] font-semibold">
                  <span className="inline-block h-1.5 w-1.5 rounded-full bg-amber-500" />
                  Approval needed
                </span>
              )}
            </div>
            {isArchived && (
              <div
                data-testid="archived-banner"
                data-archived-at={issue.archivedAt ?? ''}
                className="mt-3 rounded-md border border-slate-200 bg-slate-50 px-3 py-2 text-xs text-slate-700"
              >
                Archived{issue.archivedAt ? ` ${formatTime(issue.archivedAt)}` : ''}.
                The workflow timeline, artifacts, events, and feedback below are preserved for reference.
              </div>
            )}
            <div className="flex items-start gap-3">
              <h1 className="text-2xl font-bold text-foreground flex-1 min-w-0">
                {issue.title}
              </h1>
              <div className="flex shrink-0 items-center gap-2">
                <ActivityDialog
                  issueNumber={issueNumber}
                  issueId={issue?.id}
                  workflowStatus={issue?.workflowStatus}
                />
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => setEditOpen(true)}
                  aria-label="Edit issue"
                  title="Edit issue"
                  data-testid="edit-issue-button"
                >
                  <PencilIcon className="size-4" />
                </Button>
              </div>
            </div>
            {Object.keys(issue.labels ?? {}).length > 0 && (
              <div className="mt-3 flex flex-wrap gap-1">
                {sortLabels(issue.labels).map((label) => {
                  const s = getLabelStyle(label)
                  return (
                    <span
                      key={label}
                      className={`inline-block rounded-full px-2 font-medium ${
                        s.size === 'sm' ? 'text-[11px] py-0.5' : 'text-xs py-0.5'
                      }`}
                      style={{ backgroundColor: s.bg, color: s.text }}
                    >
                      {label}
                    </span>
                  )
                })}
              </div>
            )}
            {issue.primaryEpic && (
              <button
                type="button"
                onClick={() => navigate(toProjectPath(`/epics/${issue.primaryEpic!.id}`))}
                className="mt-3 inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors"
                data-testid="primary-epic-label"
              >
                <span className="text-xs text-muted-foreground/70">Part of Epic:</span>
                <span
                  className="font-mono font-medium text-foreground/80"
                  data-testid="primary-epic-number"
                >
                  {issue.primaryEpic.number != null
                    ? `#${issue.primaryEpic.number}`
                    : `#${issue.primaryEpic.id.slice(0, 8)}`}
                </span>
                <span className="font-medium text-foreground/90">
                  {issue.primaryEpic.title}
                </span>
              </button>
            )}
            <div className="mt-2 text-xs text-muted-foreground/70">
              Created {formatTime(issue.createdAt)} · Updated {formatTime(issue.updatedAt)}
            </div>
          </div>

          <div className="mb-8" data-testid="runtime-decision-surface-frame">
            <RuntimeDecisionSurface
              issue={issue}
              timeline={workflowTimeline ?? null}
              agentStatus={agentStatus ?? null}
              hasActiveAgent={isAgentRunningOnThis}
            />
          </div>

          <BranchBar
            issueNumber={issueNumber}
            stage={workflowStage}
            isAgentRunning={isAgentRunningOnThis}
            baseBranch={issue.repository?.baseBranch}
            allowRebase={!isBacklog && !!issue.workflowRunId}
          />

          <div className="mb-8" data-testid="workflow-view-frame">
            <WorkflowView issue={issue} />
          </div>

          {prDeliveryMetadata && (
            <div className="mb-8" data-testid="pr-delivery-summary-frame">
              <PrDeliverySummary timeline={workflowTimeline} />
            </div>
          )}

          <div className="mb-8" data-testid="workflow-profile-editor-frame">
            <IssueWorkflowProfileEditor issueNumber={issueNumber} />
          </div>

          {diffData?.available === true && (
            <div className="min-w-0 rounded-lg bg-white p-4 mb-8 border-l-2 border-gray-200" data-testid="diff-summary-banner">
              <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1 text-sm">
                <span className="min-w-0 text-gray-500 break-words">
                  <span className="font-medium text-gray-700 break-all" title={diffData.head} data-testid="diff-summary-head">{diffData.head}</span>
                  {' wants to merge into '}
                  <span className="font-medium text-gray-700 break-all" title={diffData.base} data-testid="diff-summary-base">{diffData.base}</span>
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
              <div className="mt-2 flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-xs text-gray-400">
                <span className="min-w-0 break-words">showing merge-base → <span className="break-all" title={diffData.head}>{diffData.head}</span></span>
                <span>·</span>
                <span>Workspace retained</span>
              </div>
            </div>
          )}

          <div className="grid min-w-0 grid-cols-1 lg:grid-cols-3 gap-8" data-testid="issue-detail-content-grid">
            <div className="min-w-0 lg:col-span-2 space-y-8">
              {issue.body && (
                  <div className="rounded-lg bg-white p-4" data-testid="description-section">
                    <h2 className="text-sm font-semibold text-gray-700 mb-2">Description</h2>
                    <MarkdownReader
                      content={issue.body}
                      mode="collapsible"
                      collapsedHeight={600}
                      baseHeadingLevel={2}
                      resolveAttachment={resolveIssueAttachment}
                    />
                  </div>
              )}

              {issue.workflowRunId && (
                <WorkflowYamlDialog workflowRunId={issue.workflowRunId} isArchived={isArchived} />
              )}

              {diffData?.available === true && (
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
                      onClick={() => navigate(toProjectPath(`/issues/${issueNumber}/files`))}
                      className="border-blue-200 text-blue-600 hover:border-blue-300 hover:text-blue-700"
                    >
                      View files
                    </Button>
                  </div>
                </div>
              )}

              {commitsData?.available === true && (
                <div className="rounded-lg bg-white p-4" data-testid="commits-section">
                  <div className="flex items-center justify-between mb-3">
                    <h2 className="text-sm font-semibold text-gray-700">
                      Commits ({commitsData.summary.commits})
                    </h2>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => navigate(toProjectPath(`/issues/${issueNumber}/files`))}
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
                <div className="rounded-lg bg-white p-4">
                  <p className="text-sm text-gray-400">
                    {diffData?.available === false && diffData.message}
                    {diffData?.available === false && commitsData?.available === false && ' / '}
                    {commitsData?.available === false && commitsData.message}
                  </p>
                </div>
              )}

              <div className="rounded-lg bg-white p-4" data-testid="comments-section">
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
                            <MarkdownReader
                              content={comment.body}
                              baseHeadingLevel={3}
                              resolveAttachment={(id) => attachmentFromMetadata(
                                id,
                                comment.attachments,
                                `/api${commentAttachmentContentPath(issueNumber, comment.id, id, issueProjectId)}`,
                              )}
                            />
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
                  <AttachmentComposer
                    projectId={issueProjectId}
                    value={commentText}
                    onChange={setCommentText}
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

            <div className="min-w-0 space-y-6" data-testid="issue-detail-right-rail">
              <IssueDetailsCard issue={issue} />

              <LatestArtifactsPanel issueNumber={issueNumber} workflowRunId={issue.workflowRunId} />

              <div data-testid="issue-workflow-profile-control-frame">
                <WorkflowProfileControl issue={issue} />
              </div>

              {issue.drift?.drifted && (
                <IssueDriftCard drift={issue.drift} />
              )}

              {issue.health === IssueHealth.Interrupted && (
                <CardSection title="Workflow Interrupted" tone="orange">
                  <p className="text-xs text-orange-700">
                    The workflow was interrupted (e.g. server restart). Your progress has been preserved.
                    Click &quot;Resume&quot; below to continue from where it left off.
                  </p>
                </CardSection>
              )}

              {(issue.health === IssueHealth.Blocked || issue.convergence) && (
                <WorkflowConvergencePanel convergence={issue.convergence} />
              )}

              {(!isBacklog || issue.workflowRunId) && (
                <CardSection title="Runtime/Sessions">
                  <div className="space-y-4">
                    {!isBacklog && workflowStage && (
                      <TaskProgressPanel
                        issueNumber={issueNumber}
                        currentStage={workflowStage}
                        isAgentRunning={isAgentRunningOnThis}
                      />
                    )}

                    {!isBacklog && issue.workflowRunId && (
                      <WorkflowSessionsPanel
                        issueNumber={issueNumber}
                        workflowRunId={issue.workflowRunId}
                      />
                    )}
                  </div>
                </CardSection>
              )}

              <IssueConfigurationCard
                issue={{ number: issue.number, model: issue.model, stageModels: issue.stageModels, prerequisites: issue.prerequisites, isBacklog }}
                mutations={{ addPrerequisiteMutation, removePrerequisiteMutation }}
              />

              <IssueActionsCard
                state={actionsState}
                mutations={{
                  startMutation,
                  markReadyMutation,
                  closeMutation,
                  forceStopMutation,
                  stopMutation,
                  reopenMutation,
                  resumeMutation,
                  retryMutation,
                  rerunMutation,
                }}
                confirmState={{
                  forceStopConfirming,
                  setForceStopConfirming,
                  stopConfirming,
                  setStopConfirming,
                }}
                onAskAgent={() => navigate(toProjectPath('/agent-sessions/new?issue=' + encodeURIComponent(issueNumber)))}
              />

              {issue.prerequisites && issue.prerequisites.length > 0 && (
                <IssuePrerequisitesCard prerequisites={issue.prerequisites} />
              )}

              {isBacklog && (
                <IssueReadinessCard issue={issue} />
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
