import { useState, useEffect, useCallback, useMemo } from 'react'
import { IssueStatus, IssueHealth, WorkflowStage, useWorkflowTimeline } from '../../../entities/issue'
import type { Issue } from '../../../entities/issue'
import { StageBar, workflowTimelineToStageStateMap, WORKFLOW_STAGES } from './StageBar'
import { StepList } from './InlineApproval'
import { SpecialStatePanel, IntegrateFailurePanel } from './failure-panels'

export function WorkflowView({ issue, readOnly: readOnlyProp = false }: { issue: Issue; readOnly?: boolean }) {
  const isClosed = issue.status === IssueStatus.Cancelled
  const isCompleted = issue.status === IssueStatus.Done
  const isBacklog = issue.status === IssueStatus.Backlog
  const readOnly = readOnlyProp || isClosed
  const { data: timeline } = useWorkflowTimeline(issue.number, !isBacklog)
  const stageStateMap = useMemo(() => workflowTimelineToStageStateMap(timeline), [timeline])

  const getDefaultStage = useCallback((): WorkflowStage => {
    if (isBacklog) return WorkflowStage.Plan
    if (isCompleted) return WorkflowStage.Integrate
    const currentIdx = issue.workflowStage ? WORKFLOW_STAGES.indexOf(issue.workflowStage) : -1
    if (currentIdx >= 0) return WORKFLOW_STAGES[currentIdx]
    return WorkflowStage.Plan
  }, [issue.workflowStage, isBacklog, isCompleted])

  const [selectedStage, setSelectedStage] = useState<WorkflowStage>(getDefaultStage)

  useEffect(() => {
    setSelectedStage(getDefaultStage())
  }, [getDefaultStage])

  const handleSelectStage = useCallback(
    (stage: WorkflowStage) => {
      if (readOnly) return
      setSelectedStage(stage)
    },
    [readOnly],
  )

  return (
    <div className="space-y-4">
      <StageBar
        stageStateMap={stageStateMap}
        issue={issue}
        selectedStage={selectedStage}
        onSelectStage={handleSelectStage}
        readOnly={readOnly}
      />

      {!readOnly && (isBacklog || issue.health === IssueHealth.Blocked || issue.health === IssueHealth.Interrupted) && (
        <SpecialStatePanel issue={issue} issueNumber={issue.number} />
      )}

      {!isBacklog && (
        <StepList
          stage={selectedStage}
          stageStateMap={stageStateMap}
          issue={issue}
          readOnly={readOnly}
        />
      )}

      {!readOnly && issue.workflowStage === WorkflowStage.Integrate && (issue.health === IssueHealth.Blocked || issue.health === IssueHealth.Interrupted) && (
        <IntegrateFailurePanel issue={issue} />
      )}

    </div>
  )
}