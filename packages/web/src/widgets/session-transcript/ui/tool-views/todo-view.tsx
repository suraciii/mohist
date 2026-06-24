import { parseJsonSafely } from '../../model/transcript-tool-utils'

interface TodoContentViewProps {
  input?: string
}

export function TodoContentView({ input }: TodoContentViewProps) {
  const parsed = input ? parseJsonSafely(input) : null
  if (!parsed) return null
  const todos = parsed.todos
  if (!Array.isArray(todos) || todos.length === 0) return null

  const completed = todos.filter((t: any) => t.status === 'completed').length
  const pending = todos.filter((t: any) => t.status === 'pending').length
  const inProgress = todos.filter((t: any) => t.status === 'in_progress').length

  return (
    <div className="border-t border-gray-100 px-3 py-2">
      <div className="flex items-center gap-2 mb-1.5">
        <span className="text-xs font-medium text-gray-500">
          {completed}/{todos.length} completed
        </span>
        {inProgress > 0 && (
          <span className="text-xs text-blue-600">{inProgress} in progress</span>
        )}
        {pending > 0 && (
          <span className="text-xs text-gray-400">{pending} pending</span>
        )}
      </div>
      <div className="space-y-0.5">
        {todos.slice(0, 8).map((todo: any, i: number) => {
          const statusIcon = todo.status === 'completed' ? 'done' : todo.status === 'in_progress' ? 'doing' : 'todo'
          return (
            <div key={i} className="flex items-center gap-1.5 text-xs">
              <span className={`shrink-0 w-3 text-center ${todo.status === 'completed' ? 'text-green-500' : todo.status === 'in_progress' ? 'text-blue-500' : 'text-gray-300'}`}>
                {statusIcon === 'done' ? 'done' : statusIcon === 'doing' ? '>' : 'o'}
              </span>
              <span className={`truncate ${todo.status === 'completed' ? 'text-gray-400 line-through' : 'text-gray-700'}`}>
                {todo.content ?? todo.title ?? `Task ${i + 1}`}
              </span>
            </div>
          )
        })}
        {todos.length > 8 && (
          <span className="text-xs text-gray-400">...and {todos.length - 8} more</span>
        )}
      </div>
    </div>
  )
}
