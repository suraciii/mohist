import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api'
import { onAgentEvent } from '../lib/agent-events'
import type {
  ToolCallEntry,
  WorkflowLogItem,
  AgentDetailEventMap,
} from '../lib/types'

const FLUSH_INTERVAL = 100

export interface Round {
  roundIndex: number
  label: string
  startedAt: string
  completedAt: string | null
  userText: string
  agentText: string
  toolCalls: ToolCallEntry[]
}

function reconstructRoundsFromLogs(logs: WorkflowLogItem[]): Round[] {
  if (logs.length === 0) return []

  const rounds: Round[] = []
  let currentRound: Round | null = null
  const toolCallMap = new Map<string, ToolCallEntry>()

  for (const log of logs) {
    if (log.eventType === 'user_message_chunk') {
      const userData = log.data as { text?: string }
      if (currentRound) {
        currentRound.completedAt = log.createdAt
        currentRound.toolCalls = Array.from(toolCallMap.values())
      }
      toolCallMap.clear()
      currentRound = {
        roundIndex: rounds.length,
        label: `Round ${rounds.length + 1}`,
        startedAt: log.createdAt,
        completedAt: null,
        userText: userData?.text ?? '',
        agentText: '',
        toolCalls: [],
      }
      rounds.push(currentRound)
      continue
    }

    if (!currentRound) {
      currentRound = {
        roundIndex: 0,
        label: 'Round 1',
        startedAt: log.createdAt,
        completedAt: null,
        userText: '',
        agentText: '',
        toolCalls: [],
      }
      rounds.push(currentRound)
    }

    if (log.eventType === 'plan_session_update') {
      const data = log.data as AgentDetailEventMap['plan_session_update']
      if (data?.sessionUpdate === 'agent_message_chunk') {
        const textData = data.data as { text?: string }
        if (textData?.text) {
          currentRound.agentText += textData.text
        }
      }
    }

    if (log.eventType === 'coder_text_chunk') {
      const data = log.data as AgentDetailEventMap['coder_text_chunk']
      if (data?.text) {
        currentRound.agentText += data.text
      }
    }

    if (log.eventType === 'coder_tool_call') {
      const data = log.data as AgentDetailEventMap['coder_tool_call']
      mergeToolCall(toolCallMap, data)
    }

    if (log.eventType === 'plan_round_start') {
      const data = log.data as AgentDetailEventMap['plan_round_start']
      if (data?.roundLabel && currentRound.roundIndex === rounds.length - 1) {
        currentRound.label = data.roundLabel
      }
    }
  }

  if (currentRound) {
    currentRound.toolCalls = Array.from(toolCallMap.values())
  }

  return rounds
}

function mergeToolCall(
  map: Map<string, ToolCallEntry>,
  data: AgentDetailEventMap['coder_tool_call'],
) {
  const existing = map.get(data.toolCallId)
  if (data.state === 'started') {
    map.set(data.toolCallId, {
      executionId: data.executionId,
      toolName: data.toolName,
      state: 'started',
      timestamp: Date.now(),
      acpSessionId: data.acpSessionId,
      toolCallId: data.toolCallId,
      title: data.title,
      rawInput: typeof data.rawInput === 'string' ? data.rawInput : JSON.stringify(data.rawInput ?? ''),
    })
  } else if (existing) {
    map.set(data.toolCallId, {
      ...existing,
      state: data.state,
      rawOutput: typeof data.rawOutput === 'string' ? data.rawOutput : JSON.stringify(data.rawOutput ?? ''),
    })
  }
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
  }
}
