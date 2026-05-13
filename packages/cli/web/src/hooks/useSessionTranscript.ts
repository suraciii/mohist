import { useEffect, useRef, useState, useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../lib/agent-events'
import type { SessionTurn, TextPart, ToolPart, ErrorPart } from '../lib/types'
import { parseEditInput, parsePatchOperations } from '../lib/transcript-tool-utils'
import {
  normalizeToolName,
  inferDisplayTitle,
  stringifyPayload,
  getCorrelationKey,
} from '../lib/transcript-tool-utils'

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
  status: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'
  title?: string
  target?: string
  input?: string
  output?: string
  error?: string
  startedAt: string
  completedAt?: string | null
  rawInput?: unknown
  rawOutput?: unknown
}

export interface UseSessionTranscriptResult {
  turns: SessionTurn[]
  transcriptVersion: number
  isNearBottom: boolean
  setIsNearBottom: (nearBottom: boolean) => void
  scrollToBottom: () => void
  newContentAvailable: boolean
  acknowledgeNewContent: () => void
  isFinalizing: boolean
  isThinking: boolean
  isStreaming: boolean
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
  const normalizedName = normalizeToolName(tool.toolName, tool.title, tool.rawInput, tool.rawOutput)
  const { displayTitle, displaySubtitle } = inferDisplayTitle(normalizedName, tool.title)
  const input = tool.input ?? stringifyPayload(tool.rawInput)
  const output = tool.output ?? stringifyPayload(tool.rawOutput)
  const parsedEdit = parseEditInput(input)
  const changedFiles = parsedEdit?.patch ? parsePatchOperations(parsedEdit.patch) : undefined
  return {
    id: generateId(),
    type: 'tool',
    tool: {
      toolCallId: tool.toolCallId,
      normalizedName,
      toolName: tool.toolName,
      displayTitle,
      displaySubtitle,
      status: mapStatusToDisplay(tool.status),
      title: tool.title,
      target: tool.target,
      input,
      output,
      error: tool.error,
      startedAt: tool.startedAt,
      completedAt: tool.completedAt,
      rawInput: input,
      rawOutput: output,
      changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
    },
  }
}

function mapStatusToDisplay(status: string): ToolPart['tool']['status'] {
  switch (status) {
    case 'started':
      return 'running'
    case 'completed':
      return 'completed'
    case 'failed':
      return 'failed'
    case 'timeout':
      return 'failed'
    case 'cancelled':
      return 'cancelled'
    default:
      return 'pending'
  }
}

