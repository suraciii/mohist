import { useState, useCallback, useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { issueDetailKeys, issueListKeys, IssueStatus, WorkflowStage, approveIssue, getFileContent, invalidateApprovalWait, useRequestChangesIssue } from '../../../entities/issue'
import type { Issue, StageTaskState, StageCheckState, StageStateRead } from '../../../entities/issue'
import { useProject } from '../../../entities/project'
import { ReviewSummary, parseReviewOutput } from './ReviewSummary'
import type { ReviewOutput } from './ReviewSummary'
import { FullReportModal } from './ReviewReportModal'
import { FeedbackHistory } from './FeedbackHistory'
import { classifyResult } from './format'
import { TaskItem } from './TaskItem'
import type { ArtifactContentHook } from './ArtifactContentViewer'
import { CheckItem } from './CheckItem'
import { WORKFLOW_STAGES } from './StageBar'
import { isScriptHealthCheck } from '../model/runtime-query-helpers'
import type { TaskLogDataHook, WorkflowRunSessionsHook } from './TaskLogPanel'

export type RequestChangesHook = () => Pick<
  ReturnType<typeof useRequestChangesIssue>,
  'mutate' | 'isPending' | 'error'
>

export interface StepListDependencies {
  approveIssue: typeof approveIssue
  requestChangesHook: RequestChangesHook
  artifactContentHook: ArtifactContentHook
  fileContentFn?: typeof getFileContent
  taskLogHook?: TaskLogDataHook
  workflowSessionsHook?: WorkflowRunSessionsHook
}

export function InlineApprovalControls({
  issueNumber,
  stage,
  approvalOutput,
  approveIssueFn = approveIssue,
  requestChangesHook = useRequestChangesIssue,
}: {
  issueNumber: number
  stage: WorkflowStage
  approvalOutput?: Record<string, unknown>
  approveIssueFn?: typeof approveIssue
  requestChangesHook?: RequestChangesHook
}) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const [feedbackOpen, setFeedbackOpen] = useState(false)
  const [feedbackText, setFeedbackText] = useState('')
  const [reportModalOpen, setReportModalOpen] = useState(false)

  const review: ReviewOutput = useMemo(() => parseReviewOutput(approvalOutput), [approvalOutput])
  const classified = useMemo(() => classifyResult(review.result), [review.result])

  const approveMutation = useMutation({
    mutationFn: () => approveIssueFn(issueNumber, {}, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
      invalidateApprovalWait(queryClient)
    },
  })

  const requestChangesMutation = requestChangesHook()

  const handleApprove = useCallback(() => {
    approveMutation.mutate()
  }, [approveMutation])

  const handleOpenRequestChanges = useCallback(() => {
    setFeedbackOpen(true)
  }, [])

  const handleCancelRequestChanges = useCallback(() => {
    setFeedbackOpen(false)
    setFeedbackText('')
  }, [])

  const handleSubmitRequestChanges = useCallback(() => {
    const trimmed = feedbackText.trim()
    if (!trimmed) return
    requestChangesMutation.mutate(
      {
        issueNumber,
        data: { stage, body: trimmed },
      },
      {
        onSuccess: () => {
          setFeedbackOpen(false)
          setFeedbackText('')
        },
      },
    )
  }, [feedbackText, requestChangesMutation, issueNumber, stage])

  const handleViewChanges = useCallback(() => {
    document.getElementById('changes-panel')?.scrollIntoView({ behavior: 'smooth' })
  }, [])

  const getApproveLabel = () => {
    if (stage === WorkflowStage.Plan) return 'Approve & Continue'
    if (stage === WorkflowStage.Check) return 'Approve & Continue'
    return 'Approve & Continue'
  }

  const hasApprovalOutput = approvalOutput != null

  return (
    <div className="rounded-lg border border-warning-border bg-warning-subtle p-4 space-y-3">
      {reportModalOpen && (
        <FullReportModal
          review={review}
          classified={classified}
          onClose={() => setReportModalOpen(false)}
        />
      )}

      <h3 className="text-sm font-semibold text-warning">Approval Required</h3>
      <p className="text-xs text-warning">
        {stage === WorkflowStage.Plan
          ? 'Review the design proposal and approve to continue the workflow.'
          : stage === WorkflowStage.Check
            ? 'Review the check results and approve to continue the workflow.'
            : `Review the ${stage} stage output and approve to continue, or request changes with feedback.`}
      </p>

      {hasApprovalOutput && (
        <ReviewSummary output={approvalOutput} />
      )}

      {hasApprovalOutput && (
        <div className="flex gap-4 text-xs">
          <Button
            variant="link"
            onClick={() => setReportModalOpen(true)}
            className="h-auto p-0 text-xs"
          >
            View Full Report
          </Button>
          <Button
            variant="link"
            onClick={handleViewChanges}
            className="h-auto p-0 text-xs"
          >
            View Changes
          </Button>
        </div>
      )}

      <div className="space-y-2">
        <div className="flex gap-2">
          <Button
            onClick={handleApprove}
            disabled={approveMutation.isPending}
            data-testid="approve-button"
            className={`flex-1 ${
              hasApprovalOutput && classified === 'PASS'
                ? 'bg-success hover:bg-success/90 text-success-foreground'
                : ''
            }`}
          >
            {approveMutation.isPending ? 'Approving...' : getApproveLabel()}
          </Button>
          {!feedbackOpen && (
            <Button
              variant="outline"
              onClick={handleOpenRequestChanges}
              disabled={requestChangesMutation.isPending}
              data-testid="request-changes-button"
              className="flex-1"
            >
              Request changes
            </Button>
          )}
        </div>

        {feedbackOpen && (
          <div
            className="space-y-2 rounded-md border border-border bg-card p-3"
            data-testid="request-changes-form"
          >
            <label
              htmlFor="request-changes-body"
              className="text-xs font-medium text-card-foreground"
            >
              What changes should the agent make?
            </label>
            <Textarea
              id="request-changes-body"
              value={feedbackText}
              onChange={(e) => setFeedbackText(e.target.value)}
              placeholder="Describe the changes you want the agent to apply..."
              rows={3}
              data-testid="request-changes-textarea"
              className="resize-none"
            />
            <div className="flex justify-end gap-2">
              <Button
                variant="ghost"
                onClick={handleCancelRequestChanges}
                disabled={requestChangesMutation.isPending}
                size="sm"
              >
                Cancel
              </Button>
              <Button
                onClick={handleSubmitRequestChanges}
                disabled={!feedbackText.trim() || requestChangesMutation.isPending}
                size="sm"
                data-testid="submit-request-changes"
              >
                {requestChangesMutation.isPending ? 'Submitting...' : 'Submit feedback'}
              </Button>
            </div>
          </div>
        )}
      </div>

      {(approveMutation.error || requestChangesMutation.error) && (
        <div className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger">
          {approveMutation.error?.message || requestChangesMutation.error?.message}
        </div>
      )}
    </div>
  )
}

