import { Button } from '@/shared/ui/components/button'
import { WorkflowStage, useWorkflowTimeline } from '../../../entities/issue'
import type { Issue, StageStateRead, StageCheckState } from '../../../entities/issue'
import { useIsMobile } from '@/shared/hooks/use-mobile'
import { formatDuration } from './format'
import { StageStatusIcon } from './StageStatusIcons'

export const WORKFLOW_STAGES: readonly WorkflowStage[] = [WorkflowStage.Plan, WorkflowStage.Build, WorkflowStage.Check, WorkflowStage.Integrate]

export function getStageStatus(
  stage: WorkflowStage,
  stageStateMap: Map<string, StageStateRead>,
  issue: Issue,
): 'pending' | 'running' | 'completed' | 'failed' | 'awaiting-approval' {
  const stageState = stageStateMap.get(stage)
  const stageOrder = WORKFLOW_STAGES.indexOf(stage)
  const currentStageIdx = issue.workflowStage ? WORKFLOW_STAGES.indexOf(issue.workflowStage) : -1

  if (stageState) {
    if (stageState.status === 'running') return 'running'
    if (stageState.status === 'awaiting-approval') return 'awaiting-approval'
    if (stageState.status === 'completed' || stageState.status === 'passed') return 'completed'
    if (stageState.status === 'failed') return 'failed'
    if (stageState.status === 'skipped') return 'pending'
  }

  if (issue.workflowStage === stage && !stageState) return 'running'

  if (currentStageIdx < 0 || stageOrder > currentStageIdx) return 'pending'

  return 'pending'
}

export function getStageDuration(stage: WorkflowStage, stageStateMap: Map<string, StageStateRead>): number | null {
  const stageState = stageStateMap.get(stage)
  if (!stageState) return null
  if (stageState.startedAt && stageState.completedAt) {
    const started = new Date(stageState.startedAt).getTime()
    const completed = new Date(stageState.completedAt).getTime()
    if (!Number.isNaN(started) && !Number.isNaN(completed)) {
      return Math.max(0, completed - started)
    }
  }
  if (stageState.tasks.length === 0) return null
  const total = stageState.tasks.reduce((sum, t) => sum + (t.duration || 0), 0)
  return total > 0 ? total : null
}

export function workflowTimelineToStageStateMap(timeline: ReturnType<typeof useWorkflowTimeline>['data']): Map<string, StageStateRead> {
  const map = new Map<string, StageStateRead>()
  if (!timeline) return map

  for (const stage of timeline.stages) {
    map.set(stage.stage, {
      stage: stage.stage,
      status: stage.status,
      tasks: stage.tasks.map((task, index) => ({
        taskId: task.id,
        title: task.title,
        status: task.status,
        sessionName: task.sessionName,
        order: index,
        attempts: task.attempts,
        duration: task.durationMs ?? 0,
        artifacts: [],
        artifactSummaries: task.artifactSummaries,
        output: task.output ?? null,
        startedAt: task.startedAt,
        completedAt: task.completedAt,
        updatedAt: task.completedAt ?? task.startedAt ?? '',
        reason: task.message ?? undefined,
        origin: task.uses ? { source: 'runtime', uses: task.uses } : null,
        requiredFiles: task.requiredFiles,
        classification: task.classification,
      })),
      checks: stage.checks.map((check) => ({
        checkName: check.name,
        title: check.title,
        status: check.status as StageCheckState['status'],
        message: check.message,
        output: null,
        runCount: 1,
        lastRunAt: check.completedAt ?? check.startedAt,
        origin: check.uses ? { source: 'runtime', uses: check.uses } : null,
        updatedAt: check.completedAt ?? check.startedAt ?? '',
      })),
      approval: stage.approval,
      attempts: 1,
      startedAt: stage.startedAt,
      completedAt: stage.completedAt,
      updatedAt: stage.completedAt ?? stage.startedAt ?? '',
    })
  }

  return map
}

function StageBarCell({
  stage,
  status,
  duration,
  selected,
  readOnly,
  onClick,
  isMobile,
}: {
  stage: WorkflowStage
  status: string
  duration: number | null
  selected: boolean
  readOnly: boolean
  onClick: () => void
  isMobile: boolean
}) {
  const bgColor = selected ? 'bg-muted border-border' : 'bg-background border-border'
  const stageLabel = stage.charAt(0).toUpperCase() + stage.slice(1)
  const layoutClass = isMobile ? 'min-w-32 shrink-0' : 'flex-1 min-w-0'
  const labelClass = isMobile ? 'whitespace-nowrap' : 'truncate'

  return (
    <Button
      variant="ghost"
      onClick={onClick}
      disabled={readOnly && status === 'pending'}
      className={`${layoutClass} rounded-lg border p-3 text-left transition-colors h-auto justify-start font-normal ${bgColor} ${
        !readOnly && status !== 'pending' ? 'cursor-pointer hover:bg-muted' : ''
      } ${status === 'pending' && !selected ? 'opacity-60' : ''}`}
    >
      <div className="flex items-center gap-2 mb-1">
        <StageStatusIcon status={status} />
        <span className={`text-sm font-medium text-foreground ${labelClass}`}>{stageLabel}</span>
      </div>
      {status === 'completed' && duration != null && (
        <span className="text-xs text-muted-foreground/70 ml-7">{formatDuration(duration)}</span>
      )}
      {status === 'running' && duration != null && (
        <span className="text-xs text-info ml-7">{formatDuration(duration)}</span>
      )}
    </Button>
  )
}

export function StageBar({
  stageStateMap,
  issue,
  selectedStage,
  onSelectStage,
  readOnly,
}: {
  stageStateMap: Map<string, StageStateRead>
  issue: Issue
  selectedStage: WorkflowStage
  onSelectStage: (stage: WorkflowStage) => void
  readOnly: boolean
}) {
  const isMobile = useIsMobile()

  return (
    <div
      className={`flex items-stretch gap-2 ${isMobile ? 'overflow-x-auto flex-nowrap pb-1' : ''}`}
      data-testid={isMobile ? 'workflow-stage-bar-scrollable-stepper' : 'workflow-stage-bar'}
    >
      {WORKFLOW_STAGES.map((stage, idx) => {
        const status = getStageStatus(stage, stageStateMap, issue)
        const duration = getStageDuration(stage, stageStateMap)
        return (
          <div key={stage} className={`flex items-stretch ${isMobile ? 'shrink-0' : 'flex-1 min-w-0'}`}>
            {idx > 0 && (
              <div className="flex items-center px-1">
                <svg className="h-4 w-4 text-muted-foreground/40 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
                  <path
                    fillRule="evenodd"
                    d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
                    clipRule="evenodd"
                  />
                </svg>
              </div>
            )}
            <StageBarCell
              stage={stage}
              status={status}
              duration={duration}
              selected={selectedStage === stage}
              readOnly={readOnly}
              onClick={() => onSelectStage(stage)}
              isMobile={isMobile}
            />
          </div>
        )
      })}
    </div>
  )
}