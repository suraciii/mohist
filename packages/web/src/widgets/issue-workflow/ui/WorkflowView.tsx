import { useState, useEffect, useMemo } from 'react'
import { IssueStatus, IssueHealth, WorkflowStage, useWorkflowTimeline } from '../../../entities/issue'
import type { Issue, WorkInterruption } from '../../../entities/issue'
import { StageBar, workflowTimelineToStageStateMap, WORKFLOW_STAGES } from './StageBar'
import { StepList, type StepListDependencies } from './InlineApproval'
import { SpecialStatePanel, IntegrateFailurePanel } from './failure-panels'

export type WorkflowTimelineHook = (
  ...args: Parameters<typeof useWorkflowTimeline>
) => Pick<ReturnType<typeof useWorkflowTimeline>, 'data'>

export function RecoverableInterruptionPanel({ interruption }: { interruption: WorkInterruption }) {
  return (
    <section
      className="rounded-md border border-warning-border bg-warning-subtle px-3 py-2.5 text-sm text-warning"
      data-testid="workflow-recoverable-interruption"
    >
      <div className="font-semibold">Recoverable interruption</div>
      <dl className="mt-1 grid gap-x-3 gap-y-0.5 text-xs sm:grid-cols-[auto_1fr]">
        <dt>Reason</dt>
        <dd className="break-all">{interruption.reasonCode}</dd>
        <dt>Work</dt>
        <dd className="break-all">{interruption.workId}</dd>
        <dt>Recovery deadline</dt>
        <dd className="break-all">{interruption.recoveryDeadlineAt}</dd>
      </dl>
    </section>
  )
}

export function WorkflowView({
  issue,
  readOnly: readOnlyProp = false,
  dependencies,
  timelineHook = useWorkflowTimeline,
}: {
  issue: Issue
  readOnly?: boolean
  dependencies?: Partial<StepListDependencies>
  timelineHook?: WorkflowTimelineHook
}) {
  const isClosed = issue.status === IssueStatus.Cancelled
  const isCompleted = issue.status === IssueStatus.Done
  const isBacklog = issue.status === IssueStatus.Backlog
  const readOnly = readOnlyProp || isClosed
  const { data: timeline } = timelineHook(issue.number, !isBacklog)
  const stageStateMap = useMemo(() => workflowTimelineToStageStateMap(timeline), [timeline])

  const defaultStage = useMemo((): WorkflowStage => {
    if (isBacklog) return WorkflowStage.Plan
    if (isCompleted) return WorkflowStage.Integrate
    const currentIdx = issue.workflowStage ? WORKFLOW_STAGES.indexOf(issue.workflowStage) : -1
    if (currentIdx >= 0) return WORKFLOW_STAGES[currentIdx]
    return WorkflowStage.Plan
  }, [issue.workflowStage, isBacklog, isCompleted])

  const [selectedStage, setSelectedStage] = useState<WorkflowStage>(defaultStage)

  useEffect(() => {
    setSelectedStage(defaultStage)
  }, [issue.projectId, issue.number, defaultStage])

  return (
    <div className="space-y-4">
      <StageBar
        stageStateMap={stageStateMap}
        issue={issue}
        selectedStage={selectedStage}
        onSelectStage={setSelectedStage}
      />

      {timeline?.interruption && <RecoverableInterruptionPanel interruption={timeline.interruption} />}
      {!readOnly && (isBacklog || issue.health === IssueHealth.Blocked) && (
        <SpecialStatePanel issue={issue} issueNumber={issue.number} />
      )}

      {!isBacklog && (
        <StepList
          stage={selectedStage}
          stageStateMap={stageStateMap}
          issue={issue}
          readOnly={readOnly}
          workflowRunId={timeline?.workflowRunId ?? issue.workflowRunId ?? null}
          dependencies={dependencies}
        />
      )}

      {!readOnly && issue.workflowStage === WorkflowStage.Integrate && issue.health === IssueHealth.Blocked && (
        <IntegrateFailurePanel issue={issue} />
      )}
    </div>
  )
}
