import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getAgentStatus } from '../../../entities/agent'
import { getWorkflowLogs } from '../../../entities/coder-session'
import { useProject } from '../../../entities/project'
import { onAgentEvent } from '../../../entities/agent'
import type {
  ToolCallEntry,
  WorkflowLogItem,
  TaskProgressMap,
  LoopProgress,
  CoderSessionItem,
} from '../../../entities/coder-session'
import type { AgentDetailEventMap } from '../../../entities/agent'

const FLUSH_INTERVAL = 100

export interface RecoveryEvent {
  status: 'detected' | 'recovering' | 'recovered' | 'failed'
  attempt: number
  reason?: string
  timestamp: number
}

export interface Round {
  roundIndex: number
  label: string
  startedAt: string
  completedAt: string | null
  userText: string
  agentText: string
  thoughtText: string
  toolCalls: ToolCallEntry[]
  recoveryEvents: RecoveryEvent[]
}

export interface RecoveryStatus {
  status: 'detected' | 'recovering' | 'recovered' | 'failed'
  attempt: number
  reason?: string
}

const BASE_PLAN_STEPS: Array<{ roundType: string; roundLabel: string }> = [
  { roundType: 'proposal', roundLabel: 'Proposal' },
  { roundType: 'specs', roundLabel: 'Specs' },
  { roundType: 'design', roundLabel: 'Design' },
  { roundType: 'tasks', roundLabel: 'Tasks' },
  { roundType: 'self-review', roundLabel: 'Self Review' },
]

export interface PlanStep {
  roundType: string
  roundLabel: string
  roundIndex: number
  status: 'pending' | 'running' | 'completed' | 'failed'
  duration?: number
  verdict?: 'PASS' | 'FAIL'
}

export interface PlanProgress {
  steps: PlanStep[]
  completedCount: number
  totalSteps: number
}

