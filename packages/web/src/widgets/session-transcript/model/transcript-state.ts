import type { SessionTurn, TextPart, ReasoningPart, ErrorPart, PromptKind } from '../../../entities/coder-session'

function generateId(): string {
  return Math.random().toString(36).slice(2, 11)
}

export function createTextPart(text: string, startedAt: string): TextPart {
  return { id: generateId(), type: 'text', text, startedAt, completedAt: null }
}

export function createReasoningPart(text: string, startedAt: string): ReasoningPart {
  return { id: generateId(), type: 'reasoning', text, startedAt, completedAt: null }
}

export function createErrorPart(message: string, kind: ErrorPart['kind'], at: string): ErrorPart {
  return { id: generateId(), type: 'error', message, kind, at }
}

export function normalizePromptKind(kind?: string): PromptKind {
  switch (kind) {
    case 'initial':
    case 'task':
    case 'retry':
    case 'followup':
    case 'recovery':
      return kind
    default:
      return 'legacy-missing'
  }
}

export function createTemporaryTurn(at: string): SessionTurn {
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

export function createInputTurn(detail: {
  text: string
  kind?: string
  sentAt?: string
}): SessionTurn {
  const sentAt = detail.sentAt ?? new Date().toISOString()
  return {
    id: `live-${generateId()}`,
    startedAt: sentAt,
    completedAt: null,
    incomplete: true,
    user: {
      role: 'mohist',
      text: detail.text,
      kind: normalizePromptKind(detail.kind),
      sentAt,
    },
    assistant: [],
  }
}

export function ensureLiveTurn(turns: SessionTurn[], at: string): SessionTurn[] {
  return turns.length > 0 ? [...turns] : [createTemporaryTurn(at)]
}

export function appendInputTurn(turns: SessionTurn[], detail: { text: string; kind?: string; sentAt?: string }): SessionTurn[] {
  const next = [...turns]
  const sentAt = detail.sentAt ?? new Date().toISOString()
  const lastTurn = next[next.length - 1]
  if (
    lastTurn
    && lastTurn.user.text === detail.text
    && lastTurn.assistant.length === 0
    && lastTurn.completedAt === null
  ) {
    next[next.length - 1] = {
      ...lastTurn,
      startedAt: lastTurn.startedAt ?? sentAt,
      user: {
        ...lastTurn.user,
        kind: detail.kind ? normalizePromptKind(detail.kind) : lastTurn.user.kind,
        sentAt,
      },
    }
    return next
  }
  if (lastTurn && lastTurn.completedAt === null && lastTurn.assistant.length > 0) {
    next[next.length - 1] = {
      ...lastTurn,
      completedAt: sentAt,
      incomplete: false,
    }
  }
  next.push(createInputTurn({ ...detail, sentAt }))
  return next
}

export function closeLatestTurn(turns: SessionTurn[], completedAt: string): SessionTurn[] {
  const next = ensureLiveTurn(turns, completedAt)
  const lastTurn = next[next.length - 1]
  const closedAssistant = lastTurn.assistant.map((part) => {
    if (part.type === 'text' && part.completedAt === null) {
      return { ...part, completedAt }
    }
    if (part.type === 'reasoning' && part.completedAt === null) {
      return { ...part, completedAt }
    }
    return part
  })
  next[next.length - 1] = {
    ...lastTurn,
    assistant: closedAssistant,
    completedAt,
    incomplete: false,
  }
  return next
}

export function appendTextToTurn(turn: SessionTurn, text: string): SessionTurn {
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

export function closeActiveTextPart(turn: SessionTurn, completedAt: string): SessionTurn {
  const textIndex = turn.assistant.findIndex((p): p is TextPart => p.type === 'text' && p.completedAt === null)
  if (textIndex >= 0) {
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) =>
        i === textIndex ? { ...(p as TextPart), completedAt } : p,
      ),
    }
  }
  return turn
}

export function appendReasoningToTurn(turn: SessionTurn, text: string): SessionTurn {
  const now = new Date().toISOString()
  const existingReasoningIndex = turn.assistant.findIndex(
    (p): p is ReasoningPart => p.type === 'reasoning' && p.completedAt === null,
  )

  if (existingReasoningIndex >= 0) {
    const existing = turn.assistant[existingReasoningIndex] as ReasoningPart
    const updated: ReasoningPart = { ...existing, text: existing.text + text }
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) => (i === existingReasoningIndex ? updated : p)),
    }
  }

  return {
    ...turn,
    assistant: [...turn.assistant, createReasoningPart(text, now)],
  }
}

export {
  asPayloadRecord,
  asRecord,
  getNumber,
  getString,
  truncatePreview,
} from './transcript-payload'

export {
  buildLiveToolDetails,
  createToolPart,
  deriveToolTarget,
  findToolByCorrelation,
  getDisplayFields,
  getNormalizedName,
  isTerminalState,
  mapStatusToDisplay,
  updateToolInTurn,
  type LiveToolCall,
} from './transcript-tool-state'
