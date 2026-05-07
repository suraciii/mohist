import { useEffect, useRef, useState, useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../lib/agent-events'
import type { SessionTurn, TextPart, ToolPart, ErrorPart } from '../lib/types'

interface UseSessionTranscriptOptions {
  issueNumber: number
  sessionId: string
  acpSessionId: string
  initialTurns: SessionTurn[]
  isRunning: boolean
}

interface LiveToolCall {
  toolCallId: string
  toolName: string
  status: 'started' | 'completed' | 'failed'
  title?: string
  target?: string
  input?: string
  output?: string
  error?: string
  startedAt: string
  completedAt?: string | null
}

export interface UseSessionTranscriptResult {
  turns: SessionTurn[]
  isNearBottom: boolean
  scrollToBottom: () => void
  newContentAvailable: boolean
  acknowledgeNewContent: () => void
}

function generateId(): string {
  return Math.random().toString(36).slice(2, 11)
}

function createTextPart(text: string, startedAt: string): TextPart {
  return { id: generateId(), type: 'text', text, startedAt, completedAt: null }
}

function createErrorPart(message: string, kind: ErrorPart['kind'], at: string): ErrorPart {
  return { id: generateId(), type: 'error', message, kind, at }
}

function createToolPart(tool: LiveToolCall): ToolPart {
  return {
    id: generateId(),
    type: 'tool',
    tool: {
      toolCallId: tool.toolCallId,
      toolName: tool.toolName,
      status: tool.status,
      title: tool.title,
      target: tool.target,
      input: tool.input,
      output: tool.output,
      error: tool.error,
      startedAt: tool.startedAt,
      completedAt: tool.completedAt,
    },
  }
}

function appendTextToTurn(turn: SessionTurn, text: string): SessionTurn {
  const now = new Date().toISOString()
  const existingTextIndex = turn.assistant.findIndex((p): p is TextPart => p.type === 'text' && p.completedAt === null)

  if (existingTextIndex >= 0) {
    const existing = turn.assistant[existingTextIndex] as TextPart
    const updated: TextPart = { ...existing, text: existing.text + text }
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) => (i === existingTextIndex ? updated : p)),
    }
  }

  return {
    ...turn,
    assistant: [...turn.assistant, createTextPart(text, now)],
  }
}

function updateToolInTurn(turn: SessionTurn, toolCallId: string, updates: Partial<LiveToolCall>): SessionTurn {
  const now = new Date().toISOString()
  const existingToolIndex = turn.assistant.findIndex(
    (p): p is ToolPart => p.type === 'tool' && p.tool.toolCallId === toolCallId,
  )

  if (existingToolIndex >= 0) {
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) => {
        if (i !== existingToolIndex) return p
        const toolPart = p as ToolPart
        return {
          ...toolPart,
          tool: {
            ...toolPart.tool,
            ...updates,
            completedAt: updates.status === 'completed' || updates.status === 'failed' ? now : toolPart.tool.completedAt,
          },
        }
      }),
    }
  }

  return {
    ...turn,
    assistant: [
      ...turn.assistant,
      createToolPart({
        toolCallId,
        toolName: updates.toolName ?? 'unknown',
        status: updates.status ?? 'started',
        title: updates.title,
        target: updates.target,
        input: updates.input,
        output: updates.output,
        error: updates.error,
        startedAt: now,
        completedAt: updates.status === 'completed' || updates.status === 'failed' ? now : null,
      }),
    ],
  }
}