const RECOVERY_LOG_EVENT_MAP: Record<string, RecoveryEvent['status']> = {
  acp_session_hang_detected: 'detected',
  acp_session_recovery_started: 'recovering',
  acp_session_recovery_succeeded: 'recovered',
  acp_session_recovery_failed: 'failed',
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
        recoveryEvents: [],
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
        recoveryEvents: [],
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

    const recoveryStatus = RECOVERY_LOG_EVENT_MAP[log.eventType]
    if (recoveryStatus && currentRound) {
      const d = log.data as Record<string, unknown>
      currentRound.recoveryEvents.push({
        status: recoveryStatus,
        attempt: (d.attempt as number) ?? 1,
        reason: d.reason as string | undefined,
        timestamp: new Date(log.createdAt).getTime(),
      })
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

export function useSessionTimeline(issueNumber: number, session?: CoderSessionItem) {
  const { projectId } = useProject()
  const shouldFetchLogs = !session

  const { data: logs = [], isLoading: loadingLogs } = useQuery({
    queryKey: ['workflow-logs', issueNumber, projectId],
    queryFn: () => getWorkflowLogs(issueNumber, projectId),
    enabled: issueNumber > 0 && shouldFetchLogs && !!projectId,
  })

  const sessionRef = useRef(session)
  sessionRef.current = session

  const { data: agentStatus } = useQuery({
    queryKey: ['agent-status'],
    queryFn: () => getAgentStatus(),
    refetchInterval: 5000,
  })

  const [rounds, setRounds] = useState<Round[]>([])
  const [isStreaming, setIsStreaming] = useState(false)
  const [taskProgress, setTaskProgress] = useState<TaskProgressMap>(new Map())
  const [loopProgress, setLoopProgress] = useState<LoopProgress | null>(null)
  const [recoveryStatus, setRecoveryStatus] = useState<RecoveryStatus | null>(null)
  const [planProgress, setPlanProgress] = useState<PlanProgress | null>(null)

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
    if (session) {
      const reconstructed = reconstructRoundsFromLogs(session.workflowLogs ?? [])
      setRounds(reconstructed)
      return
    }
    if (loadingLogs) return
    if (historyLoadedRef.current) return
    historyLoadedRef.current = true
    const reconstructed = reconstructRoundsFromLogs(logs)
    setRounds(reconstructed)
  }, [session?.id, logs, loadingLogs])

  useEffect(() => {
    const isRunningOnThis = !!(
      agentStatus?.running &&
      agentStatus.issueNumber === issueNumber
    )
    const wasRunningOnThis = lastAgentRunningRef.current
    if (isRunningOnThis && !wasRunningOnThis) {
      liveToolCallMapRef.current = new Map()
      setPlanProgress(null)
    }
    if (agentStatus?.running === false) {
      setIsStreaming(false)
    } else if (isRunningOnThis) {
      setIsStreaming(true)
      if (!session) {
        setPlanProgress((prev) => {
          if (prev) return prev
          const activeAgent = agentStatus?.activeAgents?.find((a) => a.issueNumber === issueNumber)
          const progress = activeAgent?.progress
          if (progress?.stage !== 'plan' || !progress.taskProgress) return prev
          const { completed, total } = progress.taskProgress
          const roundIndex = progress.roundIndex ?? 0
          return {
            steps: BASE_PLAN_STEPS.map((s, i) => ({
              roundType: s.roundType,
              roundLabel: s.roundLabel,
              roundIndex: i,
              status: i < completed ? ('completed' as const) : i === roundIndex ? ('running' as const) : ('pending' as const),
            })),
            completedCount: completed,
            totalSteps: total,
          }
        })
      }
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
        const s = sessionRef.current
        if (s) {
          if (detail.coderSessionId && detail.coderSessionId !== s.id) return
          if (!detail.coderSessionId && detail.acpSessionId && detail.acpSessionId !== s.acpSessionId) return
          if (!detail.coderSessionId && !detail.acpSessionId) return
        }
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
            recoveryEvents: [],
          }
          return [...prev, newRound]
        })
        setPlanProgress((prev) => {
          const steps: PlanStep[] = prev?.steps ? [...prev.steps] : BASE_PLAN_STEPS.map((s, i) => ({
            roundType: s.roundType,
            roundLabel: s.roundLabel,
            roundIndex: i,
            status: 'pending' as const,
          }))
          const idx = steps.findIndex((s) => s.roundType === detail.roundType)
          if (idx >= 0) {
            steps[idx] = { ...steps[idx], status: 'running' }
          } else {
            steps.push({
              roundType: detail.roundType,
              roundLabel: detail.roundLabel ?? detail.roundType,
              roundIndex: detail.roundIndex,
              status: 'running',
            })
          }
          return {
            steps,
            completedCount: prev?.completedCount ?? 0,
            totalSteps: prev?.totalSteps ?? 5,
          }
        })
      }),
    )

    unsubs.push(
      onAgentEvent('plan_session_update', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const s = sessionRef.current
        if (s) {
          if (detail.coderSessionId && detail.coderSessionId !== s.id) return
          if (!detail.coderSessionId && detail.acpSessionId && detail.acpSessionId !== s.acpSessionId) return
          if (!detail.coderSessionId && !detail.acpSessionId) return
        }
        planBufferRef.current.push(detail)
        scheduleFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('plan_round_complete', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setPlanProgress((prev) => {
          const steps: PlanStep[] = prev?.steps ? [...prev.steps] : BASE_PLAN_STEPS.map((s, i) => ({
            roundType: s.roundType,
            roundLabel: s.roundLabel,
            roundIndex: i,
            status: i < detail.roundIndex ? ('completed' as const) : ('pending' as const),
          }))
          const isFailed = detail.verdict === 'FAIL'
          const idx = steps.findIndex((s) => s.roundType === detail.roundType)
          if (idx >= 0) {
            steps[idx] = {
              ...steps[idx],
              status: isFailed ? ('failed' as const) : ('completed' as const),
              duration: detail.duration,
              ...(detail.verdict ? { verdict: detail.verdict as 'PASS' | 'FAIL' } : {}),
            }
          }
          if (detail.roundType === 'self-review' && isFailed) {
            if (!steps.some((s) => s.roundType === 'auto-fix')) {
              steps.push({
                roundType: 'auto-fix',
                roundLabel: 'Auto Fix',
                roundIndex: steps.length,
                status: 'pending',
              })
            }
            if (!steps.some((s) => s.roundType === 're-self-review')) {
              steps.push({
                roundType: 're-self-review',
                roundLabel: 'Re Self Review',
                roundIndex: steps.length,
                status: 'pending',
              })
            }
          }
          const completedCount = steps.filter((s) => s.status === 'completed' || s.status === 'failed').length
          return {
            steps,
            completedCount,
            totalSteps: prev?.totalSteps ?? 5,
          }
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const s = sessionRef.current
        if (s && detail.acpSessionId !== s.acpSessionId) return
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
        const s = sessionRef.current
        if (s && detail.acpSessionId !== s.acpSessionId) return
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
        if (sessionRef.current) return
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
        if (sessionRef.current) return
        setLoopProgress({
          completed: detail.completed,
          failed: detail.failed,
          total: detail.total,
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_recovery_status', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setRecoveryStatus({
          status: detail.status,
          attempt: detail.attempt,
          reason: detail.reason,
        })
        if (detail.status === 'detected' || detail.status === 'recovering') {
          setRoundsRef.current((prev) => {
            if (prev.length === 0) return prev
            const next = [...prev]
            const lastRound = { ...next[next.length - 1] }
            lastRound.recoveryEvents = [...lastRound.recoveryEvents, {
              status: detail.status,
              attempt: detail.attempt,
              reason: detail.reason,
              timestamp: Date.now(),
            }]
            next[next.length - 1] = lastRound
            return next
          })
        }
        if (detail.status === 'recovered' || detail.status === 'failed') {
          setRecoveryStatus(null)
          setRoundsRef.current((prev) => {
            if (prev.length === 0) return prev
            const next = [...prev]
            const lastRound = { ...next[next.length - 1] }
            lastRound.recoveryEvents = [...lastRound.recoveryEvents, {
              status: detail.status,
              attempt: detail.attempt,
              reason: detail.reason,
              timestamp: Date.now(),
            }]
            next[next.length - 1] = lastRound
            return next
          })
        }
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
    recoveryStatus,
    planProgress,
  }
}
