import type { SessionCompactView, SessionEvent } from '../types'
import {
  extractTextChunk,
  getStringProp,
  isAssistantReasoningEvent,
  isAssistantTextEvent,
  isInputEvent,
  isLivenessEvent,
  isSessionActivityEvent,
  isToolEvent,
  narrowPayload,
  readToolString,
} from './helpers'

export function buildCompactView(events: SessionEvent[]): SessionCompactView {
  let eventCount = 0
  let toolCount = 0
  let messageChunkCount = 0
  let thoughtChunkCount = 0
  let promptCount = 0
  let terminalStatus: 'completed' | 'failed' | 'cancelled' | 'running' | 'unknown' = 'running'
  let failureReason: string | undefined
  let startedAt: string | null = null
  let lastActivityAt: string | null = null
  let firstPromptText: string | null = null
  let firstPromptKind: string | null = null
  let preview: string | null = null
  const seenToolCallIds = new Set<string>()

  for (const event of events) {
    eventCount += 1
    if (!startedAt) startedAt = event.createdAt
    lastActivityAt = event.createdAt
    const payload = narrowPayload(event)

    if (isInputEvent(event.type)) {
      promptCount += 1
      if (firstPromptText === null) {
        firstPromptText = getStringProp(payload, 'text') ?? null
        firstPromptKind = getStringProp(payload, 'kind') ?? getStringProp(payload, 'source') ?? null
      }
      continue
    }

    if (isAssistantTextEvent(event.type)) {
      messageChunkCount += 1
      if (preview === null) {
        const text = extractTextChunk(payload)
        if (text) preview = text.length > 200 ? `${text.slice(0, 200)}…` : text
      }
      continue
    }

    if (isAssistantReasoningEvent(event.type)) {
      thoughtChunkCount += 1
      continue
    }

    if (isToolEvent(event.type)) {
      const toolCallId = readToolString(payload, 'toolCallId', 'id')
      if (toolCallId && !seenToolCallIds.has(toolCallId)) {
        seenToolCallIds.add(toolCallId)
        toolCount += 1
      } else if (!toolCallId) {
        toolCount += 1
      }
      continue
    }

    if (isSessionActivityEvent(event.type)) {
      const activity = getStringProp(payload, 'activity')
      if (activity === 'idle' && terminalStatus === 'running') {
        terminalStatus = 'completed'
      }
      continue
    }

    if (isLivenessEvent(event.type)) {
      const status = getStringProp(payload, 'status')
      if (status === 'failed') {
        terminalStatus = 'failed'
        failureReason = getStringProp(payload, 'failureReason') ?? failureReason
      }
      continue
    }
  }

  const summary: SessionCompactView = {
    kind: 'compact',
    eventCount,
    toolCount,
    messageChunkCount,
    thoughtChunkCount,
    promptCount,
    terminalStatus,
    startedAt,
    lastActivityAt,
    firstPromptText,
    firstPromptKind,
    preview,
  }
  if (failureReason !== undefined) {
    summary.failureReason = failureReason
  }
  return summary
}
