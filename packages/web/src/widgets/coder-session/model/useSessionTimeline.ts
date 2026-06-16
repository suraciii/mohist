import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getAgentStatus } from '../../../entities/agent'
import { onAgentEvent } from '../../../entities/agent'
import type {
  ToolCallEntry,
  TaskProgressMap,
  LoopProgress,
  CoderSessionItem,
} from '../../../entities/coder-session'
import type { AgentDetailEventMap } from '../../../entities/agent'
import {
  viewSessionEvents,
  type SessionEvent,
  type SessionTimelineToolCall,
  type SessionTimelineRecovery,
  type SessionTimelineCompaction,
} from '../../../entities/session/model/view'

const FLUSH_INTERVAL = 100

export interface RecoveryEvent {
  status: 'detected' | 'recovering' | 'recovered' | 'failed'
  attempt: number
  reason?: string
  timestamp: number
}

export type ContextHealthStatus = 'green' | 'yellow' | 'red'

export interface ContextHealthState {
  status: ContextHealthStatus
  contextWindowUsed: number | null
  contextWindowSize: number | null
  contextUsagePercent: number | null
  recordedAt: string | null
}

export interface CompactionEntry {
  id: string
  strategy?: string
  contextWindowUsedBefore?: number | null
  contextWindowUsedAfter?: number | null
  contextWindowSize?: number | null
  summary?: string
  timestamp: number
  recordedAt: string
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
  compactions: CompactionEntry[]
}

export interface RecoveryStatus {
  status: 'detected' | 'recovering' | 'recovered' | 'failed'
  attempt: number
  reason?: string
}

