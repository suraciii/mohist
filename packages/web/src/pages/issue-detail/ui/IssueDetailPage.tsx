import { useEffect, useMemo, useState, type ComponentType } from 'react'
import { Link, useParams, useNavigate } from 'react-router-dom'
import { ArrowLeftIcon, PencilIcon } from 'lucide-react'
import { IssueStatus, partitionIssueBody, useLiveTask } from '../../../entities/issue'
import { issueAttachmentContentPath } from '../../../entities/issue'
import { useIssue, useIssueDiff, useIssueCommits, useWorkflowTimeline } from '../../../entities/issue'
import { useAgentStatus } from '../../../entities/agent'
import { useWorkflowRunSessions } from '../../../entities/coder-session'
import { EditIssueDialog } from '../../../features/edit-issue'
import { WorkflowConvergencePanel } from '../../../widgets/issue-workflow'
import { NotFoundState } from '@/shared/ui/not-found-state'
import { ErrorState } from '@/shared/ui/error-state'
import { ApiError } from '@/shared/api/client'
import { IssueDetailPageSkeleton } from './IssueDetailPageSkeleton'
import { BranchBar, WorkflowView, WorkflowSessionsPanel, IssueWorkflowProfileEditor, LatestArtifactsPanel, PrDeliverySummary, findPublishViaPrMetadata, WorkflowProfileControl, deriveRuntimeDecision } from '../../../widgets/issue-workflow'
import { ActivityDialog, type EventTimelinePanelProps } from '../../../widgets/issue-event-timeline'
import { formatTime } from '../../../shared/lib/format-time'
import { useNarrowViewport } from '../../../shared/lib/use-narrow-viewport'
import { useProject, useProjectPath } from '../../../entities/project'
import { Button } from '@/shared/ui/components/button'
import { getLabelStyle, sortLabels } from '../../../shared/lib/label-colors'

import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { attachmentFromMetadata } from '../model/format'
import { deriveIssueOnlyStatus } from '../model/issueDecisionContext'
import {
  deriveIssueDecisionActions,
  type IssueDecisionAction,
} from '../model/issueDecisionActions'
import { getStopConsequenceCopy, useIssueDecisionActionController } from '../model/useIssueDecisionActions'
import { useIssueDetailSectionNavigation } from '../model/useIssueDetailSectionNavigation'
import { useIssueAttentionNudges } from '../model/useIssueAttentionNudges'
import {
  useIssueDetailMutations,
  type IssueDetailMutationDependencies,
} from '../model/useIssueDetailMutations'
import { ArchivedPill, DraftPill, PriorityChip } from './pills'
import { StatusHeadline } from './StatusHeadline'
import { IssueOnlyStatusHeadline } from './IssueOnlyStatusHeadline'
import { WorkflowYamlDialog } from './WorkflowYamlDialog'
import { IssueDecisionSurface } from './IssueDecisionSurface'
import { IssueDetailsCard } from './cards/IssueDetailsCard'
import { IssueDriftCard } from './cards/IssueDriftCard'
import { IssueConfigurationCard } from './cards/IssueConfigurationCard'
import { IssuePrerequisitesCard } from './cards/IssuePrerequisitesCard'
import { IssueReadinessCard } from './cards/IssueReadinessCard'
import { IssueWatchCard } from './cards/IssueWatchCard'
import { CollapsibleRailCard } from './cards/CollapsibleRailCard'
import { CompositeParentOverview } from './sections/CompositeParentOverview'
import { IssueDescriptionSection } from './sections/IssueDescriptionSection'
import { IssueDiffFilesSection } from './sections/IssueDiffFilesSection'
import { IssueCommitsSection } from './sections/IssueCommitsSection'
import { IssueCommentsSection } from './sections/IssueCommentsSection'
import { MobileActionBar } from './MobileActionBar'
import { ApprovalReviewPackage } from './ApprovalReviewPackage'

export interface IssueDetailPageComponents {
  EventTimelinePanel: ComponentType<EventTimelinePanelProps>
}

export interface IssueDetailPageProps {
  components?: Partial<IssueDetailPageComponents>
  mutationDependencies?: Partial<IssueDetailMutationDependencies>
}

type DecisionSummary = 'running' | 'queued' | 'approval-required' | 'blocked' | 'failed' | 'done' | 'cancelled' | 'done-no-action' | 'terminal-no-action'

