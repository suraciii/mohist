import type { SessionChatPart, SessionChatTurn, SessionChatView, SessionEvent } from '../types'
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
  mapToolState,
  narrowPayload,
  normalizeRaw,
  normalizeToolName,
  readToolString,
  readToolValue,
} from './helpers'

const KNOWN_PROMPT_KINDS = new Set([
  'initial', 'task', 'retry', 'followup', 'recovery', 'legacy-missing',
])

function nextPartId(turnId: string, counter: { value: number }): string {
  counter.value += 1
  return `${turnId}-p${counter.value}`
}

function appendTextPart(
  turn: SessionChatTurn,
  text: string,
  counter: { value: number },
  at: string,
): SessionChatTurn {
  const last = turn.parts[turn.parts.length - 1]
  if (last && last.partType === 'text' && last.completedAt === null) {
    const updated: SessionChatPart = { ...last, text: last.text + text }
    return { ...turn, parts: [...turn.parts.slice(0, -1), updated] }
  }
  const part: SessionChatPart = {
    id: nextPartId(turn.id, counter),
    partType: 'text',
    text,
    startedAt: at,
    completedAt: null,
  }
  return { ...turn, parts: [...turn.parts, part] }
}

function appendReasoningPart(
  turn: SessionChatTurn,
  text: string,
  counter: { value: number },
  at: string,
): SessionChatTurn {
  const last = turn.parts[turn.parts.length - 1]
  if (last && last.partType === 'reasoning' && last.completedAt === null) {
    const updated: SessionChatPart = { ...last, text: last.text + text }
    return { ...turn, parts: [...turn.parts.slice(0, -1), updated] }
  }
  const part: SessionChatPart = {
    id: nextPartId(turn.id, counter),
    partType: 'reasoning',
    text,
    startedAt: at,
    completedAt: null,
  }
  return { ...turn, parts: [...turn.parts, part] }
}

function closeOpenTextParts(turn: SessionChatTurn, at: string): SessionChatTurn {
  let changed = false
  const parts: SessionChatPart[] = turn.parts.map((part) => {
    if ((part.partType === 'text' || part.partType === 'reasoning') && part.completedAt === null) {
      changed = true
      return { ...part, completedAt: at }
    }
    return part
  })
  return changed ? { ...turn, parts } : turn
}

function upsertToolPart(
  turn: SessionChatTurn,
  toolCallId: string,
  toolName: string,
  title: string | undefined,
  status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled',
  rawInput: string | undefined,
  rawOutput: string | undefined,
  error: string | undefined,
  at: string,
  counter: { value: number },
): SessionChatTurn {
  const existingIndex = turn.parts.findIndex(
    (p): p is Extract<SessionChatPart, { partType: 'tool' }> => p.partType === 'tool' && p.toolCallId === toolCallId,
  )
  if (existingIndex >= 0) {
    const existing = turn.parts[existingIndex] as Extract<SessionChatPart, { partType: 'tool' }>
    const updated: SessionChatPart = {
      ...existing,
      status,
      title: title ?? existing.title,
      input: rawInput ?? existing.input,
      output: rawOutput ?? existing.output,
      error: error ?? existing.error,
      completedAt: status === 'completed' || status === 'failed' || status === 'cancelled' ? at : existing.completedAt,
    }
    return { ...turn, parts: turn.parts.map((p, i) => (i === existingIndex ? updated : p)) }
  }
  const part: SessionChatPart = {
    id: nextPartId(turn.id, counter),
    partType: 'tool',
    toolCallId,
    toolName,
    normalizedName: normalizeToolName(toolName, title),
    status,
    title,
    input: rawInput,
    output: rawOutput,
    error,
    startedAt: at,
    completedAt: status === 'completed' || status === 'failed' || status === 'cancelled' ? at : null,
  }
  return { ...turn, parts: [...turn.parts, part] }
}

function pushErrorPart(
  turn: SessionChatTurn,
  message: string,
  kind: 'failed' | 'cancelled' | 'recovery',
  at: string,
  counter: { value: number },
): SessionChatTurn {
  const part: SessionChatPart = {
    id: nextPartId(turn.id, counter),
    partType: 'error',
    message,
    kind,
    at,
  }
  return { ...turn, parts: [...turn.parts, part] }
}

function providerRetryMessage(payload: Record<string, unknown>): string {
  const phase = getStringProp(payload, 'phase')
  const attempt = getNumberProp(payload, 'attempt')
  const maxAttempts = getNumberProp(payload, 'maxAttempts')
  const message = getStringProp(payload, 'message')
  const progress = attempt && maxAttempts ? ` (${attempt}/${maxAttempts})` : ''
  return `Provider retry${phase ? `: ${phase}` : ''}${progress}${message ? ` - ${message}` : ''}`
}

