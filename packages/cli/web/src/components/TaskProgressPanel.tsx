import { useState } from 'react'
import { useIssueStageState } from '../hooks/useQueries'
import { useTaskProgress } from '../hooks/useTaskProgress'
import type { StageTaskState, Stage } from '../lib/types'
import { Stage as StageEnum } from '../lib/types'

interface TaskProgressPanelProps {
  issueNumber: number
  currentStage: Stage
  isAgentRunning: boolean
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
  return <span className="inline-block h-2 w-2 rounded-full bg-gray-300 flex-shrink-0" />
}

function TaskItem({ task, isRunning }: { task: StageTaskState; isRunning: boolean }) {
  const [expanded, setExpanded] = useState(false)
  const isFailed = task.status === 'failed'
  const isInProgress = isRunning && task.status === 'running'

  return (
    <div className={`rounded-md border ${isFailed ? 'border-red-200' : 'border-gray-100'} overflow-hidden`}>
      <button
        onClick={() => isFailed && setExpanded(!expanded)}
        className={`w-full flex items-center gap-2 px-2.5 py-2 text-left ${isFailed ? 'hover:bg-red-50 cursor-pointer' : ''}`}
      >
        <StageTaskStatusIcon status={task.status} />
        <span className={`text-sm flex-1 truncate ${isFailed ? 'text-red-700' : task.status === 'completed' ? 'text-gray-700' : 'text-gray-500'}`}>
          {task.title}
        </span>
        {isInProgress && (
          <svg className="h-3.5 w-3.5 text-blue-500 animate-spin flex-shrink-0" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
        )}
        {task.attempts > 1 && (
          <span className="text-[10px] text-gray-400 flex-shrink-0">
            {task.attempts} attempts
          </span>
        )}
        {isFailed && (
          <svg className={`h-3 w-3 text-gray-400 flex-shrink-0 transition-transform ${expanded ? 'rotate-180' : ''}`} viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 10.94l3.71-3.71a.75.75 0 111.06 1.06l-4.25 4.25a.75.75 0 01-1.06 0L5.23 8.27a.75.75 0 01.02-1.06z" clipRule="evenodd" />
          </svg>
        )}
      </button>
      {expanded && isFailed && (
        <div className="px-2.5 pb-2 border-t border-red-100 bg-red-50/50">
          <p className="text-xs text-red-600 mt-1.5 whitespace-pre-wrap">
            {typeof task.output === 'string' ? task.output : task.output != null ? JSON.stringify(task.output) : 'Task failed'}
          </p>
        </div>
      )}
    </div>
  )
}

function ProgressBar({ completed, failed, total }: { completed: number; failed: number; total: number }) {
  if (total === 0) return null
  const completedPct = (completed / total) * 100
  const failedPct = (failed / total) * 100

  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between text-xs">
        <span className="text-gray-500">
          {completed}/{total} completed
          {failed > 0 && <span className="text-red-500 ml-1">({failed} failed)</span>}
        </span>
        <span className="text-gray-400">{Math.round((completed / total) * 100)}%</span>
      </div>
      <div className="h-2 rounded-full bg-gray-100 overflow-hidden">
        <div className="flex h-full">
          <div className="h-full bg-green-500 transition-all duration-300" style={{ width: `${completedPct}%` }} />
          <div className="h-full bg-red-400 transition-all duration-300" style={{ width: `${failedPct}%` }} />
        </div>
      </div>
    </div>
  )
}

export function TaskProgressPanel({ issueNumber, currentStage, isAgentRunning }: TaskProgressPanelProps) {
  useTaskProgress(issueNumber)

  const { data: stageStateData, isLoading: stageStateLoading } = useIssueStageState(issueNumber)

  const isBacklog = currentStage === StageEnum.Backlog || currentStage === StageEnum.Draft

  if (isBacklog) return null

  const currentStageState = stageStateData?.stages?.find(s => s.stage === currentStage)
  const tasks: StageTaskState[] = currentStageState?.tasks ?? []

  if (stageStateLoading) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-gray-700 mb-3">Task Progress</h2>
        <div className="text-sm text-gray-400">Loading progress...</div>
      </div>
    )
  }

  if (tasks.length === 0) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-gray-700 mb-2">Task Progress</h2>
        <div className="text-sm text-gray-400">No tasks available yet</div>
      </div>
    )
  }

  const completed = tasks.filter((t) => t.status === 'completed').length
  const failed = tasks.filter((t) => t.status === 'failed').length
  const total = tasks.length
  const runningTask = tasks.find(t => t.status === 'running')

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-3">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-gray-700">Task Progress</h2>
        {isAgentRunning && (
          <span className="inline-flex items-center gap-1 text-xs text-blue-600">
            <span className="inline-block h-1.5 w-1.5 rounded-full bg-blue-500 animate-pulse" />
            Running
          </span>
        )}
      </div>

      <ProgressBar completed={completed} failed={failed} total={total} />

      {runningTask && isAgentRunning && (
        <div className="text-xs text-blue-600 bg-blue-50 rounded-md px-2.5 py-1.5">
          Current: {runningTask.title}
        </div>
      )}

      <div className="space-y-1">
        {tasks.map((task) => (
          <TaskItem key={task.taskId} task={task} isRunning={isAgentRunning} />
        ))}
      </div>
    </div>
  )
}
