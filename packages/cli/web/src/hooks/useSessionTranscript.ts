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
  transcriptVersion: number
  isNearBottom: boolean
  setIsNearBottom: (nearBottom: boolean) => void
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

function createTemporaryTurn(at: string): SessionTurn {
  return {
    id: `live-${generateId()}`,
    startedAt: at,
    completedAt: null,
    incomplete: true,
    user: {
      role: 'mohist',
      text: 'Prompt is loading for this live session',
      kind: 'legacy-missing',
      sentAt: at,
    },
    assistant: [],
  }
}

function ensureLiveTurn(turns: SessionTurn[], at: string): SessionTurn[] {
  return turns.length > 0 ? [...turns] : [createTemporaryTurn(at)]
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
  const [transcriptVersion, setTranscriptVersion] = useState(0)
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

  const bumpTranscriptVersion = useCallback(() => {
    setTranscriptVersion((version) => version + 1)
  }, [])

  const markNewContent = useCallback(() => {
    bumpTranscriptVersion()
    if (!isNearBottom) {
      setNewContentAvailable(true)
    }
  }, [bumpTranscriptVersion, isNearBottom])

  const acknowledgeNewContent = useCallback(() => {
    setNewContentAvailable(false)
  }, [])

  useEffect(() => {
    setTurns(initialTurns)
    liveToolCallMapRef.current.clear()
    setTranscriptVersion((version) => version + 1)
  }, [initialTurns])

  useEffect(() => {
    if (!isRunning) return

    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, new Date().toISOString())
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = appendTextToTurn(lastTurn, detail.text)
          return next
        })
        markNewContent()
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
            const next = ensureLiveTurn(prev, now)
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
          markNewContent()
        } else {
          const existing = liveToolCallMapRef.current.get(detail.toolCallId)
          if (existing) {
            existing.status = detail.state
            existing.output = typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? '')
            existing.completedAt = now
          }

          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = updateToolInTurn(lastTurn, detail.toolCallId, {
              status: detail.state,
              output: typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? ''),
              completedAt: now,
            })
            return next
          })
          markNewContent()
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
          const next = ensureLiveTurn(prev, now)
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
        markNewContent()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.coderSessionId !== sessionId) return

        const now = new Date().toISOString()

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = {
            ...lastTurn,
            completedAt: now,
          }
          return next
        })
        markNewContent()

        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions', sessionId] })
      }),
    )

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
    }
  }, [issueId, sessionId, acpSessionId, issueNumber, isRunning, queryClient, markNewContent])

  return {
    turns,
    transcriptVersion,
    isNearBottom,
    setIsNearBottom,
    scrollToBottom,
    newContentAvailable,
    acknowledgeNewContent,
  }
}
