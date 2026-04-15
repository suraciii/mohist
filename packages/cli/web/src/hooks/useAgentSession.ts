import { useEffect, useRef, useState, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api'
import { onAgentEvent } from '../lib/agent-events'
import type {
  AgentSessionMessageItem,
  CoderSessionItem,
  ToolCallEntry,
  CoderTextBuffer,
} from '../lib/types'

const FLUSH_INTERVAL = 100

export interface AgentSessionData {
  messages: AgentSessionMessageItem[]
  toolCalls: ToolCallEntry[]
  coderSessions: CoderSessionItem[]
  coderTexts: CoderTextBuffer[]
  isStreaming: boolean
  agentText: string
}

export function useAgentSession(issueNumber: number) {
  const { data: historicalMessages = [], isLoading: loadingMessages } = useQuery({
    queryKey: ['agent-session', issueNumber],
    queryFn: () => api.getAgentSession(issueNumber),
    enabled: issueNumber > 0,
  })

  const { data: historicalCoderSessions = [], isLoading: loadingCoderSessions } = useQuery({
    queryKey: ['coder-sessions', issueNumber],
    queryFn: () => api.getCoderSessions(issueNumber),
    enabled: issueNumber > 0,
  })

  const { data: agentStatus } = useQuery({
    queryKey: ['agent-status'],
    queryFn: () => api.getAgentStatus(),
    refetchInterval: 5000,
  })

  const [toolCalls, setToolCalls] = useState<ToolCallEntry[]>([])
  const [coderSessions, setCoderSessions] = useState<CoderSessionItem[]>([])
  const [coderTexts, setCoderTexts] = useState<CoderTextBuffer[]>([])
  const [isStreaming, setIsStreaming] = useState(false)
  const [agentText, setAgentText] = useState('')

  const textBufferRef = useRef('')
  const rafRef = useRef<number | null>(null)
  const timeoutRef = useRef<number | null>(null)
  const lastFlushRef = useRef(0)
  const liveToolCallsRef = useRef<Map<string, ToolCallEntry>>(new Map())
  const seenExecutionIdRef = useRef<Set<string>>(new Set())
  const historyLoadedRef = useRef(false)
  const mountedRef = useRef(true)
  const lastAgentRunningRef = useRef(false)

  const flushTextBuffer = useCallback(() => {
    if (!mountedRef.current) return
    const text = textBufferRef.current
    setAgentText(text)
    lastFlushRef.current = Date.now()
    rafRef.current = null
  }, [])

  const scheduleFlush = useCallback(() => {
    if (!mountedRef.current) return
    if (rafRef.current !== null || timeoutRef.current !== null) return
    const now = Date.now()
    const elapsed = now - lastFlushRef.current
    if (elapsed >= FLUSH_INTERVAL) {
      rafRef.current = requestAnimationFrame(flushTextBuffer)
    } else {
      timeoutRef.current = window.setTimeout(() => {
        timeoutRef.current = null
        if (mountedRef.current) {
          rafRef.current = requestAnimationFrame(flushTextBuffer)
        }
      }, FLUSH_INTERVAL - elapsed)
    }
  }, [flushTextBuffer])

  // Load historical data
  useEffect(() => {
    if (loadingMessages || loadingCoderSessions) return
    if (historyLoadedRef.current) return
    historyLoadedRef.current = true

    const callsFromHistory: ToolCallEntry[] = []
    let historicalText = ''

    for (const msg of historicalMessages) {
      if (msg.role === 'assistant' && msg.content) {
        historicalText += msg.content
      }
      if (msg.toolCalls) {
        try {
          const parsed = JSON.parse(msg.toolCalls) as Array<{
            toolCallId?: string
            toolName?: string
            args?: string
          }>
          for (const tc of parsed) {
            const execId = tc.toolCallId ?? `hist-${msg.stepIndex}-${tc.toolName}`
            if (seenExecutionIdRef.current.has(execId)) continue
            seenExecutionIdRef.current.add(execId)
            callsFromHistory.push({
              executionId: execId,
              toolName: tc.toolName ?? 'unknown',
              state: 'completed',
              args: tc.args,
              stepIndex: msg.stepIndex,
              timestamp: new Date(msg.createdAt).getTime(),
            })
          }
        } catch {
          // ignore malformed JSON
        }
      }
      if (msg.toolCallId && msg.toolName) {
        seenExecutionIdRef.current.add(msg.toolCallId)
      }
    }

    textBufferRef.current = historicalText
    setAgentText(historicalText)
    setToolCalls(callsFromHistory)
    setCoderSessions(historicalCoderSessions)
  }, [historicalMessages, historicalCoderSessions, loadingMessages, loadingCoderSessions])

  // Reset buffers when a new agent run starts on this issue
  useEffect(() => {
    const isRunningOnThis = !!(agentStatus?.running && agentStatus?.issueId === String(issueNumber))
    const wasRunningOnThis = lastAgentRunningRef.current
    if (isRunningOnThis && !wasRunningOnThis) {
      // New run started - reset live state
      textBufferRef.current = ''
      setAgentText('')
      setIsStreaming(true)
      liveToolCallsRef.current = new Map()
      seenExecutionIdRef.current = new Set()
      setCoderTexts([])
      // Keep historical tool calls since they represent previous runs;
      // new live events will append.
    }
    if (!agentStatus?.running) {
      setIsStreaming(false)
    }
    lastAgentRunningRef.current = isRunningOnThis
  }, [agentStatus, issueNumber])

  // Subscribe to live SSE events
  useEffect(() => {
    mountedRef.current = true
    const issueId = String(issueNumber)

    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('agent_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        textBufferRef.current += detail.text
        setIsStreaming(true)
        scheduleFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('main_tool_call', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const existing = liveToolCallsRef.current.get(detail.executionId)
        if (detail.state === 'started') {
          if (seenExecutionIdRef.current.has(detail.executionId)) return
          const entry: ToolCallEntry = {
            executionId: detail.executionId,
            toolName: detail.toolName,
            state: 'started',
            stepIndex: detail.stepIndex,
            timestamp: Date.now(),
          }
          liveToolCallsRef.current.set(detail.executionId, entry)
          seenExecutionIdRef.current.add(detail.executionId)
          setToolCalls((prev) => [...prev, entry])
        } else if (existing) {
          const updated: ToolCallEntry = {
            ...existing,
            state: detail.state,
            args: detail.args ?? existing.args,
            result: detail.result,
            error: detail.error,
            duration: detail.duration ?? (Date.now() - existing.timestamp),
          }
          liveToolCallsRef.current.set(detail.executionId, updated)
          setToolCalls((prev) =>
            prev.map((tc) =>
              tc.executionId === detail.executionId ? updated : tc,
            ),
          )
        }
      }),
    )

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        setCoderTexts((prev) => {
          const idx = prev.findIndex(
            (c) => c.executionId === detail.executionId,
          )
          if (idx >= 0) {
            const updated = [...prev]
            updated[idx] = { ...updated[idx], text: updated[idx].text + detail.text }
            return updated
          }
          return [
            ...prev,
            {
              executionId: detail.executionId,
              acpSessionId: detail.acpSessionId,
              text: detail.text,
            },
          ]
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        const existing = liveToolCallsRef.current.get(detail.toolCallId)
        if (detail.state === 'started') {
          if (seenExecutionIdRef.current.has(detail.toolCallId)) return
          const entry: ToolCallEntry = {
            executionId: detail.executionId,
            toolName: detail.toolName,
            state: 'started',
            timestamp: Date.now(),
            acpSessionId: detail.acpSessionId,
            toolCallId: detail.toolCallId,
          }
          liveToolCallsRef.current.set(detail.toolCallId, entry)
          seenExecutionIdRef.current.add(detail.toolCallId)
          setToolCalls((prev) => [...prev, entry])
        } else if (existing) {
          const updated: ToolCallEntry = {
            ...existing,
            state: detail.state,
          }
          liveToolCallsRef.current.set(detail.toolCallId, updated)
          setToolCalls((prev) =>
            prev.map((tc) =>
              tc.toolCallId === detail.toolCallId ? updated : tc,
            ),
          )
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
  }, [scheduleFlush, flushTextBuffer, issueNumber])

  return {
    messages: historicalMessages,
    toolCalls,
    coderSessions,
    coderTexts,
    isStreaming,
    agentText,
    isLoading: loadingMessages || loadingCoderSessions,
  }
}