function makeInitialTurn(): SessionChatTurn {
  const at = new Date(0).toISOString()
  return {
    id: 'turn-legacy-missing',
    startedAt: at,
    completedAt: null,
    incomplete: true,
    prompt: {
      text: '',
      kind: 'legacy-missing',
      sentAt: at,
    },
    parts: [],
  }
}

function makePromptTurn(
  turnIndex: number,
  text: string,
  kind: string,
  sentAt: string,
): SessionChatTurn {
  const promptKind = KNOWN_PROMPT_KINDS.has(kind)
    ? (kind as SessionChatTurn['prompt']['kind'])
    : 'task'
  return {
    id: `turn-${turnIndex}`,
    startedAt: sentAt,
    completedAt: null,
    incomplete: false,
    prompt: {
      text,
      kind: promptKind,
      sentAt,
    },
    parts: [],
  }
}

export function buildChatView(events: SessionEvent[]): SessionChatView {
  const turns: SessionChatTurn[] = []
  let current: SessionChatTurn | null = null
  let turnIndex = 0
  let counter = { value: 0 }
  let lastTimestamp: string | null = null

  const finalizeCurrent = (at: string | null) => {
    if (!current) return
    if (at) {
      current = closeOpenTextParts(current, at)
    }
    current = { ...current, incomplete: false }
    turns.push(current)
  }

  for (const event of events) {
    const payload = narrowPayload(event)
    lastTimestamp = event.createdAt

    if (isInputEvent(event.type)) {
      finalizeCurrent(event.createdAt)
      const text = getStringProp(payload, 'text') ?? ''
      const kind = getStringProp(payload, 'kind') ?? getStringProp(payload, 'source') ?? 'task'
      current = makePromptTurn(turnIndex, text, kind, event.createdAt)
      turnIndex += 1
      continue
    }

    if (!current) {
      current = makeInitialTurn()
    }

    if (isAssistantTextEvent(event.type)) {
      const text = extractTextChunk(payload)
      if (text) {
        current = appendTextPart(current, text, counter, event.createdAt)
      }
      continue
    }

    if (isAssistantReasoningEvent(event.type)) {
      const text = extractTextChunk(payload)
      if (text) {
        current = appendReasoningPart(current, text, counter, event.createdAt)
      }
      continue
    }

    if (isToolEvent(event.type)) {
      const toolCallId = readToolString(payload, 'toolCallId', 'id', 'callId')
      if (!toolCallId) continue
      const toolName = readToolString(payload, 'toolName', 'kind', 'name') ?? 'unknown'
      const title = readToolString(payload, 'title')
      const status = mapToolState(readToolString(payload, 'status', 'state') ?? defaultToolStatus(event.type))
      const rawInput = normalizeRaw(readToolValue(payload, 'rawInput', 'input'))
      const rawOutput = normalizeRaw(readToolValue(payload, 'rawOutput', 'output'))
      const error = status === 'failed' ? rawOutput : undefined
      current = upsertToolPart(current, toolCallId, toolName, title, status, rawInput, rawOutput, error, event.createdAt, counter)
      continue
    }

    if (isSessionClosedEvent(event.type)) {
      const status = getStringProp(payload, 'status') ?? 'completed'
      if (current) {
        current = { ...current, completedAt: event.createdAt }
        if (status === 'failed' || status === 'cancelled') {
          const reason = getStringProp(payload, 'failureReason') ?? `Session ${status}`
          current = pushErrorPart(current, reason, status === 'failed' ? 'failed' : 'cancelled', event.createdAt, counter)
        }
      }
      continue
    }

    if (isLivenessEvent(event.type)) {
      const status = getStringProp(payload, 'status') ?? 'running'
      if (status === 'failed') {
        const reason = getStringProp(payload, 'failureReason') ?? 'Liveness failed'
        current = pushErrorPart(current, reason, 'recovery', event.createdAt, counter)
      }
      continue
    }

    if (event.type === 'provider.retry') {
      current = pushErrorPart(current, providerRetryMessage(payload), 'recovery', event.createdAt, counter)
      continue
    }
  }

  if (current) {
    finalizeCurrent(lastTimestamp)
  } else if (events.length > 0) {
    turns.push(makeInitialTurn())
  }

  return { kind: 'chat', turns }
}