function mapLivenessToRecoveryStatus(status: AgentDetailEventMap['session.liveness']['status']): RecoveryStatus['status'] {
  if (status === 'probing') return 'recovering'
  if (status === 'running') return 'recovered'
  return 'failed'
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

function timelineToolCallToEntry(tool: SessionTimelineToolCall, fallbackAt: string): ToolCallEntry {
  const state: ToolCallEntry['state'] =
    tool.state === 'running' ? 'started' : tool.state
  return {
    executionId: '',
    toolName: tool.toolName,
    state,
    timestamp: new Date(tool.startedAt ?? fallbackAt).getTime(),
    toolCallId: tool.toolCallId,
    title: deriveToolCallTitle(tool.toolName, tool.title, tool.rawInput),
    rawInput: tool.rawInput,
    rawOutput: tool.rawOutput,
  }
}

function toToolCallEntryState(state: AgentDetailEventMap['tool_call.started']['state']): ToolCallEntry['state'] {
  if (state === 'timeout') return 'failed'
  return state
}

function timelineRecoveryToEvent(recovery: SessionTimelineRecovery): RecoveryEvent {
  return {
    status: recovery.status,
    attempt: recovery.attempt ?? 1,
    reason: recovery.reason,
    timestamp: new Date(recovery.at).getTime(),
  }
}

function timelineCompactionToEntry(compaction: SessionTimelineCompaction, fallbackIndex: number): CompactionEntry {
  const id = compaction.id != null ? String(compaction.id) : `compaction-${compaction.at}-${fallbackIndex}`
  return {
    id,
    strategy: compaction.strategy,
    contextWindowUsedBefore: compaction.contextWindowUsedBefore ?? null,
    contextWindowUsedAfter: compaction.contextWindowUsedAfter ?? null,
    contextWindowSize: compaction.contextWindowSize ?? null,
    summary: compaction.summary,
    timestamp: new Date(compaction.at).getTime(),
    recordedAt: compaction.at,
  }
}

export function reconstructRoundsFromEvents(events: SessionEvent[]): Round[] {
  if (events.length === 0) return []
  const view = viewSessionEvents(events, 'timeline')
  const totalRounds = view.rounds.length
  return view.rounds.map((round) => ({
    roundIndex: round.roundIndex,
    label: inferRoundLabel(round.roundIndex, totalRounds),
    startedAt: round.startedAt,
    completedAt: round.completedAt,
    userText: round.userText,
    agentText: round.agentText,
    thoughtText: round.thoughtText,
    toolCalls: round.toolCalls.map((tool) => timelineToolCallToEntry(tool, round.startedAt)),
    recoveryEvents: round.recovery.map(timelineRecoveryToEvent),
    compactions: round.compactions.map((entry, idx) => timelineCompactionToEntry(entry, idx)),
  }))
}

export function useSessionTimeline(issueNumber: number, session?: CoderSessionItem) {
  const sessionRef = useRef(session)
  sessionRef.current = session

  const { data: agentStatus } = useQuery({
    queryKey: ['agent-status'],
    queryFn: () => getAgentStatus(),
    refetchInterval: 5000,
  })

  const [rounds, setRounds] = useState<Round[]>([])
  const [isStreaming, setIsStreaming] = useState(false)
  const [taskProgress] = useState<TaskProgressMap>(new Map())
  const [loopProgress] = useState<LoopProgress | null>(null)
  const [recoveryStatus, setRecoveryStatus] = useState<RecoveryStatus | null>(null)
  const [planProgress, setPlanProgress] = useState<PlanProgress | null>(null)
  const [contextHealth, setContextHealth] = useState<ContextHealthState | null>(null)

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
        if (event.sessionUpdate === 'message.delta') {
          const textData = event.data as { text?: string }
          if (textData?.text) {
            lastRound.agentText += textData.text
            changed = true
          }
        } else if (event.sessionUpdate === 'reasoning.delta') {
          const textData = event.data as { text?: string }
          if (textData?.text) {
            lastRound.thoughtText += textData.text
            changed = true
          }
        } else if (event.sessionUpdate === 'tool_call.started') {
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
        } else if (event.sessionUpdate === 'tool_call.updated' || event.sessionUpdate === 'tool_call.completed') {
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
    if (historyLoadedRef.current) return
    historyLoadedRef.current = true
    setRounds([])
  }, [session?.id])

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
    const isCurrentSessionEvent = (detail: { acpSessionId?: string | null; coderSessionId?: string | null; sessionId?: string | null }) => {
      if (!mountedRef.current) return false
      const s = sessionRef.current
      if (!s) return true
      if (detail.acpSessionId === s.acpSessionId) return true
      return detail.coderSessionId === s.id || detail.sessionId === s.id
    }

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
            compactions: [],
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
      onAgentEvent('message.delta', (detail) => {
        if (!isCurrentSessionEvent(detail)) return
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
      onAgentEvent('reasoning.delta', (detail) => {
        if (!isCurrentSessionEvent(detail)) return
        setRoundsRef.current((prev) => {
          if (prev.length === 0) return prev
          const next = [...prev]
          const lastRound = { ...next[next.length - 1] }
          lastRound.thoughtText += detail.text
          next[next.length - 1] = lastRound
          return next
        })
      }),
    )

    const handleToolEvent = (detail: AgentDetailEventMap['tool_call.started']) => {
      if (!isCurrentSessionEvent(detail)) return
      const map = liveToolCallMapRef.current
      const existing = map.get(detail.toolCallId)

      if (detail.state === 'started') {
        const entry: ToolCallEntry = {
          executionId: detail.coderSessionId ?? detail.acpSessionId ?? '',
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
          state: toToolCallEntryState(detail.state),
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
    }

    unsubs.push(
      onAgentEvent('tool_call.started', handleToolEvent),
    )

    unsubs.push(
      onAgentEvent('tool_call.updated', handleToolEvent),
    )

    unsubs.push(
      onAgentEvent('tool_call.completed', handleToolEvent),
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

    unsubs.push(
      onAgentEvent('session.liveness', (detail) => {
        if (!isCurrentSessionEvent(detail)) return
        const status = mapLivenessToRecoveryStatus(detail.status)
        const attempt = detail.activeProbeVersion ?? detail.satisfiedProbeVersion ?? detail.probeVersion ?? 1
        const reason = detail.failureReason
          ?? (detail.status === 'probing'
            ? `Probe sent; waiting for activity before ${detail.probeDeadlineAt ?? 'deadline unknown'}`
            : detail.lastActivityType)

        setRecoveryStatus({
          status,
          attempt,
          reason,
        })

        setRoundsRef.current((prev) => {
          if (prev.length === 0) return prev
          const next = [...prev]
          const lastRound = { ...next[next.length - 1] }
          lastRound.recoveryEvents = [...lastRound.recoveryEvents, {
            status,
            attempt,
            reason,
            timestamp: Date.now(),
          }]
          next[next.length - 1] = lastRound
          return next
        })

        if (detail.status === 'running' || detail.status === 'failed') {
          setRecoveryStatus(null)
        }
      }),
    )

    const applyContextHealth = (next: ContextHealthState) => {
      setContextHealth((prev) => {
        if (!prev) return next
        if (
          prev.status === next.status
          && prev.contextWindowUsed === next.contextWindowUsed
          && prev.contextWindowSize === next.contextWindowSize
          && prev.contextUsagePercent === next.contextUsagePercent
        ) {
          return prev
        }
        return next
      })
    }

    unsubs.push(
      onAgentEvent('usage.updated', (detail) => {
        if (!isCurrentSessionEvent(detail)) return
        if (detail.contextWindowUsed == null && detail.contextWindowSize == null) return
        const used = detail.contextWindowUsed ?? null
        const size = detail.contextWindowSize ?? null
        const percent = used != null && size != null && size > 0
          ? Math.min(100, Math.round((used / size) * 100))
          : null
        const status: ContextHealthStatus = percent == null
          ? 'green'
          : percent >= 80 ? 'red' : percent >= 60 ? 'yellow' : 'green'
        applyContextHealth({
          status,
          contextWindowUsed: used,
          contextWindowSize: size,
          contextUsagePercent: percent,
          recordedAt: new Date().toISOString(),
        })
      }),
    )

    unsubs.push(
      onAgentEvent('context_health_update', (detail) => {
        if (!isCurrentSessionEvent(detail)) return
        const percent = detail.contextUsagePercent ?? null
        const status: ContextHealthStatus = detail.healthStatus === 'red' || detail.healthStatus === 'yellow' || detail.healthStatus === 'green'
          ? detail.healthStatus
          : (percent == null
              ? 'green'
              : percent >= 80 ? 'red' : percent >= 60 ? 'yellow' : 'green')
        applyContextHealth({
          status,
          contextWindowUsed: detail.contextWindowUsed ?? null,
          contextWindowSize: detail.contextWindowSize ?? null,
          contextUsagePercent: percent,
          recordedAt: detail.recordedAt ?? new Date().toISOString(),
        })
      }),
    )

    unsubs.push(
      onAgentEvent('compaction_event', (detail) => {
        if (!isCurrentSessionEvent(detail)) return
        const recordedAt = detail.recordedAt ?? new Date().toISOString()
        const entry: CompactionEntry = {
          id: `compaction-${recordedAt}-${Math.random().toString(36).slice(2, 8)}`,
          strategy: detail.strategy,
          contextWindowUsedBefore: detail.contextWindowUsedBefore ?? null,
          contextWindowUsedAfter: detail.contextWindowUsedAfter ?? null,
          contextWindowSize: detail.contextWindowSize ?? null,
          summary: detail.summary,
          timestamp: new Date(recordedAt).getTime(),
          recordedAt,
        }
        setRoundsRef.current((prev) => {
          if (prev.length === 0) {
            const placeholder: Round = {
              roundIndex: 0,
              label: 'Compaction',
              startedAt: recordedAt,
              completedAt: recordedAt,
              userText: '',
              agentText: '',
              thoughtText: '',
              toolCalls: [],
              recoveryEvents: [],
              compactions: [entry],
            }
            return [placeholder]
          }
          const next = [...prev]
          const lastRound = { ...next[next.length - 1] }
          lastRound.compactions = [...lastRound.compactions, entry]
          next[next.length - 1] = lastRound
          return next
        })
        const size = detail.contextWindowSize ?? null
        const percent = detail.contextWindowUsedAfter != null && size != null && size > 0
          ? Math.min(100, Math.round((detail.contextWindowUsedAfter / size) * 100))
          : null
        const status: ContextHealthStatus = percent == null
          ? 'green'
          : percent >= 80 ? 'red' : percent >= 60 ? 'yellow' : 'green'
        applyContextHealth({
          status,
          contextWindowUsed: detail.contextWindowUsedAfter ?? null,
          contextWindowSize: size,
          contextUsagePercent: percent,
          recordedAt,
        })
      }),
    )

    unsubs.push(
      onAgentEvent('com.mohist.agent-session.context-compacted', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const recordedAt = detail.recordedAt ?? new Date().toISOString()
        const entry: CompactionEntry = {
          id: `compaction-domain-${recordedAt}-${Math.random().toString(36).slice(2, 8)}`,
          strategy: detail.strategy ?? undefined,
          contextWindowUsedBefore: detail.contextWindowUsedBefore ?? null,
          contextWindowUsedAfter: detail.contextWindowUsedAfter ?? null,
          contextWindowSize: detail.contextWindowSize ?? null,
          summary: detail.summary ?? undefined,
          timestamp: new Date(recordedAt).getTime(),
          recordedAt,
        }
        setRoundsRef.current((prev) => {
          if (prev.length === 0) {
            const placeholder: Round = {
              roundIndex: 0,
              label: 'Compaction',
              startedAt: recordedAt,
              completedAt: recordedAt,
              userText: '',
              agentText: '',
              thoughtText: '',
              toolCalls: [],
              recoveryEvents: [],
              compactions: [entry],
            }
            return [placeholder]
          }
          const next = [...prev]
          const lastRound = { ...next[next.length - 1] }
          lastRound.compactions = [...lastRound.compactions, entry]
          next[next.length - 1] = lastRound
          return next
        })
        const size = detail.contextWindowSize ?? null
        const percent = detail.contextWindowUsedAfter != null && size != null && size > 0
          ? Math.min(100, Math.round((detail.contextWindowUsedAfter / size) * 100))
          : null
        const status: ContextHealthStatus = percent == null
          ? 'green'
          : percent >= 80 ? 'red' : percent >= 60 ? 'yellow' : 'green'
        applyContextHealth({
          status,
          contextWindowUsed: detail.contextWindowUsedAfter ?? null,
          contextWindowSize: size,
          contextUsagePercent: percent,
          recordedAt,
        })
      }),
    )

    unsubs.push(
      onAgentEvent('com.mohist.agent-session.context-health-updated', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const percent = detail.contextUsagePercent ?? null
        const rawStatus = detail.healthStatus
        const status: ContextHealthStatus = rawStatus === 'red' || rawStatus === 'yellow' || rawStatus === 'green'
          ? rawStatus
          : (percent == null
              ? 'green'
              : percent >= 80 ? 'red' : percent >= 60 ? 'yellow' : 'green')
        applyContextHealth({
          status,
          contextWindowUsed: detail.contextWindowUsed ?? null,
          contextWindowSize: detail.contextWindowSize ?? null,
          contextUsagePercent: percent,
          recordedAt: detail.recordedAt ?? new Date().toISOString(),
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
    isLoading: false,
    isStreaming,
    taskProgress,
    loopProgress,
    recoveryStatus,
    planProgress,
    contextHealth,
  }
}
