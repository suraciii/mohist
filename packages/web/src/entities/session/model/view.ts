import { buildChatView } from './view/chat'
import { buildCompactView } from './view/compact'
import { buildTimelineView } from './view/timeline'
import type { SessionEvent, SessionView, SessionViewKind } from './types'

export type {
  SessionChatPart,
  SessionChatTurn,
  SessionChatView,
  SessionCompactView,
  SessionEvent,
  SessionTimelineCompaction,
  SessionTimelineRecovery,
  SessionTimelineRound,
  SessionTimelineToolCall,
  SessionTimelineView,
  SessionView,
  SessionViewKind,
} from './types'

export function viewSessionEvents<K extends SessionViewKind>(
  events: SessionEvent[],
  kind: K,
): Extract<SessionView, { kind: K }> {
  if (kind === 'chat') return buildChatView(events) as Extract<SessionView, { kind: K }>
  if (kind === 'timeline') return buildTimelineView(events) as Extract<SessionView, { kind: K }>
  return buildCompactView(events) as Extract<SessionView, { kind: K }>
}