export function StepList({
  stage,
  stageStateMap,
  issue,
  readOnly,
  workflowRunId,
  dependencies,
}: {
  stage: WorkflowStage
  stageStateMap: Map<string, StageStateRead>
  issue: Issue
  readOnly: boolean
  workflowRunId: string | null
  dependencies?: Partial<StepListDependencies>
}) {
  const stageState = stageStateMap.get(stage)
  const taskResults: StageTaskState[] = stageState?.tasks ?? []
  const checkResults: StageCheckState[] = stageState?.checks ?? []

  const scriptHealthChecks = checkResults.filter(isScriptHealthCheck)
  const failedScriptHealthChecks = scriptHealthChecks.filter(c => c.status === 'failed' || c.status === 'error')
  const displayedWorkflowStage = issue.status === IssueStatus.Done
    && (!issue.workflowStage || !WORKFLOW_STAGES.includes(issue.workflowStage))
    ? WorkflowStage.Integrate
    : issue.workflowStage

  const isAwaitingApproval =
    failedScriptHealthChecks.length === 0 &&
    issue.approvalState?.status === 'awaiting' &&
    displayedWorkflowStage === stage

  return (
    <div className="space-y-4">
      <div>
        <h3 className="text-xs font-semibold text-muted-foreground uppercase tracking-wide mb-2">Tasks</h3>
        <div className="space-y-1.5">
          {taskResults.length > 0 ? (
            taskResults.map((task) => (
              <TaskItem
                key={task.taskId}
                task={task}
                issueNumber={issue.number}
                workflowRunId={workflowRunId}
                artifactContentHook={dependencies?.artifactContentHook}
                fileContentFn={dependencies?.fileContentFn}
                taskLogHook={dependencies?.taskLogHook}
                workflowSessionsHook={dependencies?.workflowSessionsHook}
              />
            ))
          ) : (
            <div className="text-sm text-muted-foreground py-2">No tasks yet</div>
          )}
        </div>
      </div>

      {checkResults.length > 0 && (
        <div>
          <h3 className="text-xs font-semibold text-muted-foreground uppercase tracking-wide mb-2">Checks</h3>
          <div className="space-y-1.5">
            {(() => {
              const nameCounts = new Map<string, number>()
              for (const c of checkResults) {
                nameCounts.set(c.checkName, (nameCounts.get(c.checkName) ?? 0) + 1)
              }
              const nameSeen = new Map<string, number>()
              return checkResults.map((check, idx) => {
                const total = nameCounts.get(check.checkName) ?? 1
                const seen = (nameSeen.get(check.checkName) ?? 0) + 1
                nameSeen.set(check.checkName, seen)
                const attemptLabel = total > 1 ? `attempt ${seen}` : undefined
                return <CheckItem key={`${check.checkName}-${idx}`} check={check} attemptLabel={attemptLabel} />
              })
            })()}
          </div>
        </div>
      )}

      {isAwaitingApproval && (
        <div className="space-y-3" data-testid="step-list-approval-evidence">
          {checkResults.length === 0 && (
            <div className="rounded-md border border-warning-border bg-warning-subtle px-3 py-2 text-xs text-warning">
              Approval is awaiting, but this stage has no recorded check results. This usually means the issue was recovered after an incomplete run; rerun the stage if you need fresh verification before approving.
            </div>
          )}
          {issue.approvalState?.output != null && (
            <ReviewSummary output={issue.approvalState.output} />
          )}
        </div>
      )}

      {/* Feedback history is rendered whenever the stage has feedback records — including
          during the running feedback-loop (apply-feedback task) when the approval card is hidden. */}
      {issue.feedback && issue.feedback.length > 0 && displayedWorkflowStage === stage && (
        <FeedbackHistory
          stage={stage}
          feedback={issue.feedback}
          approvalRequestedAt={issue.approvalState?.requestedAt}
          checks={checkResults}
        />
      )}

      {!readOnly && !isAwaitingApproval && stage === WorkflowStage.Check && failedScriptHealthChecks.length > 0 && (
        <div className="rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger">
          <span className="font-semibold">Full verification failed:</span> Check approval is blocked until the health check passes. Fix the failures and rerun Check.
        </div>
      )}

      {!readOnly && !isAwaitingApproval && stage === WorkflowStage.Check && scriptHealthChecks.length > 0 && scriptHealthChecks.every(c => c.status === 'pending') && (
        <div className="rounded-md border border-warning-border bg-warning-subtle px-3 py-2 text-xs text-warning">
          Full verification has not run yet. Approval will be available once verification completes.
        </div>
      )}
    </div>
  )
}
