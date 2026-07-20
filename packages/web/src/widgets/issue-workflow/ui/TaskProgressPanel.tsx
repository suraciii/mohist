import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/shared/ui/components/card'
import type { StageTaskState, WorkflowStage, WorkflowTimeline } from '../../../entities/issue'
import { useWorkflowTimeline } from '../../../entities/issue'
import { getDeliveryFailureGuidance } from '../../../shared/lib/delivery-failure'
import { TaskLogPanel, type TaskLogDataHook } from './TaskLogPanel'

function parseTaskOutput(raw: string | null | undefined): unknown {
  if (typeof raw !== 'string') return null
  const trimmed = raw.trim()
  if (!trimmed) return null
  try {
    return JSON.parse(trimmed)
  } catch {
    return raw
  }
}

export type TaskProgressTimelineHook = (
  issueNumber: number,
  enabled: boolean,
) => { data: WorkflowTimeline | null | undefined }

export interface TaskProgressPanelProps {
  issueNumber: number
  currentStage: WorkflowStage
  isAgentRunning: boolean
  timelineHook?: TaskProgressTimelineHook
  taskLogHook?: TaskLogDataHook
}

function StageTaskStatusIcon({ status }: { status: StageTaskState['status'] }) {
  if (status === 'failed') {
    return (
      <svg className="h-4 w-4 text-red-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
      </svg>
    )
  }
  if (status === 'completed') {
    return (
      <svg className="h-4 w-4 text-green-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  return <span className="inline-block h-2 w-2 rounded-full bg-muted-foreground/30 flex-shrink-0" />
}

function TaskItem({ task, isRunning, issueNumber, workflowRunId, taskLogHook }: { task: StageTaskState; isRunning: boolean; issueNumber: number; workflowRunId?: string | null; taskLogHook?: TaskLogDataHook }) {
  const [expanded, setExpanded] = useState(false)
  const isFailed = task.status === 'failed'
  const canExpand = typeof task.taskId === 'string' && task.taskId.length > 0 && (task.status === 'failed' || task.status === 'completed' || task.status === 'running')
  const isInProgress = isRunning && task.status === 'running'
  const isDeliveryTask = isDeliveryFailureTask(task)
  const taskReason = task.error?.message ?? (typeof task.reason === 'string' ? task.reason : null)
  const deliveryGuidance = isFailed && isDeliveryTask
    ? getDeliveryFailureGuidance(task.error?.code)
    : null
  const failureKind = deliveryGuidance?.failureKind
  const isWorkspaceSetupFailure = failureKind === 'workspace-setup'

  return (
    <div className={`rounded-md border ${isFailed ? 'border-red-200' : 'border'} overflow-hidden`}>
      <Button
        variant="ghost"
        onClick={() => canExpand && setExpanded(!expanded)}
        className={`w-full flex items-center gap-2 px-2.5 py-2 text-left h-auto justify-start font-normal ${isFailed ? 'hover:bg-red-50' : ''} ${canExpand ? 'cursor-pointer' : ''}`}
      >
        <StageTaskStatusIcon status={task.status} />
        <span className={`text-sm flex-1 truncate ${isFailed ? 'text-red-700' : task.status === 'completed' ? 'text-foreground/80' : 'text-muted-foreground'}`}>
          {task.title}
        </span>
        {isInProgress && (
          <svg className="h-3.5 w-3.5 text-blue-500 animate-spin flex-shrink-0" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
        )}
        {task.attempts > 1 && (
          <span className="text-[10px] text-muted-foreground/70 flex-shrink-0">
            {task.attempts} attempts
          </span>
        )}
        {canExpand && (
          <svg className={`h-3 w-3 text-muted-foreground/70 flex-shrink-0 transition-transform ${expanded ? 'rotate-180' : ''}`} viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 10.94l3.71-3.71a.75.75 0 111.06 1.06l-4.25 4.25a.75.75 0 01-1.06 0L5.23 8.27a.75.75 0 01.02-1.06z" clipRule="evenodd" />
          </svg>
        )}
      </Button>
      {expanded && canExpand && (
        <div className={`px-2.5 pb-2 border-t space-y-1.5 ${isFailed ? 'border-red-100 bg-red-50/50' : 'border-slate-100 bg-slate-50/50'}`}>
          {isFailed && taskReason && (
            <p className="text-xs text-amber-600">{taskReason}</p>
          )}
          {deliveryGuidance && (
            <div
              className={`rounded border px-2 py-1.5 text-xs space-y-1 ${
                deliveryGuidance.failureKind === 'branch-invariant-violation'
                  ? 'border-purple-300 bg-purple-50 text-purple-800'
                  : isWorkspaceSetupFailure
                    ? 'border-rose-300 bg-rose-50 text-rose-800'
                    : 'border-red-200 bg-white text-red-700'
              }`}
            >
              <div className="flex items-center gap-2 font-semibold">
                <span className="text-[10px] uppercase tracking-wide opacity-80">Failure kind</span>
                <span
                  className={`rounded px-1.5 py-0.5 font-mono text-[11px] ${
                    deliveryGuidance.failureKind === 'branch-invariant-violation'
                      ? 'bg-white/70'
                      : isWorkspaceSetupFailure
                        ? 'bg-white/70'
                        : 'bg-red-100'
                  }`}
                >
                  {deliveryGuidance.failureKind}
                </span>
                <span>{deliveryGuidance.label}</span>
              </div>
              <p className="leading-snug">{deliveryGuidance.nextAction}</p>
            </div>
          )}
          {isFailed && (
            <p className="text-xs text-red-600 whitespace-pre-wrap">
              {typeof task.output === 'string' ? task.output : task.output != null ? JSON.stringify(task.output) : 'Task failed'}
            </p>
          )}
          {typeof task.taskId === 'string' && task.taskId.length > 0 && (
            <TaskLogPanel
              issueNumber={issueNumber}
              taskId={task.taskId}
              workflowRunId={workflowRunId}
              taskStatus={task.status}
              sessionName={task.sessionName ?? null}
              origin={task.origin ?? null}
              classification={task.classification ?? null}
              taskLogHook={taskLogHook}
            />
          )}
        </div>
      )}
    </div>
  )
}

function isDeliveryFailureTask(task: StageTaskState): boolean {
  const uses = task.origin?.uses
  if (typeof uses !== 'string') {
    return typeof task.taskId === 'string' && (
      task.taskId.startsWith('integrate:prepare') ||
      task.taskId.startsWith('integrate:publish') ||
      task.taskId.startsWith('integrate:open-pr') ||
      task.taskId.startsWith('integrate:merge-pr') ||
      task.taskId.startsWith('recover:open-pr') ||
      task.taskId.startsWith('recover:merge-pr')
    )
  }
  return (
    uses === 'mohist/prepare' ||
    uses === 'mohist/publish' ||
    uses === 'mohist/publish-via-pr' ||
    uses === 'mohist/create-pull-request' ||
    uses === 'mohist/merge-pull-request'
  )
}

function ProgressBar({ completed, failed, total }: { completed: number; failed: number; total: number }) {
  if (total === 0) return null
  const completedPct = (completed / total) * 100
  const failedPct = (failed / total) * 100

  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between text-xs">
        <span className="text-muted-foreground">
          {completed}/{total} completed
          {failed > 0 && <span className="text-red-500 ml-1">({failed} failed)</span>}
        </span>
        <span className="text-muted-foreground/70">{Math.round((completed / total) * 100)}%</span>
      </div>
      <div className="h-2 rounded-full bg-muted overflow-hidden">
        <div className="flex h-full">
          <div className="h-full bg-green-500 transition-all duration-300" style={{ width: `${completedPct}%` }} />
          <div className="h-full bg-red-400 transition-all duration-300" style={{ width: `${failedPct}%` }} />
        </div>
      </div>
    </div>
  )
}

export function TaskProgressPanel({
  issueNumber,
  currentStage,
  isAgentRunning,
  timelineHook = useWorkflowTimeline,
  taskLogHook,
}: TaskProgressPanelProps) {
  const { data: timeline } = timelineHook(issueNumber, true)

  const stage = timeline?.stages.find((s) => s.stage === currentStage)
  const tasks: StageTaskState[] = (stage?.tasks ?? []).map((task, index) => ({
    taskId: task.id,
    title: task.title,
    status: task.status,
    sessionName: task.sessionName,
    order: index,
    attempts: task.attempts,
    duration: task.durationMs ?? 0,
    artifacts: [],
    output: parseTaskOutput(task.output),
    error: task.error,
    startedAt: task.startedAt,
    completedAt: task.completedAt,
    updatedAt: task.completedAt ?? task.startedAt ?? new Date().toISOString(),
    reason: task.message ?? undefined,
    origin: task.uses ? { source: 'runtime', uses: task.uses } : null,
    classification: task.classification,
  }))

  if (tasks.length === 0) {
    return (
      <Card>
        <CardHeader className="pb-2">
          <CardTitle className="text-sm">Task Progress</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="text-sm text-muted-foreground/70">No tasks available yet</div>
        </CardContent>
      </Card>
    )
  }

  const completed = tasks.filter((t) => t.status === 'completed').length
  const failed = tasks.filter((t) => t.status === 'failed').length
  const total = tasks.length
  const runningTask = tasks.find(t => t.status === 'running')

  return (
    <Card>
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between">
          <CardTitle className="text-sm">Task Progress</CardTitle>
          {isAgentRunning && (
            <span className="inline-flex items-center gap-1 text-xs text-blue-600">
              <span className="inline-block h-1.5 w-1.5 rounded-full bg-blue-500 animate-pulse" />
              Running
            </span>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-3">
        <ProgressBar completed={completed} failed={failed} total={total} />

        {runningTask && isAgentRunning && (
          <div className="text-xs text-blue-600 bg-blue-50 rounded-md px-2.5 py-1.5">
            Current: {runningTask.title}
          </div>
        )}

        <div className="space-y-1">
          {tasks.map((task) => (
            <TaskItem key={task.taskId} task={task} isRunning={isAgentRunning} issueNumber={issueNumber} workflowRunId={timeline?.workflowRunId ?? null} taskLogHook={taskLogHook} />
          ))}
        </div>
      </CardContent>
    </Card>
  )
}
