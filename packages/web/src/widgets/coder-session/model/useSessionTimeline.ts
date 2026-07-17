import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getAgentStatus } from '../../../entities/agent'
import { onAgentEvent } from '../../../entities/agent'
import { useProject } from '../../../entities/project'
import type {
  ToolCallEntry,
  CoderSessionItem,
} from '../../../entities/coder-session'
import type { AgentDetailEventMap } from '../../../entities/agent'
import {
  BASE_PLAN_STEPS,
  coderRecoveryStatusReducer,
  compactionEventReducer,
  contextCompactedReducer,
  contextHealthUpdateReducer,
  contextHealthUpdatedReducer,
  deriveToolCallTitle,
  planRoundCompleteReducer,
  planRoundStartReducer,
  sessionLivenessReducer,
  usageUpdatedReducer,
  type ContextHealthState,
  type PlanProgress,
  type RecoveryStatus,
  type Round,
  type SessionTimelineEnv,
  type SessionTimelineState,
} from './session-timeline-reducer'

export * from './session-timeline-reducer'

const FLUSH_INTERVAL = 100

function toToolCallEntryState(state: AgentDetailEventMap['tool_call.started']['state']): ToolCallEntry['state'] {
  if (state === 'timeout') return 'failed'
  return state
}

export function useSessionTimeline(issueNumber: number, session?: CoderSessionItem) {
  const { projectId } = useProject()
  const sessionRef = useRef(session)
  sessionRef.current = session

  const { data: agentStatus } = useQuery({
    queryKey: ['agent-status'],
    queryFn: () => getAgentStatus(),
    refetchInterval: 5000,
  })

  const [rounds, setRounds] = useState<Round[]>([])
  const [isStreaming, setIsStreaming] = useState(false)
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

  const roundsRef = useRef<Round[]>(rounds)
  const planProgressRef = useRef<PlanProgress | null>(planProgress)
  const recoveryStatusRef = useRef<RecoveryStatus | null>(recoveryStatus)
  const contextHealthRef = useRef<ContextHealthState | null>(contextHealth)
  roundsRef.current = rounds
  planProgressRef.current = planProgress
  recoveryStatusRef.current = recoveryStatus
  contextHealthRef.current = contextHealth

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
    const unsubs: Array<() => void> = []
    const isCurrentIssueEvent = (detail: { issueNumber: number; projectId: string }) =>
      detail.projectId === projectId && detail.issueNumber === issueNumber
    const isCurrentLogicalSessionEvent = (detail: { sessionId?: string | null; runtimeSessionId?: string | null }) => {
      if (!mountedRef.current) return false
      const s = sessionRef.current
      if (!s) return true
      return detail.sessionId === s.id
        && (detail.runtimeSessionId == null || detail.runtimeSessionId === s.runtimeSessionId)
    }
    const isCurrentRuntimeSessionEvent = (detail: { runtimeSessionId?: string | null }) => {
      if (!mountedRef.current) return false
      const s = sessionRef.current
      if (!s) return true
      return detail.runtimeSessionId != null && detail.runtimeSessionId === s.runtimeSessionId
    }

    const makeEnv = (): SessionTimelineEnv => ({
      now: Date.now(),
      isoNow: new Date().toISOString(),
      randomId: () => Math.random().toString(36).slice(2, 8),
    })

    const dispatch = <D>(
      reducer: (prev: SessionTimelineState, detail: D, env: SessionTimelineEnv) => SessionTimelineState,
      detail: D,
    ) => {
      const env = makeEnv()
      const snapshot: SessionTimelineState = {
        rounds: roundsRef.current,
        planProgress: planProgressRef.current,
        recoveryStatus: recoveryStatusRef.current,
        contextHealth: contextHealthRef.current,
      }
      const next = reducer(snapshot, detail, env)
      if (next.rounds !== snapshot.rounds) setRounds(next.rounds)
      if (next.planProgress !== snapshot.planProgress) setPlanProgress(next.planProgress)
      if (next.recoveryStatus !== snapshot.recoveryStatus) setRecoveryStatus(next.recoveryStatus)
      if (next.contextHealth !== snapshot.contextHealth) setContextHealth(next.contextHealth)
    }

    unsubs.push(
      onAgentEvent('plan_round_start', (detail) => {
        if (!isCurrentIssueEvent(detail) || !mountedRef.current) return
        if (!isCurrentLogicalSessionEvent(detail)) return
        dispatch(planRoundStartReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('plan_session_update', (detail) => {
        if (!isCurrentIssueEvent(detail) || !mountedRef.current) return
        if (!isCurrentRuntimeSessionEvent(detail)) return
        planBufferRef.current.push(detail)
        scheduleFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('plan_round_complete', (detail) => {
        if (!isCurrentIssueEvent(detail) || !mountedRef.current) return
        dispatch(planRoundCompleteReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('message.delta', (detail) => {
        if (!isCurrentRuntimeSessionEvent(detail)) return
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
        if (!isCurrentRuntimeSessionEvent(detail)) return
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
      if (!isCurrentRuntimeSessionEvent(detail)) return
      const map = liveToolCallMapRef.current
      const existing = map.get(detail.toolCallId)

      if (detail.state === 'started') {
        const entry: ToolCallEntry = {
          executionId: detail.sessionId ?? detail.runtimeSessionId ?? '',
          toolName: detail.toolName,
          state: 'started',
          timestamp: Date.now(),
          runtimeSessionId: detail.runtimeSessionId,
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
        if (!isCurrentIssueEvent(detail) || !mountedRef.current) return
        if (!isCurrentRuntimeSessionEvent(detail)) return
        dispatch(coderRecoveryStatusReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('session.liveness', (detail) => {
        if (!isCurrentRuntimeSessionEvent(detail)) return
        dispatch(sessionLivenessReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('usage.updated', (detail) => {
        if (!isCurrentRuntimeSessionEvent(detail)) return
        dispatch(usageUpdatedReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('context_health_update', (detail) => {
        if (!isCurrentRuntimeSessionEvent(detail)) return
        dispatch(contextHealthUpdateReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('compaction_event', (detail) => {
        if (!isCurrentRuntimeSessionEvent(detail)) return
        dispatch(compactionEventReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('com.mohist.agent-session.context-compacted', (detail) => {
        if (!isCurrentIssueEvent(detail) || !isCurrentSessionEvent(detail) || !mountedRef.current) return
        dispatch(contextCompactedReducer, detail)
      }),
    )

    unsubs.push(
      onAgentEvent('com.mohist.agent-session.context-health-updated', (detail) => {
        if (!isCurrentIssueEvent(detail) || !isCurrentSessionEvent(detail) || !mountedRef.current) return
        dispatch(contextHealthUpdatedReducer, detail)
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
  }, [issueNumber, projectId, scheduleFlush])

  return {
    rounds,
    isLoading: false,
    isStreaming,
    recoveryStatus,
    planProgress,
    contextHealth,
  }
}
