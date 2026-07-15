import { useEffect, useMemo, useRef, useState, useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../../../entities/agent'
import type { AgentDetailEventMap } from '../../../entities/agent'
import type { SessionTurn } from '../../../entities/coder-session'
import {
  appendInputTurn,
  appendReasoningToTurn,
  appendTextToTurn,
  asRecord,
  buildLiveToolDetails,
  closeActiveTextPart,
  closeLatestTurn,
  createErrorPart,
  deriveToolTarget,
  ensureLiveTurn,
  getDisplayFields,
  getNormalizedName,
  isTerminalState,
  mapStatusToDisplay,
  updateToolInTurn,
  type LiveToolCall,
} from './transcript-state'
import {
  stringifyPayload,
  getCorrelationKey,
} from './transcript-tool-utils'

interface UseSessionTranscriptOptions {
  issueNumber: number
  sessionId: string
  runtimeSessionId: string
  initialTurns?: SessionTurn[]
  sessionQueryKeys?: readonly (readonly unknown[])[]
  isRunning: boolean
  terminalInvalidationKey?: readonly unknown[]
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

export function useSessionTranscript({
  issueNumber,
  sessionId,
  runtimeSessionId,
  initialTurns,
  sessionQueryKeys,
  isRunning,
  terminalInvalidationKey,
}: UseSessionTranscriptOptions): UseSessionTranscriptResult {
  const queryClient = useQueryClient()
  const initialState = useMemo<SessionTurn[]>(() => {
    return initialTurns ?? []
  }, [])
  const [turns, setTurns] = useState<SessionTurn[]>(initialState)
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
  const hasLiveTailRef = useRef(false)

  const issueId = String(issueNumber)

  useEffect(() => {
    hasLiveTailRef.current = false
  }, [issueId, sessionId, runtimeSessionId])

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
    for (const queryKey of sessionQueryKeys ?? []) {
      queryClient.invalidateQueries({ queryKey })
    }
    const invKey = terminalInvalidationKey ?? ['issues', issueNumber, 'coder-sessions', sessionId]
    queryClient.invalidateQueries({ queryKey: invKey })
  }, [queryClient, issueNumber, sessionId, sessionQueryKeys, terminalInvalidationKey])

  useEffect(() => {
    if (hasLiveTailRef.current && isRunning) {
      return
    }
    setTurns(initialTurns ?? [])
    hasLiveTailRef.current = false
    liveToolCallMapRef.current.clear()
    pendingCorrelationRef.current.clear()
    setIsFinalizing(false)
    setIsThinking(false)
    setIsStreaming(false)
    setTranscriptVersion((version) => version + 1)
  }, [initialTurns, isRunning])

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
    const isCurrentSessionEvent = (detail: { runtimeSessionId?: string | null; sessionId?: string | null }) => {
      if (!mountedRef.current) return false
      if (runtimeSessionId && detail.runtimeSessionId === runtimeSessionId) return true
      return detail.sessionId === sessionId
    }
    const handleToolDetail = (detail: AgentDetailEventMap['tool_call.started']) => {
      if (!isCurrentSessionEvent(detail)) return

      hasLiveTailRef.current = true
      const now = new Date().toISOString()
      const toolCallId = detail.toolCallId
      const normalizedName = getNormalizedName(detail)
      const pendingCorrelation = pendingCorrelationRef.current.get(toolCallId)
      const target = deriveToolTarget(detail.toolName, detail.rawInput, detail.title) ?? pendingCorrelation?.target
      const correlationKey = getCorrelationKey(detail.toolName, detail.title, target)
      const metadata = detail.metadata ?? detail.rawOutputMetadata
      const detailsMetadata = asRecord(metadata)
      const liveDetails = detail.details ?? buildLiveToolDetails(normalizedName, detail.rawInput, detail.rawOutput, detailsMetadata ?? undefined)
      const { displayTitle, displaySubtitle } = getDisplayFields(detail)

      if (detail.state === 'started') {
        liveToolCallMapRef.current.set(toolCallId, {
          toolCallId,
          toolName: detail.toolName,
          normalizedName,
          displayTitle,
          displaySubtitle,
          category: detail.category,
          status: 'started',
          title: detail.title,
          target,
          input: stringifyPayload(detail.rawInput),
          output: stringifyPayload(detail.rawOutput),
          error: '',
          rawInput: detail.rawInput,
          rawOutput: detail.rawOutput,
          metadata: detailsMetadata ?? undefined,
          details: liveDetails,
          startedAt: now,
          completedAt: null,
        })

        pendingCorrelationRef.current.set(toolCallId, { normalizedName, target, turnIndex: -1 })

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = updateToolInTurn(lastTurn, toolCallId, {
            toolName: detail.toolName,
            normalizedName,
            displayTitle,
            displaySubtitle,
            category: detail.category,
            status: 'started',
            title: detail.title,
            target,
            input: stringifyPayload(detail.rawInput),
            output: stringifyPayload(detail.rawOutput),
            rawInput: detail.rawInput,
            rawOutput: detail.rawOutput,
            metadata: detailsMetadata ?? undefined,
            details: liveDetails,
            startedAt: now,
          }, correlationKey)
          return next
        })
        setIsThinking(false)
        markNewContentRef.current()
        return
      }

      if (!isTerminalState(detail.state)) {
        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = updateToolInTurn(lastTurn, toolCallId, {
            status: detail.state as LiveToolCall['status'],
            toolName: detail.toolName,
            normalizedName,
            displayTitle,
            displaySubtitle,
            category: detail.category,
            title: detail.title,
            target,
            input: stringifyPayload(detail.rawInput),
            output: stringifyPayload(detail.rawOutput),
            rawInput: detail.rawInput,
            rawOutput: detail.rawOutput,
            metadata: detailsMetadata ?? undefined,
            details: liveDetails,
          }, correlationKey)
          return next
        })
        setIsThinking(false)
        markNewContentRef.current()
        return
      }

      const existing = liveToolCallMapRef.current.get(toolCallId)
      if (existing) {
        existing.status = detail.state as LiveToolCall['status']
        existing.normalizedName = normalizedName
        existing.displayTitle = displayTitle
        existing.displaySubtitle = displaySubtitle
        existing.category = detail.category ?? existing.category
        existing.input = stringifyPayload(detail.rawInput) ?? existing.input
        existing.output = stringifyPayload(detail.rawOutput)
        existing.rawInput = detail.rawInput ?? existing.rawInput
        existing.rawOutput = detail.rawOutput
        existing.metadata = detailsMetadata ?? existing.metadata
        existing.details = liveDetails ?? existing.details
        existing.completedAt = now
        existing.error = detail.state === 'failed'
          ? (typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? 'Tool failed'))
          : existing.error
      }

      setTurns((prev) => {
        const next = ensureLiveTurn(prev, now)
        const lastTurn = next[next.length - 1]
        const error = detail.state === 'failed'
          ? (typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? 'Tool failed'))
          : undefined
        next[next.length - 1] = updateToolInTurn(lastTurn, toolCallId, {
          status: mapStatusToDisplay(detail.state) as LiveToolCall['status'],
          toolName: detail.toolName,
          normalizedName,
          displayTitle,
          displaySubtitle,
          category: detail.category,
          title: detail.title,
          target,
          input: stringifyPayload(detail.rawInput),
          output: stringifyPayload(detail.rawOutput),
          rawInput: detail.rawInput,
          rawOutput: detail.rawOutput,
          metadata: detailsMetadata ?? undefined,
          details: liveDetails,
          completedAt: now,
          error,
        }, correlationKey)
        return next
      })
      liveToolCallMapRef.current.delete(toolCallId)
      pendingCorrelationRef.current.delete(toolCallId)
      markNewContentRef.current()
      invalidateAndRefetch()
    }

    unsubs.push(
      onAgentEvent('session.input', (detail) => {
        if (!isCurrentSessionEvent(detail)) return

        hasLiveTailRef.current = true
        setTurns((prev) => appendInputTurn(prev, {
          text: detail.text,
          kind: detail.kind,
          sentAt: detail.sentAt,
        }))
        setIsThinking(true)
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('message.delta', (detail) => {
        if (!isCurrentSessionEvent(detail)) return

        hasLiveTailRef.current = true
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
      onAgentEvent('coder_text_chunk', (detail) => {
        if (!isCurrentSessionEvent(detail)) return

        hasLiveTailRef.current = true
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
      onAgentEvent('reasoning.delta', (detail) => {
        if (!isCurrentSessionEvent(detail)) return

        hasLiveTailRef.current = true
        setIsStreaming(true)
        setTurns((prev) => {
          const now = new Date().toISOString()
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          const withClosedText = closeActiveTextPart(lastTurn, now)
          next[next.length - 1] = appendReasoningToTurn(withClosedText, detail.text)
          return next
        })
        setIsThinking(true)
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('tool_call.started', handleToolDetail),
    )

    unsubs.push(
      onAgentEvent('tool_call.updated', handleToolDetail),
    )

    unsubs.push(
      onAgentEvent('tool_call.completed', handleToolDetail),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        handleToolDetail({
          ...detail,
          state: (detail.state ?? detail.status ?? 'started') as AgentDetailEventMap['tool_call.started']['state'],
        })
      }),
    )

    unsubs.push(
      onAgentEvent('session.closed', (detail) => {
        if (!isCurrentSessionEvent(detail)) return

        hasLiveTailRef.current = true
        const now = new Date().toISOString()
        setTurns((prev) => closeLatestTurn(prev, now))

        if (detail.status === 'failed' || detail.status === 'cancelled') {
          const errorPart = createErrorPart(
            detail.failureReason ?? `Session ${detail.status}`,
            detail.status === 'cancelled' ? 'cancelled' : 'failed',
            now,
          )
          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = {
              ...lastTurn,
              assistant: [...lastTurn.assistant, errorPart],
            }
            return next
          })
        }

        setIsThinking(false)
        clearStreaming()
        invalidateAndRefetch()
        markNewContentRef.current()
      }),
    )

    const handleLegacyTerminalEvent = (
      detail: { sessionId?: string | null; runtimeSessionId?: string | null; reason?: string | null },
      status: 'completed' | 'failed' | 'cancelled',
    ) => {
      if (!isCurrentSessionEvent(detail)) return

      hasLiveTailRef.current = true
      const now = new Date().toISOString()
      setTurns((prev) => closeLatestTurn(prev, now))

      if (status === 'failed' || status === 'cancelled') {
        const errorPart = createErrorPart(
          detail.reason ?? `Session ${status}`,
          status === 'cancelled' ? 'cancelled' : 'failed',
          now,
        )
        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = {
            ...lastTurn,
            assistant: [...lastTurn.assistant, errorPart],
          }
          return next
        })
      }

      setIsThinking(false)
      clearStreaming()
      invalidateAndRefetch()
      markNewContentRef.current()
    }

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        handleLegacyTerminalEvent(detail, 'completed')
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_failed', (detail) => {
        handleLegacyTerminalEvent(detail, 'failed')
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_cancelled', (detail) => {
        handleLegacyTerminalEvent(detail, 'cancelled')
      }),
    )

    unsubs.push(
      onAgentEvent('coder_recovery_status', (detail) => {
        if (!isCurrentSessionEvent(detail)) return

        hasLiveTailRef.current = true
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

    unsubs.push(
      onAgentEvent('session.liveness', (detail) => {
        if (!isCurrentSessionEvent(detail)) return

        hasLiveTailRef.current = true
        const timestamp = detail.probeSentAt ?? detail.lastDataAt ?? new Date().toISOString()
        const message = detail.status === 'probing'
          ? `Liveness probe sent. Waiting until ${detail.probeDeadlineAt ?? 'deadline unknown'}. Last activity: ${detail.lastActivityType ?? 'unknown'}.`
          : detail.status === 'running'
            ? `Liveness recovered after ${detail.lastActivityType ?? 'session'} activity.`
            : `Liveness failed: ${detail.failureReason ?? 'unknown'}. Last activity: ${detail.lastActivityType ?? 'unknown'}.`

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, timestamp)
          const lastTurn = next[next.length - 1]
          const newPart = createErrorPart(message, 'recovery', timestamp)
          next[next.length - 1] = {
            ...lastTurn,
            assistant: [...lastTurn.assistant, newPart],
          }
          return next
        })
        markNewContentRef.current()

        if (detail.status === 'running' || detail.status === 'failed') {
          invalidateAndRefetch()
        }
      }),
    )

    return () => {
      mountedRef.current = false
      if (streamingTimerRef.current !== null) {
        clearTimeout(streamingTimerRef.current)
        streamingTimerRef.current = null
      }
      for (const unsub of unsubs) unsub()
    }
  }, [issueId, sessionId, runtimeSessionId, issueNumber, isRunning, queryClient, invalidateAndRefetch])

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
