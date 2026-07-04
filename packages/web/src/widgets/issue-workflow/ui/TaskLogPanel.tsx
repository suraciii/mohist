import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { HubConnection } from '@microsoft/signalr'
import { useProject } from '../../../entities/project'
import { useIssueWorkflowTaskLog, type TaskLogLine, type TaskLogPage } from '../../../entities/issue'
import {
  useEventsConnection,
  subscribeTaskLog,
  unsubscribeTaskLog,
  type TaskLogDeltaEnvelopeWire,
} from '../../../shared/api/events-hub'
import type { StageTaskStatus } from '../../../entities/issue/model/stage-state'

interface TaskLogPanelProps {
  issueNumber: number
  taskId: string
  workflowRunId?: string | null
  taskStatus?: StageTaskStatus | null
}

const TASK_LOG_RETAINED_LIMIT = 5000
const TASK_LOG_QUERY_KEY_NAMESPACE = 'workflow-task-log'

const TERMINAL_TASK_STATUSES: ReadonlySet<StageTaskStatus> = new Set<StageTaskStatus>([
  'completed',
  'failed',
  'skipped',
])

function formatTimestamp(iso: string): string {
  const parsed = new Date(iso)
  if (Number.isNaN(parsed.getTime())) return iso
  const hh = String(parsed.getHours()).padStart(2, '0')
  const mm = String(parsed.getMinutes()).padStart(2, '0')
  const ss = String(parsed.getSeconds()).padStart(2, '0')
  const ms = String(parsed.getMilliseconds()).padStart(3, '0')
  return `${hh}:${mm}:${ss}.${ms}`
}

function isTerminalStatus(status: StageTaskStatus | null | undefined): boolean {
  return !!status && TERMINAL_TASK_STATUSES.has(status)
}

function buildTaskLogQueryKey(
  issueNumber: number,
  taskId: string,
  projectId: string,
  workflowRunId: string | null,
  params: { limit: number },
): unknown[] {
  return [issueNumber, taskId, projectId, workflowRunId, TASK_LOG_QUERY_KEY_NAMESPACE, params]
}

function isTaskLogEnvelope(value: unknown): value is TaskLogDeltaEnvelopeWire {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<TaskLogDeltaEnvelopeWire>
  return (
    typeof candidate.ownerKind === 'string' &&
    typeof candidate.ownerId === 'string' &&
    typeof candidate.workId === 'string' &&
    Array.isArray(candidate.entries) &&
    typeof candidate.truncated === 'boolean'
  )
}

export function mergeTaskLogDelta(
  page: TaskLogPage | undefined,
  delta: TaskLogDeltaEnvelopeWire,
): TaskLogPage {
  const lines: TaskLogLine[] = page ? [...page.lines] : []
  const existingSeqs = new Set<number>(lines.map((line) => line.seq))
  const incoming: TaskLogLine[] = []
  for (const entry of delta.entries) {
    if (existingSeqs.has(entry.seq)) continue
    incoming.push({
      seq: entry.seq,
      timestamp: typeof entry.timestamp === 'string' ? entry.timestamp : new Date().toISOString(),
      source: entry.source,
      text: entry.text,
    })
    existingSeqs.add(entry.seq)
  }
  const sorted = lines.concat(incoming).sort((a, b) => a.seq - b.seq)
  const truncated = !!(delta.truncated || page?.truncated)
  return {
    lines: sorted,
    nextCursor: page?.nextCursor ?? null,
    truncated,
  }
}

export function TaskLogPanel({ issueNumber, taskId, workflowRunId, taskStatus }: TaskLogPanelProps) {
  const { projectId } = useProject()
  const queryClient = useQueryClient()
  const { data, isLoading, isError } = useIssueWorkflowTaskLog(
    issueNumber,
    taskId,
    { limit: TASK_LOG_RETAINED_LIMIT },
    true,
    workflowRunId,
  )
  const scrollRef = useRef<HTMLDivElement | null>(null)
  const subscribedRef = useRef(false)
  const subscribedConnectionRef = useRef<HubConnection | null>(null)
  const subscribedReconnectVersionRef = useRef<number | null>(null)
  const terminalNow = isTerminalStatus(taskStatus)

  const onTaskLogDelta = (envelope: unknown) => {
    if (!isTaskLogEnvelope(envelope)) return
    const taskScope = envelope.taskId
    if (taskScope == null) return
    if (taskScope !== taskId) return
    if (envelope.ownerKind !== 'workflow') return
    if (envelope.ownerId !== workflowRunId) return
    if (!projectId) return
    const queryKey = buildTaskLogQueryKey(issueNumber, taskId, projectId, workflowRunId ?? null, { limit: TASK_LOG_RETAINED_LIMIT })
    queryClient.setQueryData<TaskLogPage | undefined>(queryKey, (current) => mergeTaskLogDelta(current, envelope))
  }

  const { connection, reconnectVersion } = useEventsConnection(projectId, () => {}, undefined, onTaskLogDelta)

  useEffect(() => {
    const node = scrollRef.current
    if (!node) return
    node.scrollTop = node.scrollHeight
  }, [data?.lines.length])

  useEffect(() => {
    if (!projectId || !workflowRunId) return

    if (terminalNow) {
      if (subscribedRef.current) {
        subscribedRef.current = false
        const conn = subscribedConnectionRef.current
        subscribedConnectionRef.current = null
        subscribedReconnectVersionRef.current = null
        if (conn) {
          void unsubscribeTaskLog(conn, { workflowRunId, taskId })
        }
      }
      queryClient.invalidateQueries({
        queryKey: [issueNumber, taskId, projectId, workflowRunId, TASK_LOG_QUERY_KEY_NAMESPACE],
      })
      return
    }

    if (taskStatus !== 'running') {
      if (subscribedRef.current) {
        subscribedRef.current = false
        const conn = subscribedConnectionRef.current
        subscribedConnectionRef.current = null
        subscribedReconnectVersionRef.current = null
        if (conn) {
          void unsubscribeTaskLog(conn, { workflowRunId, taskId })
        }
      }
      return
    }

    if (!connection) return
    if (
      subscribedRef.current &&
      subscribedConnectionRef.current === connection &&
      subscribedReconnectVersionRef.current === reconnectVersion
    ) return
    subscribedRef.current = true
    subscribedConnectionRef.current = connection
    subscribedReconnectVersionRef.current = reconnectVersion
    void subscribeTaskLog(connection, { workflowRunId, taskId })
  }, [connection, reconnectVersion, terminalNow, taskStatus, projectId, workflowRunId, issueNumber, taskId, queryClient])

  useEffect(() => {
    return () => {
      const wasSubscribed = subscribedRef.current
      const conn = subscribedConnectionRef.current
      subscribedRef.current = false
      subscribedConnectionRef.current = null
      subscribedReconnectVersionRef.current = null
      if (wasSubscribed && conn && workflowRunId) {
        void unsubscribeTaskLog(conn, { workflowRunId, taskId })
      }
    }
  }, [workflowRunId, taskId])

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
