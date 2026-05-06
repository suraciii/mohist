import { useState } from 'react'
import { useTasks, useBuildStatus } from '../hooks/useQueries'
import { useTaskProgress } from '../hooks/useTaskProgress'
import type { Task, Stage } from '../lib/types'
import { Stage as StageEnum } from '../lib/types'

interface TaskProgressPanelProps {
  issueNumber: number
  currentStage: Stage
  isAgentRunning: boolean
}

function TaskStatusIcon({ passes, error }: { passes: boolean; error?: string | null }) {
  if (error) {
    return (
      <svg className="h-4 w-4 text-red-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
      </svg>
    )
  }
  if (passes) {
    return (
      <svg className="h-4 w-4 text-green-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  return <span className="inline-block h-2 w-2 rounded-full bg-gray-300 flex-shrink-0" />
}

function TaskItem({ task, isRunning }: { task: Task; isRunning: boolean }) {
  const [expanded, setExpanded] = useState(false)
  const hasError = !!task.error
  const isInProgress = isRunning && !task.passes && !hasError

  return (
    <div className={`rounded-md border ${hasError ? 'border-red-200' : 'border-gray-100'} overflow-hidden`}>
      <button
        onClick={() => hasError && setExpanded(!expanded)}
        className={`w-full flex items-center gap-2 px-2.5 py-2 text-left ${hasError ? 'hover:bg-red-50 cursor-pointer' : ''}`}
      >
        <TaskStatusIcon passes={task.passes} error={task.error} />
        <span className={`text-sm flex-1 truncate ${hasError ? 'text-red-700' : task.passes ? 'text-gray-700' : 'text-gray-500'}`}>
          {task.title}
        </span>
        {isInProgress && (
          <svg className="h-3.5 w-3.5 text-blue-500 animate-spin flex-shrink-0" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
        )}
        {task.attempts > 0 && (
          <span className="text-[10px] text-gray-400 flex-shrink-0">
            {task.attempts > 1 ? `${task.attempts} attempts` : '1 attempt'}
          </span>
        )}
        {hasError && (
          <svg className={`h-3 w-3 text-gray-400 flex-shrink-0 transition-transform ${expanded ? 'rotate-180' : ''}`} viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 10.94l3.71-3.71a.75.75 0 111.06 1.06l-4.25 4.25a.75.75 0 01-1.06 0L5.23 8.27a.75.75 0 01.02-1.06z" clipRule="evenodd" />
          </svg>
        )}
      </button>
      {expanded && hasError && (
        <div className="px-2.5 pb-2 border-t border-red-100 bg-red-50/50">
          <p className="text-xs text-red-600 mt-1.5 whitespace-pre-wrap">{task.error}</p>
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

  const { data: tasksData, isLoading: tasksLoading } = useTasks(issueNumber)
  const { data: buildStatus, isLoading: buildStatusLoading } = useBuildStatus(issueNumber)

  const isBacklog = currentStage === StageEnum.Backlog || currentStage === StageEnum.Draft
  const isPlan = currentStage === StageEnum.Plan

  if (isBacklog) return null

  const loading = tasksLoading && buildStatusLoading
  const hasTasks = tasksData && tasksData.tasks && tasksData.tasks.length > 0
  const tasks = buildStatus?.tasks ?? tasksData?.tasks ?? []

  if (isPlan) {
    if (loading) {
      return (
        <div className="rounded-lg border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold text-gray-700 mb-3">Task Breakdown</h2>
          <div className="text-sm text-gray-400">Loading tasks...</div>
        </div>
      )
    }

    if (!hasTasks) {
      return (
        <div className="rounded-lg border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold text-gray-700 mb-2">Task Breakdown</h2>
          <div className="text-sm text-gray-400">Agent is still designing tasks...</div>
        </div>
      )
    }

    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-gray-700 mb-3">Task Breakdown</h2>
        <div className="space-y-1.5">
          {tasks.map((task) => (
            <div key={task.id} className="flex items-center gap-2 px-2 py-1.5 rounded-md bg-gray-50">
              <span className="inline-block h-2 w-2 rounded-full bg-gray-300 flex-shrink-0" />
              <span className="text-sm text-gray-600 flex-1 truncate">{task.title}</span>
            </div>
          ))}
        </div>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-gray-700 mb-3">Task Progress</h2>
        <div className="text-sm text-gray-400">Loading progress...</div>
      </div>
    )
  }

  if (!hasTasks) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold text-gray-700 mb-2">Task Progress</h2>
        <div className="text-sm text-gray-400">No tasks available yet</div>
      </div>
    )
  }

  const progress = buildStatus?.progress
  const completed = progress?.completed ?? tasks.filter((t) => t.passes).length
  const failed = progress?.failed ?? tasks.filter((t) => t.error).length
  const total = progress?.total ?? tasks.length

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

      {progress?.currentTask && isAgentRunning && (
        <div className="text-xs text-blue-600 bg-blue-50 rounded-md px-2.5 py-1.5">
          Current: {progress.currentTask}
        </div>
      )}

      <div className="space-y-1">
        {tasks.map((task) => (
          <TaskItem key={task.id} task={task} isRunning={isAgentRunning} />
        ))}
      </div>
    </div>
  )
}
