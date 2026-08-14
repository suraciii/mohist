import { Button } from '@/shared/ui/components/button'
import { WorkflowStage, useWorkflowTimeline } from '../../../entities/issue'
import type { Issue, StageStateRead, StageCheckState } from '../../../entities/issue'
import { formatDuration } from './format'
import { StageStatusIcon } from './StageStatusIcons'

export const WORKFLOW_STAGES: readonly WorkflowStage[] = [
  WorkflowStage.Plan,
  WorkflowStage.Build,
  WorkflowStage.Check,
  WorkflowStage.Integrate,
]

export function getStageStatus(
  stage: WorkflowStage,
  stageStateMap: Map<string, StageStateRead>,
  issue: Issue,
): 'pending' | 'running' | 'completed' | 'failed' | 'blocked' | 'awaiting-approval' {
  const stageState = stageStateMap.get(stage)
  const stageOrder = WORKFLOW_STAGES.indexOf(stage)
  const currentStageIdx = issue.workflowStage ? WORKFLOW_STAGES.indexOf(issue.workflowStage) : -1

  if (stageState) {
    if (stageState.status === 'running') return 'running'
    if (stageState.status === 'awaiting-approval') return 'awaiting-approval'
    if (stageState.status === 'completed' || stageState.status === 'passed') return 'completed'
    if (stageState.status === 'failed') return 'failed'
    if (stageState.status === 'blocked') return 'blocked'
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

export function workflowTimelineToStageStateMap(
  timeline: ReturnType<typeof useWorkflowTimeline>['data'],
): Map<string, StageStateRead> {
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
        error: task.error,
        startedAt: task.startedAt,
        completedAt: task.completedAt,
        updatedAt: task.completedAt ?? task.startedAt ?? '',
        reason: task.message ?? undefined,
        origin: task.uses ? { source: 'runtime', uses: task.uses } : null,
        requiredFiles: task.requiredFiles,
        classification: task.classification,
        agentResultSettlement: task.agentResultSettlement,
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
  onClick,
}: {
  stage: WorkflowStage
  status: string
  duration: number | null
  selected: boolean
  onClick: () => void
}) {
  const bgColor = selected ? 'bg-muted border-border' : 'bg-background border-border'
  const stageLabel = stage.charAt(0).toUpperCase() + stage.slice(1)

  return (
    <Button
      variant="ghost"
      onClick={onClick}
      aria-current={selected ? 'step' : undefined}
      className={`min-w-0 rounded-md border p-3 text-left transition-colors h-auto justify-start font-normal hover:bg-muted ${bgColor} ${
        status === 'pending' && !selected ? 'opacity-60' : ''
      }`}
    >
      <div className="flex min-w-0 items-center gap-2 mb-1">
        <StageStatusIcon status={status} />
        <span className="min-w-0 break-words text-sm font-medium text-foreground">{stageLabel}</span>
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
}: {
  stageStateMap: Map<string, StageStateRead>
  issue: Issue
  selectedStage: WorkflowStage
  onSelectStage: (stage: WorkflowStage) => void
}) {
  return (
    <div className="grid min-w-0 grid-cols-2 gap-2 sm:grid-cols-4" data-testid="workflow-stage-bar">
      {WORKFLOW_STAGES.map((stage) => {
        const status = getStageStatus(stage, stageStateMap, issue)
        const duration = getStageDuration(stage, stageStateMap)
        return (
          <div key={stage} className="grid min-w-0">
            <StageBarCell
              stage={stage}
              status={status}
              duration={duration}
              selected={selectedStage === stage}
              onClick={() => onSelectStage(stage)}
            />
          </div>
        )
      })}
    </div>
  )
}
