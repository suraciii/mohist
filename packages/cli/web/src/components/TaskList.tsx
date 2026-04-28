import type { Task } from '../lib/types'
import { useLiveTask } from '../hooks/useSSE'

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) {
    const sec = ms / 1000
    return sec % 1 === 0 ? `${sec}s` : `${sec.toFixed(1)}s`
  }
  const min = Math.floor(ms / 60000)
  const sec = Math.round((ms % 60000) / 1000)
  return sec > 0 ? `${min}m ${sec}s` : `${min}m`
}

function formatDurationShort(ms: number): string {
  if (ms < 60000) return `${Math.round(ms / 1000)}s`
  return `${Math.floor(ms / 60000)}m`
}

function CheckIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function CrossIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function SpinnerIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="none">
      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
      <path
        className="opacity-75"
        fill="currentColor"
        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
      />
    </svg>
  )
}

function StatusIcon({ task, isRunning }: { task: Task; isRunning: boolean }) {
  if (task.passes) {
    return <CheckIcon className="h-4 w-4 text-green-500 shrink-0" />
  }

  if (task.error) {
    return <CrossIcon className="h-4 w-4 text-red-500 shrink-0" />
  }

  if (isRunning) {
    return <span className="inline-block h-3 w-3 rounded-full bg-blue-500 animate-pulse shrink-0" />
  }

  return <span className="inline-block h-3 w-3 rounded-full border-2 border-gray-300 shrink-0" />
}

function DurationBadge({
  durations,
  passes,
  isLive,
  liveElapsedMs,
}: {
  durations?: number[]
  passes: boolean
  isLive: boolean
  liveElapsedMs: number | null
}) {
  if (isLive && liveElapsedMs !== null) {
    return (
      <span className="inline-flex items-center gap-1 text-xs text-blue-600 font-mono tabular-nums">
        <SpinnerIcon className="h-3 w-3 animate-spin" />
        {formatDuration(liveElapsedMs)}
      </span>
    )
  }

  if (!durations || durations.length === 0) return null

  const lastDuration = durations[durations.length - 1]
  const icon = passes ? (
    <CheckIcon className="h-3.5 w-3.5 text-green-500" />
  ) : (
    <CrossIcon className="h-3.5 w-3.5 text-red-500" />
  )

  if (durations.length === 1) {
    return (
      <span className="inline-flex items-center gap-1 text-xs text-gray-500 font-mono">
        {icon}
        {formatDuration(lastDuration)}
      </span>
    )
  }

  const totalMs = durations.reduce((a, b) => a + b, 0)
  const tooltipLines = durations
    .map((d, i) => `Attempt ${i + 1}: ${formatDuration(d)}`)
    .join('\n')

  return (
    <span className="group relative inline-flex items-center gap-1">
      <span className="inline-flex items-center gap-1 text-xs text-gray-500 font-mono">
        {icon}
        {formatDurationShort(lastDuration)}
      </span>
      <span className="ml-0.5 inline-flex items-center rounded bg-gray-100 px-1 py-0.5 text-[10px] font-medium text-gray-600">
        {durations.length}x
      </span>
      <span className="absolute bottom-full left-0 mb-1 hidden group-hover:block z-10 whitespace-pre rounded bg-gray-900 px-2 py-1.5 text-[11px] text-white font-mono shadow-lg">
        {tooltipLines}{'\n'}Total: {formatDuration(totalMs)}
      </span>
    </span>
  )
}

function BlockedHint({ task, allTasks }: { task: Task; allTasks: Task[] }) {
  if (!task.dependsOn || task.dependsOn.length === 0) return null

  const taskMap = new Map(allTasks.map((t) => [t.id, t]))
  const blockers = task.dependsOn.filter((depId) => {
    const dep = taskMap.get(depId)
    return dep && !dep.passes
  })

  if (blockers.length === 0) return null

  return (
    <div className="mt-0.5 text-xs text-amber-600">
      blocked by {blockers.join(', ')}
    </div>
  )
}

interface TaskListProps {
  tasks: Task[]
  currentTask: string | null
  progress: { completed: number; failed: number; total: number }
}

export function TaskList({ tasks, currentTask, progress }: TaskListProps) {
  const { activeTaskId, activeTaskElapsedMs } = useLiveTask()

  if (tasks.length === 0) return null

  const resolvedCurrentId = currentTask ?? activeTaskId

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4">
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-sm font-semibold text-gray-700">Tasks</h2>
        <span className="text-xs text-gray-500">
          {progress.completed}/{progress.total} completed
        </span>
      </div>
      <div className="space-y-2">
        {tasks.map((task) => {
          const isRunning = !task.passes && !task.error && task.id === resolvedCurrentId
          const isLive = isRunning
          return (
            <div key={task.id}>
              <div className="flex items-start gap-2">
                <div className="mt-0.5">
                  <StatusIcon task={task} isRunning={isRunning} />
                </div>
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="text-xs font-mono text-gray-400">{task.id}</span>
                    <span className={`text-sm ${task.passes ? 'text-gray-500 line-through' : 'text-gray-700'}`}>
                      {task.title}
                    </span>
                    <DurationBadge
                      durations={task.durations}
                      passes={task.passes}
                      isLive={isLive}
                      liveElapsedMs={task.id === resolvedCurrentId ? activeTaskElapsedMs : null}
                    />
                  </div>
                  {task.error && (
                    <div className="mt-0.5 text-xs text-red-500 truncate" title={task.error}>
                      {task.error}
                    </div>
                  )}
                  <BlockedHint task={task} allTasks={tasks} />
                </div>
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
