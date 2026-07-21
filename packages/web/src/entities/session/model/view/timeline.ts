import type { SessionEvent, SessionTimelineRound, SessionTimelineToolCall, SessionTimelineView } from '../types'
import {
  defaultToolStatus,
  extractTextChunk,
  getNumberProp,
  getStringProp,
  isAssistantReasoningEvent,
  isAssistantTextEvent,
  isInputEvent,
  isLivenessEvent,
  isToolEvent,
  mapToolState,
  narrowPayload,
  normalizeRaw,
  readToolString,
  readToolValue,
} from './helpers'

function isCompactionEvent(type: string): boolean {
  return type === 'compaction' || type === 'compaction_event'
}

export function buildTimelineView(events: SessionEvent[]): SessionTimelineView {
  const rounds: SessionTimelineRound[] = []
  const toolCallMap = new Map<string, SessionTimelineToolCall>()
  let current: SessionTimelineRound | null = null

  const finalizeCurrent = (at: string | null) => {
    if (!current) return
    if (at) {
      current.completedAt = at
    }
    current.toolCalls = Array.from(toolCallMap.values())
    rounds.push(current)
  }

  const ensureCurrent = (at: string) => {
    if (current) return current
    toolCallMap.clear()
    current = {
      roundIndex: rounds.length,
      startedAt: at,
      completedAt: null,
      userText: '',
      agentText: '',
      thoughtText: '',
      toolCalls: [],
      recovery: [],
      compactions: [],
    }
    return current
  }

  for (const event of events) {
    const payload = narrowPayload(event)

    if (isInputEvent(event.type)) {
      finalizeCurrent(event.createdAt)
      toolCallMap.clear()
      const text = getStringProp(payload, 'text') ?? ''
      current = {
        roundIndex: rounds.length,
        startedAt: event.createdAt,
        completedAt: null,
        userText: text,
        agentText: '',
        thoughtText: '',
        toolCalls: [],
        recovery: [],
        compactions: [],
      }
      continue
    }

    if (isCompactionEvent(event.type)) {
      // Compaction events are attached to the active round so they
      // appear alongside the activity that triggered them. If the
      // session has not yet produced a round (e.g. only metadata
      // refresh events), synthesise an empty round so the compaction
      // is still visible to the user.
      const round = ensureCurrent(event.createdAt)
      const beforeValue = readToolValue(payload, 'contextWindowUsedBefore')
      const afterValue = readToolValue(payload, 'contextWindowUsedAfter')
      const sizeValue = readToolValue(payload, 'contextWindowSize')
      const before = typeof beforeValue === 'number' ? beforeValue : null
      const after = typeof afterValue === 'number' ? afterValue : null
      const size = typeof sizeValue === 'number' ? sizeValue : null
      round.compactions.push({
        id: event.id,
        strategy: getStringProp(payload, 'strategy'),
        contextWindowUsedBefore: before,
        contextWindowUsedAfter: after,
        contextWindowSize: size,
        summary: getStringProp(payload, 'summary'),
        at: event.createdAt,
      })
      continue
    }

    if (!current) {
      toolCallMap.clear()
      current = {
        roundIndex: 0,
        startedAt: event.createdAt,
        completedAt: null,
        userText: '',
        agentText: '',
        thoughtText: '',
        toolCalls: [],
        recovery: [],
        compactions: [],
      }
    }

    if (isAssistantTextEvent(event.type)) {
      const text = extractTextChunk(payload)
      if (text && current) current.agentText += text
      continue
    }

    if (isAssistantReasoningEvent(event.type)) {
      const text = extractTextChunk(payload)
      if (text && current) current.thoughtText += text
      continue
    }

    if (isToolEvent(event.type)) {
      const toolCallId = readToolString(payload, 'toolCallId', 'id')
      if (!toolCallId) continue
      const status = readToolString(payload, 'status', 'state') ?? defaultToolStatus(event.type)
      const toolName = readToolString(payload, 'toolName', 'kind', 'name') ?? 'unknown'
      const title = readToolString(payload, 'title')
      const rawInput = normalizeRaw(readToolValue(payload, 'rawInput', 'input'))
      const rawOutput = normalizeRaw(readToolValue(payload, 'rawOutput', 'output'))

      if (status === 'completed' || status === 'failed' || status === 'cancelled') {
        const existing = toolCallMap.get(toolCallId)
        if (existing) {
          existing.state = status
          if (title !== undefined) existing.title = title
          if (rawInput !== undefined) existing.rawInput = rawInput
          if (rawOutput !== undefined) existing.rawOutput = rawOutput
          existing.completedAt = event.createdAt
        } else {
          toolCallMap.set(toolCallId, {
            toolCallId,
            toolName,
            state: status,
            title,
            rawInput,
            rawOutput,
            startedAt: event.createdAt,
            completedAt: event.createdAt,
          })
        }
      } else {
        toolCallMap.set(toolCallId, {
          toolCallId,
          toolName,
          state: mapToolState(status),
          title,
          rawInput,
          rawOutput,
          startedAt: event.createdAt,
          completedAt: null,
        })
      }
      continue
    }

    if (isLivenessEvent(event.type)) {
      if (!current) continue
      const status = getStringProp(payload, 'status') ?? 'running'
      const mapped = status === 'probing' ? 'recovering' : status === 'running' ? 'recovered' : status === 'failed' ? 'failed' : 'detected'
      current.recovery.push({
        status: mapped,
        attempt: getNumberProp(payload, 'activeProbeVersion') ?? getNumberProp(payload, 'attempt'),
        reason: getStringProp(payload, 'failureReason'),
        at: event.createdAt,
      })
      continue
    }

    if (event.type === 'provider.retry') {
      current.recovery.push({
        status: 'recovering',
        attempt: getNumberProp(payload, 'attempt'),
        reason: getStringProp(payload, 'message') ?? 'Provider retry',
        at: event.createdAt,
      })
      continue
    }
  }

  if (current) {
    finalizeCurrent(events.length > 0 ? events[events.length - 1].createdAt : null)
  }

  return { kind: 'timeline', rounds }
}
