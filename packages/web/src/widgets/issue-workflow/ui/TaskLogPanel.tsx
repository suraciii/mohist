import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { HubConnection } from '@microsoft/signalr'
import { DiamondIcon } from 'lucide-react'
import { useProject } from '../../../entities/project'
import {
  issueWorkflowTaskLogQueryOptions,
  useIssueWorkflowTaskLog,
  type TaskLogLine,
  type TaskLogPage,
} from '../../../entities/issue'
import { useWorkflowRunSessions } from '../../../entities/coder-session'
import type { WorkflowRunSession } from '../../../entities/coder-session'
import {
  useEventsConnection,
  subscribeTaskLog,
  unsubscribeTaskLog,
  type TaskLogDeltaEnvelopeWire,
} from '../../../shared/api/events-hub'
import type { StageTaskStatus } from '../../../entities/issue'
import {
  deriveMilestones,
  isInlineAgentTask,
  isTaskLogMilestone,
  mergeTimelineRows,
  serializeMilestoneForExport,
  type TaskLogMilestone,
  type TimelineRow,
} from './milestones'

export interface TaskLogDataHookInput {
  issueNumber: number
  taskId: string
  projectId: string | null | undefined
  workflowRunId: string | null
}

export interface TaskLogDataResult {
  data: TaskLogPage | undefined
  isLoading: boolean
  isError: boolean
}

export type TaskLogDataHook = (input: TaskLogDataHookInput) => TaskLogDataResult

export type WorkflowRunSessionsHook = (
  workflowRunId: string | null | undefined,
) => { sessions: WorkflowRunSession[]; isLoading: boolean }

export interface TaskLogPanelProps {
  issueNumber: number
  taskId: string
  workflowRunId?: string | null
  taskStatus?: StageTaskStatus | null
  sessionName?: string | null
  origin?: { uses?: string } | null
  classification?: string | null
  taskLogHook?: TaskLogDataHook
  workflowSessionsHook?: WorkflowRunSessionsHook
}

const TASK_LOG_RETAINED_LIMIT = 5000
const TASK_LOG_QUERY_KEY_NAMESPACE = 'workflow-task-log'

const TERMINAL_TASK_STATUSES: ReadonlySet<StageTaskStatus> = new Set<StageTaskStatus>([
  'completed',
  'failed',
  'skipped',
])

const useDefaultTaskLogData: TaskLogDataHook = ({ issueNumber, taskId, workflowRunId }) =>
  useIssueWorkflowTaskLog(
    issueNumber,
    taskId,
    { limit: TASK_LOG_RETAINED_LIMIT },
    true,
    workflowRunId,
  )

function formatTimestamp(iso: string): string {
  const parsed = new Date(iso)
  if (Number.isNaN(parsed.getTime())) return iso
  const hh = String(parsed.getUTCHours()).padStart(2, '0')
  const mm = String(parsed.getUTCMinutes()).padStart(2, '0')
  const ss = String(parsed.getUTCSeconds()).padStart(2, '0')
  const ms = String(parsed.getUTCMilliseconds()).padStart(3, '0')
  return `${hh}:${mm}:${ss}.${ms}`
}

