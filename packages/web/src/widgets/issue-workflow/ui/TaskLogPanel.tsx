import { useEffect, useRef } from 'react'
import { useIssueWorkflowTaskLog } from '../../../entities/issue'

interface TaskLogPanelProps {
  issueNumber: number
  taskId: string
}

function formatTimestamp(iso: string): string {
  const parsed = new Date(iso)
  if (Number.isNaN(parsed.getTime())) return iso
  const hh = String(parsed.getHours()).padStart(2, '0')
  const mm = String(parsed.getMinutes()).padStart(2, '0')
  const ss = String(parsed.getSeconds()).padStart(2, '0')
  const ms = String(parsed.getMilliseconds()).padStart(3, '0')
  return `${hh}:${mm}:${ss}.${ms}`
}

export function TaskLogPanel({ issueNumber, taskId }: TaskLogPanelProps) {
  const { data, isLoading, isError } = useIssueWorkflowTaskLog(issueNumber, taskId, {}, true)
  const scrollRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    const node = scrollRef.current
    if (!node) return
    node.scrollTop = node.scrollHeight
  }, [data?.lines.length])

  const lines = data?.lines ?? []
  const truncated = data?.truncated ?? false

  return (
    <div className="rounded border border-slate-200 bg-white px-2 py-1.5 space-y-1" data-testid="task-log-panel">
      <div className="flex items-center gap-2 text-[10px] uppercase tracking-wide text-slate-500">
        <span>Execution log</span>
        {truncated && (
          <span
            className="rounded bg-amber-100 text-amber-800 px-1.5 py-0.5 font-mono normal-case tracking-normal"
            data-testid="task-log-truncation-indicator"
          >
            Earlier lines truncated — showing retained tail
          </span>
        )}
      </div>
      <div
        ref={scrollRef}
        className="max-h-64 overflow-y-auto rounded bg-slate-900 text-slate-100 font-mono text-[11px] leading-snug p-2"
        data-testid="task-log-scroll"
      >
        {isLoading ? (
          <div className="text-slate-400">Loading execution log…</div>
        ) : isError ? (
          <div className="text-slate-400">Execution log unavailable</div>
        ) : lines.length === 0 ? (
          <div className="text-slate-400" data-testid="task-log-empty">
            No execution log captured for this task.
          </div>
        ) : (
          <ol className="space-y-0.5" data-testid="task-log-lines">
            {lines.map((line) => (
              <li key={line.seq} className="flex gap-2 whitespace-pre-wrap break-words">
                <span className="text-slate-500 flex-shrink-0">{formatTimestamp(line.timestamp)}</span>
                <span className="text-sky-300 flex-shrink-0">[{line.source}]</span>
                <span className="flex-1 min-w-0">{line.text}</span>
              </li>
            ))}
          </ol>
        )}
      </div>
    </div>
  )
}