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
import { buildTimelineView } from './view/timeline'
import {
  extractTextChunk,
  getStringProp,
  isAssistantReasoningEvent,
  isAssistantTextEvent,
  isInputEvent,
  isLivenessEvent,
  isSessionClosedEvent,
  isToolEvent,
  mapTerminalStatus,
  narrowPayload,
  readToolString,
} from './view/helpers'

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