function isTerminalStatus(status: StageTaskStatus | null | undefined): boolean {
  return !!status && TERMINAL_TASK_STATUSES.has(status)
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

function buildExportFilename(taskId: string): string {
  const isoDate = new Date().toISOString().slice(0, 10)
  return `task-logs-${taskId}-${isoDate}.txt`
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
  const retained = sorted.length > TASK_LOG_RETAINED_LIMIT
    ? sorted.slice(sorted.length - TASK_LOG_RETAINED_LIMIT)
    : sorted
  const truncated = !!(delta.truncated || page?.truncated || retained.length < sorted.length)
  return {
    lines: retained,
    nextCursor: retained.length < sorted.length ? null : (page?.nextCursor ?? null),
    truncated,
  }
}

export function TaskLogPanel({
  issueNumber,
  taskId,
  workflowRunId,
  taskStatus,
  sessionName,
  origin,
  classification,
  taskLogHook = useDefaultTaskLogData,
  workflowSessionsHook = useWorkflowRunSessions,
}: TaskLogPanelProps) {
  const { projectId } = useProject()
  const queryClient = useQueryClient()
  const { data, isLoading, isError } = taskLogHook({
    issueNumber,
    taskId,
    projectId,
    workflowRunId: workflowRunId ?? null,
  })
  const scrollRef = useRef<HTMLDivElement | null>(null)
  const subscribedRef = useRef(false)
  const subscribedConnectionRef = useRef<HubConnection | null>(null)
  const subscribedReconnectVersionRef = useRef<number | null>(null)
  const terminalNow = isTerminalStatus(taskStatus)

  const [searchQuery, setSearchQuery] = useState('')
  const [disabledSources, setDisabledSources] = useState<Set<string>>(new Set())
  const [userPausedAutoFollow, setUserPausedAutoFollow] = useState(false)

  const onTaskLogDelta = (envelope: unknown) => {
    if (!isTaskLogEnvelope(envelope)) return
    const taskScope = envelope.taskId
    if (taskScope == null) return
    if (taskScope !== taskId) return
    if (envelope.ownerKind !== 'workflow') return
    if (envelope.ownerId !== workflowRunId) return
    if (!projectId) return
    const queryKey = issueWorkflowTaskLogQueryOptions(
      projectId,
      issueNumber,
      taskId,
      { limit: TASK_LOG_RETAINED_LIMIT },
      true,
      workflowRunId,
    ).queryKey
    queryClient.setQueryData<TaskLogPage | undefined>(queryKey, (current) => mergeTaskLogDelta(current, envelope))
  }

  const { connection, reconnectVersion } = useEventsConnection(projectId, () => {}, undefined, onTaskLogDelta, {
    applyDefaultSubscriptions: false,
  })

  const lines = data?.lines ?? []
  const truncated = data?.truncated ?? false

  const isAgentTask = isInlineAgentTask({ origin: origin ?? null, sessionName: sessionName ?? null, classification: classification ?? null })
  const trimmedSessionName = typeof sessionName === 'string' ? sessionName.trim() : ''
  const { sessions, isLoading: sessionsLoading } = workflowSessionsHook(isAgentTask && trimmedSessionName.length > 0 ? workflowRunId ?? null : null)
  const resolvedSession = useMemo(() => {
    if (!isAgentTask || trimmedSessionName.length === 0) return null
    const match = sessions.find((s) => s.sessionName === trimmedSessionName)
    return match ?? null
  }, [isAgentTask, trimmedSessionName, sessions])

  const milestones: TaskLogMilestone[] = useMemo(
    () => (isAgentTask && trimmedSessionName.length > 0 ? deriveMilestones(resolvedSession) : []),
    [isAgentTask, trimmedSessionName, resolvedSession],
  )
  const isSessionSummaryLoading = isAgentTask && trimmedSessionName.length > 0 && sessionsLoading

  const sources = useMemo(
    () => Array.from(new Set(lines.map((line) => line.source))).sort(),
    [lines],
  )

  const filteredRows: TimelineRow[] = useMemo(() => {
    const query = searchQuery.trim().toLowerCase()
    const opsRows: TaskLogLine[] = []
    const milestoneRows: TaskLogMilestone[] = []
    for (const line of lines) {
      if (disabledSources.has(line.source)) continue
      if (query) {
        const haystack = `${line.text} ${line.source}`.toLowerCase()
        if (!haystack.includes(query)) continue
      }
      opsRows.push(line)
    }
    for (const milestone of milestones) {
      if (query) {
        const haystack = `${milestone.label} ${milestone.detail}`.toLowerCase()
        if (!haystack.includes(query)) continue
      }
      milestoneRows.push(milestone)
    }
    return mergeTimelineRows(opsRows, milestoneRows)
  }, [lines, disabledSources, milestones, searchQuery])

  const visibleLines = filteredRows.length

  const handleSearchChange = useCallback((event: React.ChangeEvent<HTMLInputElement>) => {
    setSearchQuery(event.target.value)
  }, [])

  const toggleSource = useCallback((source: string) => {
    setDisabledSources((prev) => {
      const next = new Set(prev)
      if (next.has(source)) {
        next.delete(source)
      } else {
        next.add(source)
      }
      return next
    })
  }, [])

  const handleDownload = useCallback(() => {
    if (filteredRows.length === 0) return
    const text = filteredRows
      .map((row) => (isTaskLogMilestone(row) ? serializeMilestoneForExport(row) : row.text))
      .join('\n')
    const blob = new Blob([text], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = buildExportFilename(taskId)
    document.body.appendChild(anchor)
    anchor.click()
    document.body.removeChild(anchor)
    URL.revokeObjectURL(url)
  }, [filteredRows, taskId])

  useEffect(() => {
    const node = scrollRef.current
    if (!node) return
    if (userPausedAutoFollow) return
    node.scrollTop = node.scrollHeight
  }, [visibleLines, userPausedAutoFollow])

  useEffect(() => {
    const node = scrollRef.current
    if (!node) return
    const handleScroll = () => {
      const distFromBottom = node.scrollHeight - node.scrollTop - node.clientHeight
      if (distFromBottom > 10) {
        setUserPausedAutoFollow(true)
      } else {
        setUserPausedAutoFollow(false)
      }
    }
    node.addEventListener('scroll', handleScroll, { passive: true })
    return () => {
      node.removeEventListener('scroll', handleScroll)
    }
  }, [])

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

  const renderScrollBody = () => {
    if (isLoading || (isSessionSummaryLoading && lines.length === 0 && milestones.length === 0)) {
      return <div className="text-slate-400">Loading execution log…</div>
    }
    if (isError) {
      return <div className="text-slate-400">Execution log unavailable</div>
    }
    if (lines.length === 0 && milestones.length === 0) {
      return (
        <div className="text-slate-400" data-testid="task-log-empty">
          No execution log captured for this task.
        </div>
      )
    }
    const trimmedQuery = searchQuery.trim()
    if (filteredRows.length === 0) {
      if (trimmedQuery) {
        return (
          <div className="text-slate-400" data-testid="task-log-no-search-match">
            No lines match &lsquo;{trimmedQuery}&rsquo;
          </div>
        )
      }
      return (
        <div className="text-slate-400" data-testid="task-log-no-source-match">
          No lines match the active source filters
        </div>
      )
    }
    return (
      <ol className="space-y-0.5" data-testid="task-log-lines">
        {filteredRows.map((row, index) =>
          isTaskLogMilestone(row) ? (
            <li
              key={`milestone-${row.kind}-${row.timestamp}-${index}`}
              data-testid={`task-log-milestone-${row.kind}`}
              className="flex gap-2 whitespace-pre-wrap break-words rounded border border-violet-400/40 bg-violet-400/10 px-1.5"
            >
              <span className="text-slate-500 flex-shrink-0">{formatTimestamp(row.timestamp)}</span>
              <DiamondIcon
                className="h-3 w-3 flex-shrink-0 text-violet-300"
                aria-label="Session event"
                data-testid="task-log-milestone-marker"
                role="img"
              />
              <span className="text-violet-200 flex-shrink-0">[session]</span>
              <span className="flex-1 min-w-0">
                <span className="font-semibold">{row.label}:</span>{' '}
                <span className={row.failed ? 'text-red-300' : 'text-slate-100'}>{row.detail}</span>
              </span>
            </li>
          ) : (
            <li key={row.seq} className="flex gap-2 whitespace-pre-wrap break-words">
              <span className="text-slate-500 flex-shrink-0">{formatTimestamp(row.timestamp)}</span>
              <span className="text-sky-300 flex-shrink-0">[{row.source}]</span>
              <span className="flex-1 min-w-0">{row.text}</span>
            </li>
          ),
        )}
      </ol>
    )
  }

  return (
    <div className="rounded border border-slate-200 bg-white px-2 py-1.5 space-y-1" data-testid="task-log-panel">
      <div className="flex flex-wrap items-center gap-2 text-[10px] uppercase tracking-wide text-slate-500">
        <span className="shrink-0">Execution log</span>
        {truncated && (
          <span
            className="rounded bg-amber-100 text-amber-800 px-1.5 py-0.5 font-mono normal-case tracking-normal"
            data-testid="task-log-truncation-indicator"
          >
            Earlier lines truncated — showing retained tail
          </span>
        )}
        <div className="ml-auto flex min-w-0 flex-1 flex-wrap items-center justify-end gap-2 normal-case tracking-normal sm:flex-initial">
          <div className="relative min-w-0 flex-1 basis-44 sm:flex-initial sm:basis-auto">
            <svg
              className="absolute left-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-400"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
            <input
              type="text"
              value={searchQuery}
              onChange={handleSearchChange}
              placeholder="Search log lines…"
              aria-label="Search log lines"
              data-testid="task-log-search-input"
              className="w-full rounded-md border border-slate-300 bg-white pl-7 pr-3 py-1 text-[11px] text-slate-900 placeholder-slate-400 focus:border-sky-500 focus:ring-1 focus:ring-sky-500 focus:outline-none min-h-[28px] sm:w-48"
            />
          </div>
          <button
            type="button"
            onClick={handleDownload}
            disabled={visibleLines === 0}
            aria-label="Download execution log"
            data-testid="task-log-download-button"
            className="inline-flex items-center gap-1.5 rounded-md border border-slate-300 bg-white px-2.5 py-1 text-[11px] font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-40 disabled:cursor-not-allowed transition-colors min-h-[28px]"
          >
            <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <path d="M10.75 2.75a.75.75 0 00-1.5 0v8.614L6.295 8.235a.75.75 0 10-1.09 1.03l4.25 4.5a.75.75 0 001.09 0l4.25-4.5a.75.75 0 00-1.09-1.03l-2.955 3.129V2.75z" />
              <path d="M3.5 12.75a.75.75 0 00-1.5 0v2.5A2.75 2.75 0 004.75 18h10.5A2.75 2.75 0 0018 15.25v-2.5a.75.75 0 00-1.5 0v2.5c0 .69-.56 1.25-1.25 1.25H4.75c-.69 0-1.25-.56-1.25-1.25v-2.5z" />
            </svg>
            <span>Download</span>
          </button>
        </div>
      </div>
      {sources.length > 0 && (
        <div className="flex items-center gap-1.5 flex-wrap" data-testid="task-log-source-chips">
          {sources.map((source) => {
            const disabled = disabledSources.has(source)
            return (
              <button
                key={source}
                type="button"
                onClick={() => toggleSource(source)}
                aria-pressed={!disabled}
                data-testid={`task-log-source-chip-${source}`}
                className={
                  disabled
                    ? 'inline-flex items-center rounded-full border border-slate-200 bg-white px-2.5 py-0.5 text-[10px] font-semibold text-slate-400 line-through transition-colors'
                    : 'inline-flex items-center rounded-full border border-slate-300 bg-slate-100 px-2.5 py-0.5 text-[10px] font-semibold text-slate-700 hover:bg-slate-200 transition-colors'
                }
              >
                {source}
              </button>
            )
          })}
        </div>
      )}
      <div
        ref={scrollRef}
        className="max-h-64 overflow-y-auto rounded bg-slate-900 text-slate-100 font-mono text-[11px] leading-snug p-2"
        data-testid="task-log-scroll"
      >
        {renderScrollBody()}
      </div>
    </div>
  )
}
