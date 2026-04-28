import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api'
import { onAgentEvent } from '../lib/agent-events'
import type {
  ToolCallEntry,
  WorkflowLogItem,
  AgentDetailEventMap,
  TaskProgressMap,
  LoopProgress,
} from '../lib/types'

const FLUSH_INTERVAL = 100

export interface Round {
  roundIndex: number
  label: string
  startedAt: string
  completedAt: string | null
  userText: string
  agentText: string
  thoughtText: string
  toolCalls: ToolCallEntry[]
}

export function deriveToolCallTitle(toolName: string, title: string | undefined, rawInput: string | undefined): string {
  if (title && title !== toolName) return title
  if (!rawInput) return toolName
  try {
    const parsed = JSON.parse(rawInput)
    if (typeof parsed !== 'object' || parsed === null) return toolName
    const lower = toolName.toLowerCase()
    if (['read', 'read_file', 'write', 'write_file', 'edit'].includes(lower)) {
      const fp = parsed.file_path ?? parsed.filePath ?? parsed.path
      if (typeof fp === 'string' && fp) return fp.split('/').pop() ?? fp
    }
    if (lower === 'bash') {
      const cmd = parsed.command ?? parsed.script
      if (typeof cmd === 'string' && cmd) return cmd.length > 60 ? cmd.slice(0, 57) + '...' : cmd
    }
    if (['glob', 'search_files', 'grep', 'search'].includes(lower)) {
      const pat = parsed.pattern ?? parsed.query ?? parsed.search
      if (typeof pat === 'string' && pat) return pat
    }
    return toolName
  } catch {
    return rawInput || toolName
  }
}

const PLAN_ROUND_LABELS = ['proposal.md', 'specs/', 'design.md', 'tasks.json', 'self-review']

function inferRoundLabel(roundIndex: number, totalRounds: number): string {
  if (roundIndex < PLAN_ROUND_LABELS.length && totalRounds <= PLAN_ROUND_LABELS.length) {
    return PLAN_ROUND_LABELS[roundIndex]
  }
  return `Round ${roundIndex + 1}`
}

export function reconstructRoundsFromLogs(logs: WorkflowLogItem[]): Round[] {
  if (logs.length === 0) return []

  const rounds: Round[] = []
  let currentRound: Round | null = null
  const toolCallMap = new Map<string, ToolCallEntry>()

  for (const log of logs) {
    if (log.eventType === 'user_message_chunk') {
      const d = log.data as { content?: { text?: string } }
      const userText = d?.content?.text ?? (d as Record<string, unknown>)?.text as string ?? ''
      if (currentRound) {
        currentRound.completedAt = log.createdAt
        currentRound.toolCalls = Array.from(toolCallMap.values())
      }
      toolCallMap.clear()
      currentRound = {
        roundIndex: rounds.length,
        label: '',
        startedAt: log.createdAt,
        completedAt: null,
        userText,
        agentText: '',
        thoughtText: '',
        toolCalls: [],
      }
      rounds.push(currentRound)
      continue
    }

    if (!currentRound) {
      currentRound = {
        roundIndex: 0,
        label: '',
        startedAt: log.createdAt,
        completedAt: null,
        userText: '',
        agentText: '',
        thoughtText: '',
        toolCalls: [],
      }
      rounds.push(currentRound)
    }

    if (log.eventType === 'agent_message_chunk') {
      const d = log.data as { content?: { text?: string } }
      const text = d?.content?.text ?? (d as Record<string, unknown>)?.text as string ?? ''
      if (text) {
        currentRound.agentText += text
      }
    }

    if (log.eventType === 'agent_thought_chunk') {
      const d = log.data as { content?: { text?: string } }
      const text = d?.content?.text ?? (d as Record<string, unknown>)?.text as string ?? ''
      if (text) {
        currentRound.thoughtText += text
      }
    }

    if (log.eventType === 'tool_call' || log.eventType === 'tool_call_update') {
      const d = log.data as Record<string, unknown>
      const toolCallId = d.toolCallId as string | undefined
      const status = d.status as string | undefined
      const title = d.title as string | undefined
      const kind = d.kind as string | undefined
      const rawInput = d.rawInput
      const rawOutput = d.rawOutput
      if (!toolCallId) continue

      if (status === 'completed' || status === 'failed') {
        const existing = toolCallMap.get(toolCallId)
        if (existing) {
          existing.state = status === 'completed' ? 'completed' : 'failed'
          if (title !== undefined) existing.title = title
          if (rawInput !== undefined) existing.rawInput = typeof rawInput === 'string' ? rawInput : JSON.stringify(rawInput ?? '')
          if (rawOutput !== undefined) existing.rawOutput = typeof rawOutput === 'string' ? rawOutput : JSON.stringify(rawOutput ?? '')
        }
      } else {
        const toolName = title ?? kind ?? ''
        const rawInputStr = typeof rawInput === 'string' ? rawInput : JSON.stringify(rawInput ?? '')
        toolCallMap.set(toolCallId, {
          executionId: '',
          toolName,
          state: ((status ?? 'pending') === 'pending' || (status ?? 'pending') === 'in_progress') ? 'started' : status as 'completed' | 'failed',
          timestamp: new Date(log.createdAt).getTime(),
          toolCallId,
          title: deriveToolCallTitle(toolName, title, rawInputStr),
          rawInput: rawInputStr,
        })
      }
    }
  }

  if (currentRound) {
    currentRound.toolCalls = Array.from(toolCallMap.values())
  }

  const totalRounds = rounds.length
  for (const round of rounds) {
    if (!round.label) {
      round.label = inferRoundLabel(round.roundIndex, totalRounds)
    }
  }

  return rounds
}