export function useSessionTranscript({
  issueNumber,
  sessionId,
  acpSessionId,
  initialTurns,
  isRunning,
}: UseSessionTranscriptOptions): UseSessionTranscriptResult {
  const queryClient = useQueryClient()
  const [turns, setTurns] = useState<SessionTurn[]>(initialTurns)
  const [isNearBottom, setIsNearBottom] = useState(true)
  const [newContentAvailable, setNewContentAvailable] = useState(false)

  const turnsRef = useRef(turns)
  turnsRef.current = turns

  const liveToolCallMapRef = useRef<Map<string, LiveToolCall>>(new Map())
  const mountedRef = useRef(true)

  const issueId = String(issueNumber)

  const scrollToBottom = useCallback(() => {
    setIsNearBottom(true)
    setNewContentAvailable(false)
  }, [])

  const acknowledgeNewContent = useCallback(() => {
    setNewContentAvailable(false)
  }, [])

  useEffect(() => {
    setTurns(initialTurns)
    liveToolCallMapRef.current.clear()
  }, [initialTurns])

  useEffect(() => {
    if (!isRunning) return

    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        setTurns((prev) => {
          if (prev.length === 0) return prev
          const next = [...prev]
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = appendTextToTurn(lastTurn, detail.text)
          return next
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        const now = new Date().toISOString()

        if (detail.state === 'started') {
          liveToolCallMapRef.current.set(detail.toolCallId, {
            toolCallId: detail.toolCallId,
            toolName: detail.toolName,
            status: 'started',
            title: detail.title,
            target: detail.title,
            input: typeof detail.rawInput === 'string' ? detail.rawInput : JSON.stringify(detail.rawInput ?? ''),
            output: '',
            error: '',
            startedAt: now,
            completedAt: null,
          })

          setTurns((prev) => {
            if (prev.length === 0) return prev
            const next = [...prev]
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = updateToolInTurn(lastTurn, detail.toolCallId, {
              toolName: detail.toolName,
              status: 'started',
              title: detail.title,
              target: detail.title,
              input: typeof detail.rawInput === 'string' ? detail.rawInput : JSON.stringify(detail.rawInput ?? ''),
              startedAt: now,
            })
            return next
          })
        } else {
          const existing = liveToolCallMapRef.current.get(detail.toolCallId)
          if (existing) {
            existing.status = detail.state
            existing.output = typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? '')
            existing.completedAt = now
          }

          setTurns((prev) => {
            if (prev.length === 0) return prev
            const next = [...prev]
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = updateToolInTurn(lastTurn, detail.toolCallId, {
              status: detail.state,
              output: typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? ''),
              completedAt: now,
            })
            return next
          })
        }
      }),
    )

    unsubs.push(
      onAgentEvent('coder_recovery_status', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        const now = new Date().toISOString()
        const errorMessages: Record<string, string> = {
          detected: 'Recovery detected',
          recovering: 'Recovery in progress',
          recovered: 'Recovery succeeded',
          failed: 'Recovery failed',
        }

        setTurns((prev) => {
          if (prev.length === 0) return prev
          const next = [...prev]
          const lastTurn = next[next.length - 1]
          const newPart = createErrorPart(
            errorMessages[detail.status] ?? detail.status,
            'recovery',
            now,
          )
          next[next.length - 1] = {
            ...lastTurn,
            assistant: [...lastTurn.assistant, newPart],
          }
          return next
        })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.coderSessionId !== sessionId) return

        const now = new Date().toISOString()

        setTurns((prev) => {
          if (prev.length === 0) return prev
          const next = [...prev]
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = {
            ...lastTurn,
            completedAt: now,
          }
          return next
        })

        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions', sessionId] })
      }),
    )

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
    }
  }, [issueId, sessionId, acpSessionId, issueNumber, isRunning, queryClient])

  useEffect(() => {
    if (!isRunning) return

    let lastTurnsLength = turns.length
    const checkNewContent = () => {
      if (turns.length > lastTurnsLength) {
        lastTurnsLength = turns.length
        if (!isNearBottom) {
          setNewContentAvailable(true)
        }
      }
    }

    const interval = setInterval(checkNewContent, 500)
    return () => clearInterval(interval)
  }, [isRunning, isNearBottom, turns.length])

  return {
    turns,
    isNearBottom,
    scrollToBottom,
    newContentAvailable,
    acknowledgeNewContent,
  }
}