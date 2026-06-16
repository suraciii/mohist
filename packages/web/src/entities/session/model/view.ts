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

const KNOWN_PROMPT_KINDS = new Set([
  'initial', 'task', 'retry', 'followup', 'recovery', 'legacy-missing',
])

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function narrowPayload(event: SessionEvent): Record<string, unknown> {
  return isRecord(event.payload) ? event.payload : {}
}

function getStringProp(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key]
  return typeof value === 'string' && value ? value : undefined
}

function getNumberProp(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function extractTextChunk(payload: Record<string, unknown>): string {
  const direct = getStringProp(payload, 'text')
  if (direct !== undefined) return direct
  const content = payload.content
  if (isRecord(content)) {
    const nested = getStringProp(content, 'text')
    if (nested !== undefined) return nested
  }
  return ''
}

function normalizeRaw(value: unknown): string | undefined {
  if (value === undefined || value === null) return undefined
  if (typeof value === 'string') return value
  try {
    return JSON.stringify(value)
  } catch {
    return undefined
  }
}

function normalizeToolName(toolName: string, title?: string): string {
  if (title) {
    const lower = title.toLowerCase()
    if (lower.startsWith('loaded skill:') || lower === 'skill' || lower.startsWith('skill:')) return 'skill'
    if (lower.includes('subagent') || lower.includes('delegate') || lower.startsWith('task:')) return 'task'
    if (lower.includes('apply_patch')) return 'apply_patch'
    if (lower.includes('search_files')) return 'search_files'
    if (lower.includes('webfetch')) return 'webfetch'
    if (lower.includes('websearch')) return 'websearch'
    if (lower.includes('todowrite')) return 'todowrite'
    if (lower === 'todo' || lower.startsWith('todo:') || lower.includes(' todo ')) return 'todo'
    if (lower.includes('bash')) return 'bash'
    if (lower.includes('shell')) return 'shell'
    if (lower.includes('grep')) return 'grep'
    if (lower.includes('glob')) return 'glob'
    if (lower.includes('read')) return 'read'
    if (lower.includes('write')) return 'write'
    if (lower.includes('edit')) return 'edit'
    if (lower.includes('question')) return 'question'
    if (lower.includes('search')) return 'search'
  }
  if (toolName) return toolName.toLowerCase()
  return 'unknown'
}

function mapToolState(status: string | undefined): 'pending' | 'running' | 'completed' | 'failed' | 'cancelled' {
  if (status === 'completed' || status === 'failed' || status === 'cancelled') return status
  if (status === 'in_progress' || status === 'running' || status === 'pending') return 'running'
  return 'pending'
}

function mapTerminalStatus(status: string | undefined): 'completed' | 'failed' | 'cancelled' | 'running' {
  if (status === 'completed' || status === 'failed' || status === 'cancelled') return status
  return 'running'
}

function isInputEvent(type: string): boolean {
  return type === 'session.input' || type === 'input'
}

function isAssistantTextEvent(type: string): boolean {
  return type === 'message.delta' || type === 'assistant_text'
}

function isAssistantReasoningEvent(type: string): boolean {
  return type === 'reasoning.delta' || type === 'assistant_reasoning'
}

function isToolEvent(type: string): boolean {
  return type === 'tool_call' || type === 'tool_call.started' || type === 'tool_call.updated' || type === 'tool_call.completed'
}

function isSessionClosedEvent(type: string): boolean {
  return type === 'session.closed' || type === 'session_closed'
}

function isLivenessEvent(type: string): boolean {
  return type === 'session.liveness' || type === 'status'
}

function defaultToolStatus(type: string): string {
  if (type === 'tool_call.completed') return 'completed'
  if (type === 'tool_call.started') return 'started'
  return 'running'
}

function toolRecord(payload: Record<string, unknown>): Record<string, unknown> {
  const nested = payload.toolCall
  return nested && typeof nested === 'object' && !Array.isArray(nested)
    ? nested as Record<string, unknown>
    : payload
}

function readToolString(payload: Record<string, unknown>, ...keys: string[]): string | undefined {
  const nested = toolRecord(payload)
  for (const key of keys) {
    const value = payload[key] ?? nested[key]
    if (typeof value === 'string' && value) return value
  }
  return undefined
}

function readToolValue(payload: Record<string, unknown>, ...keys: string[]): unknown {
  const nested = toolRecord(payload)
  for (const key of keys) {
    const value = payload[key] ?? nested[key]
    if (value !== undefined) return value
  }
  return undefined
}

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

function buildChatView(events: SessionEvent[]): SessionChatView {
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
  }

  if (current) {
    finalizeCurrent(lastTimestamp)
  } else if (events.length > 0) {
    turns.push(makeInitialTurn())
  }

  return { kind: 'chat', turns }
}

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
