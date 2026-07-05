import { buildChatView } from './view/chat'
import { buildCompactView } from './view/compact'
import { buildTimelineView } from './view/timeline'

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

export function viewSessionEvents<K extends SessionViewKind>(
  events: SessionEvent[],
  kind: K,
): Extract<SessionView, { kind: K }> {
  if (kind === 'chat') return buildChatView(events) as Extract<SessionView, { kind: K }>
  if (kind === 'timeline') return buildTimelineView(events) as Extract<SessionView, { kind: K }>
  return buildCompactView(events) as Extract<SessionView, { kind: K }>
}
