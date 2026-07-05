export type SessionEvent = {
  id: number
  sequence: number
  type: string
  payload: unknown
  createdAt: string
}

export type SessionViewKind = 'chat' | 'timeline' | 'compact'

export type SessionChatPart =
  | { id: string; partType: 'text'; text: string; startedAt: string; completedAt: string | null }
  | { id: string; partType: 'reasoning'; text: string; startedAt: string; completedAt: string | null }
  | {
      id: string
      partType: 'tool'
      toolCallId: string
      toolName: string
      normalizedName: string
      status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled'
      title?: string
      input?: string
      output?: string
      error?: string
      startedAt: string
      completedAt: string | null
    }
  | { id: string; partType: 'error'; message: string; kind: 'failed' | 'cancelled' | 'recovery'; at: string }

export type SessionChatTurn = {
  id: string
  startedAt: string
  completedAt: string | null
  incomplete: boolean
  prompt: {
    text: string
    kind: 'initial' | 'task' | 'retry' | 'followup' | 'recovery' | 'legacy-missing'
    sentAt: string
  }
  parts: SessionChatPart[]
}

export type SessionChatView = {
  kind: 'chat'
  turns: SessionChatTurn[]
}

export type SessionTimelineToolCall = {
  toolCallId: string
  toolName: string
  state: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled'
  title?: string
  rawInput?: string
  rawOutput?: string
  startedAt: string
  completedAt: string | null
}

export type SessionTimelineRecovery = {
  status: 'detected' | 'recovering' | 'recovered' | 'failed'
  attempt?: number
  reason?: string
  at: string
}

export type SessionTimelineCompaction = {
  id?: string | number
  strategy?: string
  contextWindowUsedBefore?: number | null
  contextWindowUsedAfter?: number | null
  contextWindowSize?: number | null
  summary?: string
  at: string
}

export type SessionTimelineRound = {
  roundIndex: number
  startedAt: string
  completedAt: string | null
  userText: string
  agentText: string
  thoughtText: string
  toolCalls: SessionTimelineToolCall[]
  recovery: SessionTimelineRecovery[]
  compactions: SessionTimelineCompaction[]
}

export type SessionTimelineView = {
  kind: 'timeline'
  rounds: SessionTimelineRound[]
}

export type SessionCompactView = {
  kind: 'compact'
  eventCount: number
  toolCount: number
  messageChunkCount: number
  thoughtChunkCount: number
  promptCount: number
  terminalStatus: 'completed' | 'failed' | 'cancelled' | 'running' | 'unknown'
  failureReason?: string
  startedAt: string | null
  lastActivityAt: string | null
  firstPromptText: string | null
  firstPromptKind: string | null
  preview: string | null
}

export type SessionView =
  | SessionChatView
  | SessionTimelineView
  | SessionCompactView

import { buildChatView } from './view/chat'
import {
  defaultToolStatus,
  extractTextChunk,
  getNumberProp,
  getStringProp,
  isAssistantReasoningEvent,
  isAssistantTextEvent,
  isInputEvent,
  isLivenessEvent,
  isSessionClosedEvent,
  isToolEvent,
  mapTerminalStatus,
  mapToolState,
  narrowPayload,
  normalizeRaw,
  readToolString,
  readToolValue,
} from './view/helpers'

function isCompactionEvent(type: string): boolean {
  return type === 'compaction' || type === 'compaction_event'
}

function buildTimelineView(events: SessionEvent[]): SessionTimelineView {
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
  }

  if (current) {
    finalizeCurrent(events.length > 0 ? events[events.length - 1].createdAt : null)
  }

  return { kind: 'timeline', rounds }
}

function buildCompactView(events: SessionEvent[]): SessionCompactView {
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

    if (isSessionClosedEvent(event.type)) {
      const status = mapTerminalStatus(getStringProp(payload, 'status'))
      terminalStatus = status
      if (status === 'failed' || status === 'cancelled') {
        failureReason = getStringProp(payload, 'failureReason')
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

export function viewSessionEvents<K extends SessionViewKind>(
  events: SessionEvent[],
  kind: K,
): Extract<SessionView, { kind: K }> {
  if (kind === 'chat') return buildChatView(events) as Extract<SessionView, { kind: K }>
  if (kind === 'timeline') return buildTimelineView(events) as Extract<SessionView, { kind: K }>
  return buildCompactView(events) as Extract<SessionView, { kind: K }>
}