function isTerminalState(state: string): boolean {
  return state === 'completed' || state === 'failed' || state === 'timeout' || state === 'cancelled'
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

function findToolByCorrelation(
  turn: SessionTurn,
  normalizedName: string,
  target?: string,
  toolCallId?: string,
): number {
  return turn.assistant.findIndex((p): p is ToolPart => {
    if (p.type !== 'tool') return false
    if (toolCallId && p.tool.toolCallId === toolCallId) return false
    const toolNormalized = normalizeToolName(p.tool.toolName, p.tool.title, p.tool.rawInput, p.tool.rawOutput)
    if (toolNormalized !== normalizedName) return false
    const toolTarget = p.tool.target ?? p.tool.title
    if (toolTarget !== undefined && target !== toolTarget) return false
    return !isTerminalState(p.tool.status)
  })
}

function updateToolInTurn(
  turn: SessionTurn,
  toolCallId: string,
  updates: Partial<LiveToolCall>,
  correlationKey?: string,
): SessionTurn {
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
        const rawInput = updates.rawInput ?? updates.input ?? toolPart.tool.rawInput ?? toolPart.tool.input
        const rawOutput = updates.rawOutput ?? updates.output ?? toolPart.tool.rawOutput ?? toolPart.tool.output
        const startedAt = updates.startedAt ?? toolPart.tool.startedAt
        const normalizedName = normalizeToolName(
          updates.toolName ?? toolPart.tool.toolName,
          updates.title ?? toolPart.tool.title,
          rawInput,
          rawOutput,
        )
        const input = stringifyPayload(rawInput) ?? toolPart.tool.input
        const output = stringifyPayload(rawOutput) ?? toolPart.tool.output
        const parsedEdit = parseEditInput(input)
        const changedFiles = parsedEdit?.patch ? parsePatchOperations(parsedEdit.patch) : toolPart.tool.changedFiles
        const newStatus = mapStatusToDisplay(updates.status ?? toolPart.tool.status)
        const { status: _updatesStatus, ...restUpdates } = updates
        return {
          ...toolPart,
          tool: {
            ...toolPart.tool,
            ...restUpdates,
            normalizedName,
            input,
            output,
            rawInput: input,
            rawOutput: output,
            changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
            startedAt,
            status: newStatus,
            completedAt: isTerminalState(newStatus) ? now : toolPart.tool.completedAt,
          },
        }
      }),
    }
  }

  if (correlationKey) {
    const [normalizedName, target] = correlationKey.split('|')
    const correlatedIndex = findToolByCorrelation(turn, normalizedName, target)

    if (correlatedIndex >= 0) {
      return {
        ...turn,
        assistant: turn.assistant.map((p, i) => {
          if (i !== correlatedIndex) return p
          const toolPart = p as ToolPart
          const rawInput = updates.rawInput ?? updates.input ?? toolPart.tool.rawInput ?? toolPart.tool.input
          const rawOutput = updates.rawOutput ?? updates.output ?? toolPart.tool.rawOutput ?? toolPart.tool.output
          const startedAt = updates.startedAt ?? toolPart.tool.startedAt
          const input = stringifyPayload(rawInput) ?? toolPart.tool.input
          const output = stringifyPayload(rawOutput) ?? toolPart.tool.output
const parsedEdit = parseEditInput(input)
        const changedFiles = parsedEdit?.patch ? parsePatchOperations(parsedEdit.patch) : toolPart.tool.changedFiles
        const newStatus = mapStatusToDisplay(updates.status ?? toolPart.tool.status)
        const { status: _updatesStatus, ...restUpdates } = updates
        return {
          ...toolPart,
          tool: {
            ...toolPart.tool,
            ...restUpdates,
            toolCallId,
            normalizedName,
            input,
            output,
            rawInput: input,
            rawOutput: output,
            changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
            startedAt,
            status: newStatus,
            completedAt: isTerminalState(newStatus) ? now : toolPart.tool.completedAt,
          },
        }
        }),
      }
    }
  }

  return {
    ...turn,
    assistant: [
      ...turn.assistant,
      createToolPart({
        toolCallId,
        toolName: updates.toolName ?? 'unknown',
        status: (updates.status ?? 'started') as LiveToolCall['status'],
        title: updates.title,
        target: updates.target,
        input: updates.input,
        output: updates.output,
        error: updates.error,
        rawInput: updates.rawInput,
        rawOutput: updates.rawOutput,
        startedAt: now,
        completedAt: isTerminalState(mapStatusToDisplay(updates.status ?? 'started')) ? now : null,
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
  const [isFinalizing, setIsFinalizing] = useState(false)
  const [isThinking, setIsThinking] = useState(false)
  const [isStreaming, setIsStreaming] = useState(false)

  const turnsRef = useRef(turns)
  turnsRef.current = turns
  const isNearBottomRef = useRef(isNearBottom)
  isNearBottomRef.current = isNearBottom

  const liveToolCallMapRef = useRef<Map<string, LiveToolCall>>(new Map())
  const pendingCorrelationRef = useRef<Map<string, { normalizedName: string; target?: string; turnIndex: number }>>(new Map())
  const mountedRef = useRef(true)
  const streamingTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const isStreamingRef = useRef(false)

  const issueId = String(issueNumber)

  const scrollToBottom = useCallback(() => {
    setIsNearBottom(true)
    setNewContentAvailable(false)
  }, [])

  const clearStreaming = useCallback(() => {
    isStreamingRef.current = false
    setIsStreaming(false)
  }, [])

  const bumpTranscriptVersion = useCallback(() => {
    setTranscriptVersion((version) => version + 1)
    if (streamingTimerRef.current !== null) {
      clearTimeout(streamingTimerRef.current)
    }
    isStreamingRef.current = true
    setIsStreaming(true)
    streamingTimerRef.current = setTimeout(() => {
      clearStreaming()
      streamingTimerRef.current = null
    }, 2000)
  }, [clearStreaming])

  const markNewContent = useCallback(() => {
    bumpTranscriptVersion()
    if (!isNearBottomRef.current) {
      setNewContentAvailable(true)
    }
  }, [bumpTranscriptVersion])

  const markNewContentRef = useRef(markNewContent)
  markNewContentRef.current = markNewContent

  const acknowledgeNewContent = useCallback(() => {
    setNewContentAvailable(false)
  }, [])

  const invalidateAndRefetch = useCallback(() => {
    setIsFinalizing(true)
    queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions', sessionId] })
  }, [queryClient, issueNumber, sessionId])

  useEffect(() => {
    setTurns(initialTurns)
    liveToolCallMapRef.current.clear()
    pendingCorrelationRef.current.clear()
    setIsFinalizing(false)
    setIsThinking(false)
    setIsStreaming(false)
    setTranscriptVersion((version) => version + 1)
  }, [initialTurns])

  useEffect(() => {
    if (!isRunning) {
      setIsThinking(false)
      return
    }
    const hasVisibleContent = turns.some(t =>
      t.assistant.some(p => p.type === 'text' || p.type === 'reasoning' || p.type === 'tool')
    )
    if (!hasVisibleContent) {
      setIsThinking(true)
    }
  }, [isRunning, turns])

  useEffect(() => {
    if (!isRunning) return

    mountedRef.current = true
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        setIsStreaming(true)
        setTurns((prev) => {
          const next = ensureLiveTurn(prev, new Date().toISOString())
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = appendTextToTurn(lastTurn, detail.text)
          return next
        })
        setIsThinking(false)
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        const now = new Date().toISOString()
        const toolCallId = detail.toolCallId
        const normalizedName = normalizeToolName(detail.toolName, detail.title, detail.rawInput, detail.rawOutput)
        const pendingCorrelation = pendingCorrelationRef.current.get(toolCallId)
        const target = detail.title ?? pendingCorrelation?.target
        const correlationKey = getCorrelationKey(detail.toolName, detail.title ?? pendingCorrelation?.target, pendingCorrelation?.target)

        if (detail.state === 'started') {
          liveToolCallMapRef.current.set(toolCallId, {
            toolCallId,
            toolName: detail.toolName,
            status: 'started',
            title: detail.title,
            target: detail.title,
            input: stringifyPayload(detail.rawInput),
            output: stringifyPayload(detail.rawOutput),
            error: '',
            rawInput: detail.rawInput,
            rawOutput: detail.rawOutput,
            startedAt: now,
            completedAt: null,
          })

          pendingCorrelationRef.current.set(toolCallId, { normalizedName, target, turnIndex: -1 })

          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = updateToolInTurn(lastTurn, toolCallId, {
              toolName: detail.toolName,
              status: 'started',
              title: detail.title,
              target: detail.title,
              input: stringifyPayload(detail.rawInput),
              output: stringifyPayload(detail.rawOutput),
              rawInput: detail.rawInput,
              rawOutput: detail.rawOutput,
              startedAt: now,
            }, correlationKey)
            return next
          })
          setIsThinking(false)
          markNewContentRef.current()
        } else if (isTerminalState(detail.state)) {
          const existing = liveToolCallMapRef.current.get(toolCallId)
          if (existing) {
            existing.status = detail.state as LiveToolCall['status']
            existing.output = stringifyPayload(detail.rawOutput)
            existing.rawOutput = detail.rawOutput
            existing.completedAt = now
            existing.error = detail.state === 'failed' ? (typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? 'Tool failed')) : existing.error
          }

          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            const error = detail.state === 'failed' ? (typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? 'Tool failed')) : undefined
            next[next.length - 1] = updateToolInTurn(lastTurn, toolCallId, {
              status: mapStatusToDisplay(detail.state) as LiveToolCall['status'],
              output: stringifyPayload(detail.rawOutput),
              rawOutput: detail.rawOutput,
              completedAt: now,
              error,
            }, correlationKey)
            return next
          })
          markNewContentRef.current()

          if (isTerminalState(detail.state)) {
            invalidateAndRefetch()
          }
        }
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
        invalidateAndRefetch()
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_failed', (detail) => {
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

        const errorPart = createErrorPart(
          detail.reason ?? 'Session failed',
          'failed',
          now,
        )
        setTurns((prev) => {
          const next = [...prev]
          if (next.length > 0) {
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = {
              ...lastTurn,
              assistant: [...lastTurn.assistant, errorPart],
            }
          }
          return next
        })

        invalidateAndRefetch()
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_cancelled', (detail) => {
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

        const errorPart = createErrorPart(
          detail.reason ?? 'Session cancelled',
          'cancelled',
          now,
        )
        setTurns((prev) => {
          const next = [...prev]
          if (next.length > 0) {
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = {
              ...lastTurn,
              assistant: [...lastTurn.assistant, errorPart],
            }
          }
          return next
        })

        invalidateAndRefetch()
        markNewContentRef.current()
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
        markNewContentRef.current()

        if (detail.status === 'recovered' || detail.status === 'failed') {
          invalidateAndRefetch()
        }
      }),
    )

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
    }
  }, [issueId, sessionId, acpSessionId, issueNumber, isRunning, queryClient, invalidateAndRefetch])

return {
    turns,
    transcriptVersion,
    isNearBottom,
    setIsNearBottom,
    scrollToBottom,
    newContentAvailable,
    acknowledgeNewContent,
    isFinalizing,
    isThinking,
    isStreaming,
  }
}