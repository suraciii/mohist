import { useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { ArrowLeftIcon, PencilIcon } from 'lucide-react'
import { IssueStatus, IssueHealth } from '../../../entities/issue'
import { issueAttachmentContentPath } from '../../../entities/issue'
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
import { getLabelStyle, sortLabels } from '../../../shared/lib/label-colors'
import { CardSection } from '@/shared/ui/components/card-section'

import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { attachmentFromMetadata } from '../model/format'
import { useIssueDetailMutations } from '../model/useIssueDetailMutations'
import { deriveRuntimeDecision } from '../../../widgets/issue-workflow/model/derive-runtime-decision'
import { ArchivedPill, DraftPill, PriorityChip, RuntimeSummaryPill } from './pills'
import { WorkflowYamlDialog } from './WorkflowYamlDialog'
import { IssueActionsCard } from './cards/IssueActionsCard'
import { IssueDetailsCard } from './cards/IssueDetailsCard'
import { IssueDriftCard } from './cards/IssueDriftCard'
import { IssueConfigurationCard } from './cards/IssueConfigurationCard'
import { IssuePrerequisitesCard } from './cards/IssuePrerequisitesCard'
import { IssueReadinessCard } from './cards/IssueReadinessCard'
import { IssueDescriptionSection } from './sections/IssueDescriptionSection'
import { IssueDiffFilesSection } from './sections/IssueDiffFilesSection'
import { IssueCommitsSection } from './sections/IssueCommitsSection'
import { IssueCommentsSection } from './sections/IssueCommentsSection'

export function IssueDetailPage() {
  const { number } = useParams<{ number: string }>()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { projectId } = useProject()
  const issueNumber = parseInt(number ?? '0', 10)
  const [editOpen, setEditOpen] = useState(false)
  const [commentText, setCommentText] = useState('')
  const [deletingCommentId, setDeletingCommentId] = useState<string | null>(null)
  const [deleteCommentError, setDeleteCommentError] = useState<string | null>(null)

  const {
    startMutation,
    approveMutation,
    sendBackMutation,
    markReadyMutation,
    addPrerequisiteMutation,
    removePrerequisiteMutation,
    closeMutation,
    forceStopMutation,
    stopMutation,
    resumeMutation,
    retryMutation,
    rerunMutation,
    addCommentMutation,
    deleteCommentMutation,
  } = useIssueDetailMutations({
    issueNumber,
    projectId,
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
        <div className="text-muted-foreground">Loading...</div>
      </div>
    )
  }

  const isBacklog = issue.status === IssueStatus.Backlog
  const isArchived = !!issue.archivedAt
  const workflowStage = issue.workflowStage ?? null
  const prDeliveryMetadata = findPublishViaPrMetadata(workflowTimeline)
  const decision = deriveRuntimeDecision({
    issue: {
      status: issue.status,
      workflowStage: issue.workflowStage ?? null,
      workflowStatus: issue.workflowStatus ?? null,
      health: issue.health,
      approvalState: issue.approvalState ?? undefined,
      blockedReason: issue.blockedReason ?? undefined,
      recovery: issue.recovery ?? undefined,
      convergence: issue.convergence ?? undefined,
      drift: issue.drift ?? undefined,
      workflowStageProgress: issue.workflowStageProgress ?? undefined,
      prerequisites: issue.prerequisites ?? [],
      isDraft: issue.isDraft,
      canStart: issue.canStart,
      blocker: issue.blocker,
    },
    timeline: workflowTimeline
      ? {
          currentStage: workflowTimeline.currentStage,
          status: workflowTimeline.status,
          stages: workflowTimeline.stages,
          pendingWork: workflowTimeline.pendingWork,
          availableActions: workflowTimeline.availableActions,
        }
      : null,
    agentStatus: agentStatus ?? null,
    issueNumber,
    hasActiveAgent: isAgentRunningOnThis,
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
            <div className="flex flex-wrap items-center gap-3 mb-2">
              <div className="flex flex-wrap items-center gap-1.5" data-testid="status-badges-identity">
                <span className="text-sm font-mono text-muted-foreground/70 tabular-nums">
                  #{issue.number}
                </span>
                <PriorityChip priority={issue.priority} />
                {issue.isDraft && <DraftPill />}
                {isArchived && <ArchivedPill archivedAt={issue.archivedAt} />}
              </div>
              <div className="flex flex-wrap items-center gap-1.5" data-testid="status-badges-runtime">
                <RuntimeSummaryPill summary={decision.summary} />
              </div>
            </div>
            {isArchived && (
              <div
                data-testid="archived-banner"
                data-archived-at={issue.archivedAt ?? ''}
                className="mt-3 rounded-md border border-border bg-muted px-3 py-2 text-xs text-muted-foreground"
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
              decision={decision}
              mutations={{
                approveMutation,
                sendBackMutation,
                retryMutation,
                resumeMutation,
                rerunMutation,
                forceStopMutation,
                stopMutation,
                startMutation,
              }}
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
            <WorkflowView issue={issue} readOnly />
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
            <div className="min-w-0 rounded-lg bg-card p-4 mb-8 border-l-2 border-border" data-testid="diff-summary-banner">
              <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1 text-sm">
                <span className="min-w-0 text-muted-foreground break-words">
                  <span className="font-medium text-card-foreground break-all" title={diffData.head} data-testid="diff-summary-head">{diffData.head}</span>
                  {' wants to merge into '}
                  <span className="font-medium text-card-foreground break-all" title={diffData.base} data-testid="diff-summary-base">{diffData.base}</span>
                </span>
                <span className="text-muted-foreground/40">·</span>
                <span className="text-muted-foreground">
                  <span className="font-medium text-card-foreground">{diffData.ahead}</span> ahead
                </span>
                {diffData.behind > 0 && (
                  <>
                    <span className="text-muted-foreground/40">·</span>
                    <span className="text-muted-foreground">
                      <span className="font-medium text-card-foreground">{diffData.behind}</span> behind
                    </span>
                  </>
                )}
                <span className="text-muted-foreground/40">·</span>
                <span className="text-muted-foreground">
                  <span className="font-medium text-card-foreground">{diffData.summary.filesChanged}</span> files changed
                </span>
                <span className="text-muted-foreground/40">·</span>
                <span className="text-success">+{diffData.summary.additions}</span>
                <span className="text-danger">-{diffData.summary.deletions}</span>
              </div>
              <div className="mt-2 flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
                <span className="min-w-0 break-words">showing merge-base → <span className="break-all" title={diffData.head}>{diffData.head}</span></span>
                <span>·</span>
                <span>Workspace retained</span>
              </div>
            </div>
          )}

          <div className="grid min-w-0 grid-cols-1 lg:grid-cols-3 gap-8" data-testid="issue-detail-content-grid">
            <div className="min-w-0 lg:col-span-2 space-y-8">
              <IssueDescriptionSection issue={issue} resolveIssueAttachment={resolveIssueAttachment} />

              {issue.workflowRunId && (
                <WorkflowYamlDialog workflowRunId={issue.workflowRunId} isArchived={isArchived} />
              )}

              <IssueDiffFilesSection
                diffData={diffData}
                onViewFiles={() => navigate(toProjectPath(`/issues/${issueNumber}/files`))}
              />

              <IssueCommitsSection
                commitsData={commitsData}
                onViewAllCommits={() => navigate(toProjectPath(`/issues/${issueNumber}/files`))}
              />

              {(diffData?.available === false || commitsData?.available === false) && (
                <div className="rounded-lg bg-card p-4">
                  <p className="text-sm text-muted-foreground">
                    {diffData?.available === false && diffData.message}
                    {diffData?.available === false && commitsData?.available === false && ' / '}
                    {commitsData?.available === false && commitsData.message}
                  </p>
                </div>
              )}

              <IssueCommentsSection
                comments={comments}
                issueNumber={issueNumber}
                issueProjectId={issueProjectId}
                commentText={commentText}
                setCommentText={setCommentText}
                deletingCommentId={deletingCommentId}
                setDeletingCommentId={setDeletingCommentId}
                deleteCommentError={deleteCommentError}
                setDeleteCommentError={setDeleteCommentError}
                mutations={{ addCommentMutation, deleteCommentMutation }}
              />
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
                  <p className="text-xs text-warning">
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
                issue={issue}
                decision={decision}
                agentStatus={agentStatus ?? null}
                mutations={{
                  approveMutation,
                  sendBackMutation,
                  startMutation,
                  markReadyMutation,
                  closeMutation,
                  resumeMutation,
                  retryMutation,
                  rerunMutation,
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