export function useSessionTimeline(issueNumber: number) {
  const { data: logs = [], isLoading: loadingLogs } = useQuery({
    queryKey: ['workflow-logs', issueNumber],
    queryFn: () => api.getWorkflowLogs(issueNumber),
    enabled: issueNumber > 0,
  })

  const { data: agentStatus } = useQuery({
    queryKey: ['agent-status'],
    queryFn: () => api.getAgentStatus(),
    refetchInterval: 5000,
  })

  const [rounds, setRounds] = useState<Round[]>([])
  const [isStreaming, setIsStreaming] = useState(false)
  const [taskProgress, setTaskProgress] = useState<TaskProgressMap>(new Map())
  const [loopProgress, setLoopProgress] = useState<LoopProgress | null>(null)

  const planBufferRef = useRef<Array<AgentDetailEventMap['plan_session_update']>>([])
  const rafRef = useRef<number | null>(null)
  const timeoutRef = useRef<number | null>(null)
  const lastFlushRef = useRef(0)
  const mountedRef = useRef(true)
  const lastAgentRunningRef = useRef(false)
  const liveToolCallMapRef = useRef<Map<string, ToolCallEntry>>(new Map())
  const historyLoadedRef = useRef(false)
  const setRoundsRef = useRef(setRounds)
  setRoundsRef.current = setRounds

  const flushPlanBuffer = useCallback(() => {
    if (!mountedRef.current) return
    const batch = planBufferRef.current
    planBufferRef.current = []
    if (batch.length === 0) {
      rafRef.current = null
      return
    }

    setRoundsRef.current((prev) => {
      if (prev.length === 0) return prev
      const next = [...prev]
      const lastRound = { ...next[next.length - 1] }
      let changed = false

      for (const event of batch) {
        if (event.sessionUpdate === 'agent_message_chunk') {
          const textData = event.data as { text?: string }
          if (textData?.text) {
            lastRound.agentText += textData.text
            changed = true
          }
        } else if (event.sessionUpdate === 'agent_thought_chunk') {
          const textData = event.data as { text?: string }
          if (textData?.text) {
            lastRound.thoughtText += textData.text
            changed = true
          }
        } else if (event.sessionUpdate === 'tool_call') {
          const d = event.data as Record<string, unknown>
          const toolCallId = d.toolCallId as string | undefined
          const toolName = (d.title ?? d.kind ?? '') as string
          const rawInput = d.rawInput
          const rawInputStr = typeof rawInput === 'string' ? rawInput : JSON.stringify(rawInput ?? '')
          if (toolCallId) {
            const entry: ToolCallEntry = {
              executionId: '',
              toolName,
              state: 'started',
              timestamp: Date.now(),
              toolCallId,
              title: deriveToolCallTitle(toolName, d.title as string | undefined, rawInputStr),
              rawInput: rawInputStr,
            }
            liveToolCallMapRef.current.set(toolCallId, entry)
            lastRound.toolCalls = [...lastRound.toolCalls, entry]
            changed = true
          }
        } else if (event.sessionUpdate === 'tool_call_update') {
          const d = event.data as Record<string, unknown>
          const toolCallId = d.toolCallId as string | undefined
          const status = d.status as string | undefined
          if (toolCallId) {
            const existing = liveToolCallMapRef.current.get(toolCallId)
            if (existing) {
              if (status === 'completed' || status === 'failed') {
                existing.state = status === 'completed' ? 'completed' : 'failed'
              }
              if (d.title !== undefined) existing.title = d.title as string
              if (d.rawInput !== undefined) existing.rawInput = typeof d.rawInput === 'string' ? d.rawInput : JSON.stringify(d.rawInput ?? '')
              if (d.rawOutput !== undefined) existing.rawOutput = typeof d.rawOutput === 'string' ? d.rawOutput : JSON.stringify(d.rawOutput ?? '')
              lastRound.toolCalls = lastRound.toolCalls.map((tc) =>
                tc.toolCallId === toolCallId ? { ...existing } : tc,
              )
              changed = true
            }
          }
        }
      }

      if (changed) {
        next[next.length - 1] = lastRound
      }
      return next
    })

    lastFlushRef.current = Date.now()
    rafRef.current = null
  }, [])

  const scheduleFlush = useCallback(() => {
    if (!mountedRef.current) return
    if (rafRef.current !== null || timeoutRef.current !== null) return
    const now = Date.now()
    const elapsed = now - lastFlushRef.current
    if (elapsed >= FLUSH_INTERVAL) {
      rafRef.current = requestAnimationFrame(flushPlanBuffer)
    } else {
      timeoutRef.current = window.setTimeout(() => {
        timeoutRef.current = null
        if (mountedRef.current) {
          rafRef.current = requestAnimationFrame(flushPlanBuffer)
        }
      }, FLUSH_INTERVAL - elapsed)
    }
  }, [flushPlanBuffer])

  useEffect(() => {
    if (loadingLogs) return
    if (historyLoadedRef.current) return
    historyLoadedRef.current = true
    const reconstructed = reconstructRoundsFromLogs(logs)
    setRounds(reconstructed)
  }, [logs, loadingLogs])

  useEffect(() => {
    const isRunningOnThis = !!(
      agentStatus?.running &&
      agentStatus.issueNumber === issueNumber
    )
    const wasRunningOnThis = lastAgentRunningRef.current
    if (isRunningOnThis && !wasRunningOnThis) {
      liveToolCallMapRef.current = new Map()
    }
    if (agentStatus?.running === false) {
      setIsStreaming(false)
    } else if (isRunningOnThis) {
      setIsStreaming(true)
    }
    lastAgentRunningRef.current = isRunningOnThis
  }, [agentStatus, issueNumber])

  useEffect(() => {
    mountedRef.current = true
    const issueId = String(issueNumber)
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('plan_round_start', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setRoundsRef.current((prev) => {
          const newRound: Round = {
            roundIndex: prev.length,
            label: detail.roundLabel ?? `Round ${prev.length + 1}`,
            startedAt: new Date().toISOString(),
            completedAt: null,
            userText: '',
            agentText: '',
            thoughtText: '',
            toolCalls: [],
          }
          return [...prev, newRound]
        })
      }),
    )

    unsubs.push(
      onAgentEvent('plan_session_update', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        planBufferRef.current.push(detail)
        scheduleFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setRoundsRef.current((prev) => {
          if (prev.length === 0) return prev
          const next = [...prev]
          const lastRound = { ...next[next.length - 1] }
          lastRound.agentText += detail.text
          next[next.length - 1] = lastRound
          return next
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const map = liveToolCallMapRef.current
        const existing = map.get(detail.toolCallId)

        if (detail.state === 'started') {
          const entry: ToolCallEntry = {
            executionId: detail.executionId,
            toolName: detail.toolName,
            state: 'started',
            timestamp: Date.now(),
            acpSessionId: detail.acpSessionId,
            toolCallId: detail.toolCallId,
            title: detail.title,
            rawInput: typeof detail.rawInput === 'string' ? detail.rawInput : JSON.stringify(detail.rawInput ?? ''),
          }
          map.set(detail.toolCallId, entry)
          setRoundsRef.current((prev) => {
            if (prev.length === 0) return prev
            const next = [...prev]
            const lastRound = { ...next[next.length - 1] }
            lastRound.toolCalls = [...lastRound.toolCalls, entry]
            next[next.length - 1] = lastRound
            return next
          })
        } else if (existing) {
          const updated: ToolCallEntry = {
            ...existing,
            state: detail.state,
            title: detail.title ?? existing.title,
            rawInput: detail.rawInput != null ? (typeof detail.rawInput === 'string' ? detail.rawInput : JSON.stringify(detail.rawInput)) : existing.rawInput,
            rawOutput: typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? ''),
          }
          map.set(detail.toolCallId, updated)
          setRoundsRef.current((prev) => {
            if (prev.length === 0) return prev
            const next = [...prev]
            const lastRound = { ...next[next.length - 1] }
            lastRound.toolCalls = lastRound.toolCalls.map((tc) =>
              tc.toolCallId === detail.toolCallId ? updated : tc,
            )
            next[next.length - 1] = lastRound
            return next
          })
        }
      }),
    )

    unsubs.push(
      onAgentEvent('ralph_task_update', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setTaskProgress((prev) => {
          const next = new Map(prev)
          const existing = next.get(detail.taskId)
          const statusMap: Record<string, 'running' | 'passed' | 'failed' | 'retrying' | 'pending'> = {
            started: 'running',
            completed: 'passed',
            failed: 'failed',
            retrying: 'retrying',
          }
          next.set(detail.taskId, {
            taskId: detail.taskId,
            taskIndex: detail.taskIndex,
            totalTasks: detail.totalTasks,
            status: statusMap[detail.status] ?? 'pending',
            executionId: detail.executionId,
            attempt: detail.attempt ?? existing?.attempt,
            error: detail.error ?? existing?.error,
          })
          return next
        })
      }),
    )

    unsubs.push(
      onAgentEvent('ralph_loop_progress', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setLoopProgress({
          completed: detail.completed,
          failed: detail.failed,
          total: detail.total,
        })
      }),
    )

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
      if (rafRef.current !== null) {
        cancelAnimationFrame(rafRef.current)
        rafRef.current = null
      }
      if (timeoutRef.current !== null) {
        clearTimeout(timeoutRef.current)
        timeoutRef.current = null
      }
    }
  }, [issueNumber, scheduleFlush])

  return {
    rounds,
    isLoading: loadingLogs,
    isStreaming,
    taskProgress,
    loopProgress,
  }
}
