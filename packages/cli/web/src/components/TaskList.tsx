import type { Task } from '../lib/types'

interface TaskListProps {
  tasks: Task[]
  currentTask: string | null
  progress: { completed: number; failed: number; total: number }
}

function StatusIcon({ task, isRunning }: { task: Task; isRunning: boolean }) {
  if (task.passes) {
    return (
      <svg className="h-4 w-4 text-green-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }

  if (task.error) {
    return (
      <svg className="h-4 w-4 text-red-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
      </svg>
    )
  }

  if (isRunning) {
    return <span className="inline-block h-3 w-3 rounded-full bg-blue-500 animate-pulse shrink-0" />
  }

  return <span className="inline-block h-3 w-3 rounded-full border-2 border-gray-300 shrink-0" />
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

export function TaskList({ tasks, currentTask, progress }: TaskListProps) {
  if (tasks.length === 0) return null

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
          const isRunning = !task.passes && !task.error && task.id === currentTask
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