function decisionSummaryFromRuntime(summary: 'running' | 'queued' | 'approval-required' | 'blocked' | 'failed' | 'done' | 'cancelled' | undefined): DecisionSummary {
  if (!summary) return 'terminal-no-action'
  return summary
}

export function IssueDetailPage({
  components,
  mutationDependencies,
}: IssueDetailPageProps = {}) {
  const { number } = useParams<{ number: string }>()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { projectId } = useProject()
  const issueNumber = parseInt(number ?? '0', 10)
  const [editOpen, setEditOpen] = useState(false)
  const [commentText, setCommentText] = useState('')
  const [commentAuthor, setCommentAuthor] = useState('')
  const [deletingCommentId, setDeletingCommentId] = useState<string | null>(null)
  const [deleteCommentError, setDeleteCommentError] = useState<string | null>(null)

  const mutations = useIssueDetailMutations(
    {
      issueNumber,
      projectId,
      onAddCommentSuccess: () => {
        setCommentAuthor('')
        setCommentText('')
      },
      onDeleteCommentSuccess: () => {
        setDeletingCommentId(null)
        setDeleteCommentError(null)
      },
      onDeleteCommentError: (err) => {
        setDeleteCommentError(err.message)
        setDeletingCommentId(null)
      },
    },
    mutationDependencies,
  )

  const { data: issue, isLoading, isError, error, refetch } = useIssue(issueNumber)
  const isCompositeParent = !!issue && (issue.children?.length ?? 0) > 0
  const workflowDataEnabled = !!issue && !isCompositeParent
  const { data: agentStatus } = useAgentStatus()
  const diffQuery = useIssueDiff(issueNumber, workflowDataEnabled)
  const { data: diffData } = diffQuery
  const { data: workflowTimeline, refetch: refetchWorkflowTimeline } = useWorkflowTimeline(
    issueNumber,
    workflowDataEnabled && !!issue && issue.status !== IssueStatus.Backlog,
  )
  const { eventsReconnectVersion } = useLiveTask()
  useEffect(() => {
    if (eventsReconnectVersion === 0 || !workflowDataEnabled) return
    void refetchWorkflowTimeline()
  }, [eventsReconnectVersion, refetchWorkflowTimeline, workflowDataEnabled])
  const isNarrowViewport = useNarrowViewport()

  const activeAgents = agentStatus?.activeAgents ?? []
  const isAgentRunningOnThis = activeAgents.some(a => a.issueNumber === issueNumber)

  const workflowRunId = !isCompositeParent ? (issue?.workflowRunId ?? null) : null
  const { sessions: workflowSessions } = useWorkflowRunSessions(workflowRunId)

  useDocumentTitle(`Issue #${issueNumber} — Mohist`, isAgentRunningOnThis)

  const { data: commitsData } = useIssueCommits(issueNumber, workflowDataEnabled)
  const sectionNavigation = useIssueDetailSectionNavigation({
    workflow: !!issue && !isCompositeParent,
    artifacts: !!issue && !isCompositeParent,
    comments: !!issue,
  })

  const isBacklog = issue?.status === IssueStatus.Backlog
  const isArchived = !!issue?.archivedAt

  const decisionInputs = isCompositeParent || !issue ? null : {
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
      prerequisites: issue.prereq ?? [],
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
  }

  const decision = decisionInputs ? deriveRuntimeDecision(decisionInputs) : null
  useIssueAttentionNudges({ issueNumber, summary: decision?.summary ?? null })
  const issueBody = useMemo(() => partitionIssueBody(issue?.body), [issue?.body])

  const decisionActions = useMemo(() => {
    if (!issue) {
      return { actions: [] as ReadonlyArray<IssueDecisionAction>, primary: null, transcript: null }
    }
    return deriveIssueDecisionActions({
      decision,
      issue: {
        number: issue.number,
        status: issue.status,
        workflowStatus: issue.workflowStatus ?? null,
        health: issue.health,
        isDraft: !!issue.isDraft,
        canStart: !!issue.canStart,
        workflowStage: issue.workflowStage ?? null,
        workflowRunId: issue.workflowRunId ?? null,
        archivedAt: issue.archivedAt,
        children: issue.children,
        childIssuesSummary: issue.childIssuesSummary ?? null,
        blocker: issue.blocker,
      },
      agentStatus: agentStatus ?? null,
       workflowSessions: workflowSessions.map((s) => ({
         id: s.id,
         sessionName: s.sessionName,

        activity: s.activity,
        startedAt: s.startedAt,
        createdAt: s.createdAt,
      })),
      projectPath: toProjectPath,
    })
  }, [
    decision,
    issue,
    agentStatus,
    workflowSessions,
    toProjectPath,
  ])

  const controller = useIssueDecisionActionController({
    mutations,
    stopRecoverable: decision?.stopRecoverable ?? null,
    approvalStage: decision?.approvalStage ?? null,
    getStopConsequenceCopy,
  })
  const approvalArtifactSummaries = useMemo(() => {
    if (!workflowTimeline || !decision?.approvalStage) return undefined
    return workflowTimeline.stages
      .find((stage) => stage.stage === decision.approvalStage)
      ?.tasks.flatMap((task) => task.artifactSummaries ?? []) ?? []
  }, [decision?.approvalStage, workflowTimeline])

  if (isError) {
    const isNotFound = error instanceof ApiError && error.status === 404
    if (isNotFound) {
      return <NotFoundState />
    }
    return (
      <ErrorState
        title="Failed to load issue"
        message={
          error instanceof Error && error.message
            ? error.message
            : 'We could not load this issue. Please try again.'
        }
        onRetry={() => {
          void refetch()
        }}
      />
    )
  }

  if (isLoading || !issue) {
    return <IssueDetailPageSkeleton />
  }

  const workflowStage = isCompositeParent ? null : (issue.workflowStage ?? null)
  const prDeliveryMetadata = isCompositeParent ? null : findPublishViaPrMetadata(workflowTimeline)

  const comments = [...(issue.comments ?? [])].sort(
    (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
  )
  const convergence = isCompositeParent ? null : (issue.convergence ?? null)
  const hasConvergenceContent = !isCompositeParent && !!convergence
    && (!!convergence.failedCheck || convergence.blockingItemCount > 0 || convergence.reactionAttempts > 0)
  const convergenceSummary = convergence?.blockingItemCount
    ? `${convergence.blockingItemCount} blocking`
    : convergence?.reactionAttempts
      ? `${convergence.reactionAttempts} attempt${convergence.reactionAttempts === 1 ? '' : 's'}`
      : 'convergence'
  const issueProjectId = projectId ?? issue.projectId
  const resolveIssueAttachment = (id: string) => attachmentFromMetadata(
    id,
    issue.attachments,
    `/api${issueAttachmentContentPath(issueNumber, id, issueProjectId)}`,
  )
  const compositeSummary = isCompositeParent
    ? {
        count: issue.childIssuesSummary?.count ?? issue.children?.length ?? 0,
        doneCount: issue.childIssuesSummary?.doneCount ?? 0,
        blockedCount: issue.childIssuesSummary?.blockedCount ?? 0,
      }
    : null
  const issueOnlyContext = isCompositeParent
    ? deriveIssueOnlyStatus({
      status: issue.status,
      health: issue.health,
      isDraft: !!issue.isDraft,
      isArchived,
      childSummary: compositeSummary,
    })
    : null

  const issueActions: ReadonlyArray<IssueDecisionAction> = decisionActions.actions
  const surfaceSummary: DecisionSummary = decision
    ? decisionSummaryFromRuntime(decision.summary)
    : (isArchived ? 'terminal-no-action' : issueActions.length === 0 ? 'done-no-action' : 'terminal-no-action')
  const surfaceRationale = decision?.rationale
    ?? issueOnlyContext?.rationale
    ?? 'No active workflow decision.'
  const surfaceNextAction = decision?.nextAction
    ?? issueOnlyContext?.nextAction
    ?? 'No action required right now.'

  const isApproval = decision?.summary === 'approval-required'
  const showDecisionSurface = !isNarrowViewport
    && (decision !== null || issueOnlyContext !== null)
  const showMobileActionBar = isNarrowViewport && !isApproval
  const showMobileReservedBar = isNarrowViewport && (isApproval || showMobileActionBar)
  const showWorkflowSections = !isCompositeParent

  return (
    <>
      <div className="flex-1 min-w-0 overflow-y-auto" data-testid="issue-detail-page-container">
        <div
          className={
            showMobileReservedBar
              ? 'max-w-4xl min-w-0 mx-auto px-4 sm:px-6 pt-6 pb-[calc(8rem+env(safe-area-inset-bottom))]'
              : 'max-w-4xl min-w-0 mx-auto px-4 sm:px-6 py-6'
          }
          data-testid="issue-detail-content-column"
          data-bar-reserved={showMobileReservedBar ? 'true' : 'false'}
        >
          <div data-testid="status-header-tier" className="space-y-4">
            {isCompositeParent && issueOnlyContext ? (
              <IssueOnlyStatusHeadline
                status={issue.status}
                health={issue.health}
                isDraft={!!issue.isDraft}
                isArchived={isArchived}
                context={issueOnlyContext}
              />
            ) : (
              decision && (
                <StatusHeadline
                  decision={decision}
                  stageProgress={issue.workflowStageProgress ?? null}
                />
              )
            )}

            <button
              type="button"
              onClick={() => navigate(isArchived ? toProjectPath('/archived') : toProjectPath())}
              data-testid={isArchived ? 'back-to-archived' : 'back-to-board'}
              className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              <ArrowLeftIcon className="size-3.5" />
              <span>{isArchived ? 'Back to archived' : 'Back to board'}</span>
            </button>

            <div data-testid="issue-detail-header">
              <div className="flex flex-wrap items-center gap-3 mb-2">
                <div className="flex flex-wrap items-center gap-1.5" data-testid="status-badges-identity">
                  <span className="text-sm font-mono text-muted-foreground/70 tabular-nums">
                    #{issue.number}
                  </span>
                  <PriorityChip priority={issue.priority} />
                  {issue.isDraft && <DraftPill />}
                  {isArchived && <ArchivedPill archivedAt={issue.archivedAt} />}
                  {isCompositeParent && (
                    <span
                      data-testid="composite-parent-badge"
                      className="inline-flex items-center rounded-full bg-violet-100 text-violet-800 px-2 py-0.5 text-[10px] font-semibold"
                    >
                      Parent issue
                    </span>
                  )}
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
                  {!isCompositeParent && (
                    <ActivityDialog
                      issueNumber={issueNumber}
                      workflowStatus={issue?.workflowStatus}
                      open={sectionNavigation.activityOpen}
                      onOpenChange={sectionNavigation.onActivityOpenChange}
                      TimelinePanel={components?.EventTimelinePanel}
                    />
                  )}
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
              {issue.epic && (
                <button
                  type="button"
                  onClick={() => {
                    if (issue.epic?.number != null) {
                      navigate(toProjectPath(`/epics/${issue.epic.number}`))
                    }
                  }}
                  className="mt-3 inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors"
                  data-testid="primary-epic-label"
                >
                  <span className="text-xs text-muted-foreground/70">Part of Epic:</span>
                  <span
                    className="font-mono font-medium text-foreground/80"
                    data-testid="primary-epic-number"
                  >
                    {issue.epic.number != null
                      ? `#${issue.epic.number}`
                      : 'Epic'}
                  </span>
                  <span className="font-medium text-foreground/90">
                    {issue.epic.title}
                  </span>
                </button>
              )}
              <div className="mt-2 text-xs text-muted-foreground/70">
                Created {formatTime(issue.createdAt)} · Updated {formatTime(issue.updatedAt)}
              </div>
              <nav aria-label="Issue sections" className="mt-3 flex flex-wrap gap-x-4 gap-y-2 text-sm">
                {showWorkflowSections && (
                  <>
                    <Link to={sectionNavigation.links.workflow} className="text-muted-foreground hover:text-foreground">Workflow</Link>
                    <Link to={sectionNavigation.links.artifacts} className="text-muted-foreground hover:text-foreground">Artifacts</Link>
                    <Link to={sectionNavigation.links.activity} className="text-muted-foreground hover:text-foreground">Activity</Link>
                  </>
                )}
                <Link to={sectionNavigation.links.comments} className="text-muted-foreground hover:text-foreground">Comments</Link>
              </nav>
            </div>

            {isApproval ? (
              <ApprovalReviewPackage
                issueNumber={issueNumber}
                workflowRunId={issue.workflowRunId ?? null}
                approvalStage={decision?.approvalStage || null}
                artifactSummaries={approvalArtifactSummaries}
                actions={issueActions}
                controller={controller}
                rationale={surfaceRationale}
                nextAction={surfaceNextAction}
                isNarrowViewport={isNarrowViewport}
                diffData={diffData}
                diffIsLoading={diffQuery.isLoading}
                diffError={diffQuery.error}
              />
            ) : showDecisionSurface && (
              <div data-testid="issue-decision-surface-frame">
                <IssueDecisionSurface
                  actions={issueActions}
                  summary={surfaceSummary}
                  rationale={surfaceRationale}
                  nextAction={surfaceNextAction}
                  controller={controller}
                />
              </div>
            )}
          </div>

          <div className="mt-8 grid min-w-0 grid-cols-1 lg:grid-cols-3 gap-8" data-testid="issue-detail-content-grid">
            <div className="min-w-0 lg:col-span-2 space-y-8" data-testid="reading-flow" data-tier-weight="reading-flow">
              {isCompositeParent && compositeSummary && (
                <CompositeParentOverview
                  children={issue.children ?? []}
                  summary={compositeSummary}
                />
              )}

              {showWorkflowSections && (
                <BranchBar
                  issueNumber={issueNumber}
                  stage={workflowStage}
                  isAgentRunning={isAgentRunningOnThis}
                  baseBranch={issue.repository?.baseBranch}
                  allowRebase={!isBacklog && !!issue.workflowRunId}
                />
              )}

              {showWorkflowSections && (
                <div id="workflow" className="scroll-mt-20" data-testid="workflow-view-frame">
                  <WorkflowView issue={issue} readOnly dependencies={{ workflowSessionsHook: useWorkflowRunSessions }} />
                </div>
              )}

              {showWorkflowSections && prDeliveryMetadata && (
                <div data-testid="pr-delivery-summary-frame">
                  <PrDeliverySummary timeline={workflowTimeline} />
                </div>
              )}

              {showWorkflowSections && !isApproval && (
                <LatestArtifactsPanel issueNumber={issueNumber} workflowRunId={issue.workflowRunId} />
              )}

              {showWorkflowSections && issue.workflowRunId && (
                <WorkflowYamlDialog workflowRunId={issue.workflowRunId} isArchived={isArchived} />
              )}

              {showWorkflowSections && (!isBacklog || issue.workflowRunId) && (
                <div data-testid="runtime-evidence-frame" className="space-y-4">
                  {!isBacklog && issue.workflowRunId && (
                    <WorkflowSessionsPanel
                      issueNumber={issueNumber}
                      workflowRunId={issue.workflowRunId}
                    />
                  )}
                </div>
              )}

              {showWorkflowSections && (
                <IssueDiffFilesSection
                  diffData={diffData}
                  isLoading={diffQuery.isLoading}
                  error={diffQuery.error}
                  commitsUnavailable={commitsData?.available === false}
                  onViewFiles={() => navigate(toProjectPath(`/issues/${issueNumber}/files`))}
                />
              )}

              {showWorkflowSections && (
                <IssueCommitsSection
                  commitsData={commitsData}
                  onViewAllCommits={() => navigate(toProjectPath(`/issues/${issueNumber}/files`))}
                />
              )}

              <IssueDescriptionSection
                description={issueBody.description}
                resolveIssueAttachment={resolveIssueAttachment}
              />

              <IssueCommentsSection
                comments={comments}
                issueNumber={issueNumber}
                issueProjectId={issueProjectId}
                commentText={commentText}
                setCommentText={setCommentText}
                commentAuthor={commentAuthor}
                setCommentAuthor={setCommentAuthor}
                deletingCommentId={deletingCommentId}
                setDeletingCommentId={setDeletingCommentId}
                deleteCommentError={deleteCommentError}
                setDeleteCommentError={setDeleteCommentError}
                mutations={{ addCommentMutation: mutations.addCommentMutation, deleteCommentMutation: mutations.deleteCommentMutation }}
              />
            </div>

            <div
              className={isNarrowViewport ? 'min-w-0 space-y-4' : 'min-w-0 space-y-6 lg:sticky lg:top-6 lg:col-span-1 lg:max-h-[calc(100vh-3rem)] lg:self-start lg:overflow-y-auto'}
              data-testid="reference-rail"
              data-tier-weight="reference-rail"
              data-rail-mode={isNarrowViewport ? 'narrow' : 'desktop'}
            >
              <CollapsibleRailCard
                testId="reference-rail-details"
                title="Details"
                forceCollapsed={isNarrowViewport}
                summary={issue.status === IssueStatus.Backlog ? 'Backlog' : issue.status}
              >
                <IssueDetailsCard
                  issue={issue}
                  bodyMetadata={issueBody}
                  unframed
                />
              </CollapsibleRailCard>

              {!isCompositeParent && (
                <CollapsibleRailCard
                  testId="reference-rail-workflow-profile"
                  title="Workflow Profile"
                  forceCollapsed={isNarrowViewport}
                  summary={issue.workflowProfileId ?? 'default'}
                >
                  <div className="space-y-4">
                    <div data-testid="issue-workflow-profile-control-frame">
                      <WorkflowProfileControl issue={issue} embedded />
                    </div>
                    <div data-testid="workflow-profile-editor-frame">
                      <IssueWorkflowProfileEditor issueNumber={issueNumber} embedded />
                    </div>
                  </div>
                </CollapsibleRailCard>
              )}

              {!isCompositeParent && issue.drift?.drifted && (
                <CollapsibleRailCard
                  testId="reference-rail-drift"
                  title="Base Drift Detected"
                  defaultCollapsed
                  forceCollapsed={isNarrowViewport}
                  summary={issue.drift.decision ?? 'drifted'}
                >
                  <IssueDriftCard drift={issue.drift} unframed />
                </CollapsibleRailCard>
              )}

              {!isCompositeParent && hasConvergenceContent && convergence && (
                <CollapsibleRailCard
                  testId="reference-rail-convergence"
                  title="Convergence"
                  defaultCollapsed
                  forceCollapsed={isNarrowViewport}
                  summary={convergenceSummary}
                >
                  <WorkflowConvergencePanel convergence={convergence} />
                </CollapsibleRailCard>
              )}

              <CollapsibleRailCard
                testId="reference-rail-configuration"
                title="Configuration"
                forceCollapsed={isNarrowViewport}
                summary={issue.model ?? 'default model'}
              >
                <IssueConfigurationCard
                  issue={{ number: issue.number, model: issue.model, stageModels: issue.stageModels, workflowRunId: issue.workflowRunId, workflowProfileId: issue.workflowProfileId, prerequisites: issue.prereq, canStart: issue.canStart, blocker: issue.blocker, isBacklog: !!isBacklog }}
                  projectId={issueProjectId}
                  mutations={{ addPrerequisiteMutation: mutations.addPrerequisiteMutation, removePrerequisiteMutation: mutations.removePrerequisiteMutation }}
                  unframed
                />
              </CollapsibleRailCard>

              {issue.prereq && issue.prereq.length > 0 && (
                <CollapsibleRailCard
                  testId="reference-rail-prerequisites"
                  title="Start Prerequisites"
                  forceCollapsed={isNarrowViewport}
                  summary={`${issue.prereq.length} item${issue.prereq.length === 1 ? '' : 's'}`}
                >
                  <IssuePrerequisitesCard prerequisites={issue.prereq} unframed />
                </CollapsibleRailCard>
              )}

              {isBacklog && (
                <CollapsibleRailCard
                  testId="reference-rail-readiness"
                  title="Readiness"
                  forceCollapsed={isNarrowViewport}
                  summary={issue.canStart ? 'ready' : 'not ready'}
                >
                  <IssueReadinessCard issue={issue} unframed />
                </CollapsibleRailCard>
              )}

              {issue.watching && issue.watching.length > 0 && (
                <CollapsibleRailCard
                  testId="reference-rail-watching"
                  title="Watching"
                  forceCollapsed={isNarrowViewport}
                  summary={`${issue.watching.length} agent${issue.watching.length === 1 ? '' : 's'}`}
                >
                  <IssueWatchCard entries={issue.watching} variant="watching" unframed />
                </CollapsibleRailCard>
              )}

              {issue.muted && issue.muted.length > 0 && (
                <CollapsibleRailCard
                  testId="reference-rail-muted"
                  title="Muted"
                  forceCollapsed={isNarrowViewport}
                  summary={`${issue.muted.length} agent${issue.muted.length === 1 ? '' : 's'}`}
                >
                  <IssueWatchCard entries={issue.muted} variant="muted" unframed />
                </CollapsibleRailCard>
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

      {showMobileActionBar && (
        <MobileActionBar
          actions={issueActions}
          primary={decisionActions.primary}
          rationale={surfaceRationale}
          nextAction={surfaceNextAction}
          controller={controller}
          summary={surfaceSummary}
        />
      )}
    </>
  )
}
