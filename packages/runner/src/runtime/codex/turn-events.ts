import { redactCodexCredentialString } from './credential.js'
import type { CodexDiagnostic } from './types.js'

/**
 * Build the diagnostic for an item event whose exact Thread or Turn
 * identity does not match the active Turn, or `null` when the event
 * belongs to it. Item events that omit identity are treated as
 * belonging to the active Turn (the internal projection form); the
 * wire form produced by {@link normalizeCodexNotification} always
 * carries both IDs, so exact-ID routing is enforced on real traffic.
 */
export function staleItemDiagnostic(
  event: unknown,
  activeThreadId: string,
  activeTurnId: string | null,
): CodexDiagnostic | null {
  if (!event || typeof event !== 'object') return null
  const view = event as { type?: unknown; threadId?: unknown; turnId?: unknown }
  const staleThread = typeof view.threadId === 'string' && view.threadId !== activeThreadId
  const staleTurn = typeof view.turnId === 'string' && view.turnId !== activeTurnId
  if (!staleThread && !staleTurn) return null
  return {
    severity: 'info',
    code: 'item-stale',
    message: redactCodexCredentialString(
      `ignored Codex ${typeof view.type === 'string' ? view.type : 'item'} for thread=${String(view.threadId)} turn=${String(view.turnId)}; expected thread=${activeThreadId} turn=${String(activeTurnId)}`,
    ),
  }
}

/** Normalize official Codex app-server notifications into the small internal event subset. */
export function normalizeCodexNotification(message: unknown): unknown | null {
  if (!message || typeof message !== 'object') return null
  const envelope = message as { method?: unknown; params?: unknown }
  if (typeof envelope.method !== 'string' || !envelope.params || typeof envelope.params !== 'object') return null
  const params = envelope.params as Record<string, unknown>
  const threadId = typeof params.threadId === 'string' ? params.threadId : null
  const turnId = typeof params.turnId === 'string' ? params.turnId : null
  if (envelope.method === 'turn/completed') {
    const turn = params.turn && typeof params.turn === 'object' ? (params.turn as Record<string, unknown>) : params
    const id = typeof turn.id === 'string' ? turn.id : turnId
    const status = typeof turn.status === 'string' ? turn.status : null
    if (threadId && id && (status === 'completed' || status === 'failed' || status === 'interrupted')) {
      return {
        type: 'turn/completed',
        threadId,
        turnId: id,
        status,
        ...(turn.error && typeof turn.error === 'object' ? { error: turn.error } : {}),
      }
    }
  }
  if (envelope.method === 'thread/status/changed' || envelope.method === 'thread/status') {
    const rawStatus =
      typeof params.status === 'string'
        ? params.status
        : params.status && typeof params.status === 'object'
          ? (params.status as { type?: unknown }).type
          : params.thread && typeof params.thread === 'object'
            ? (params.thread as { status?: unknown }).status
            : null
    if (threadId && typeof rawStatus === 'string')
      return { type: 'thread/status', threadId, ...(turnId ? { turnId } : {}), status: rawStatus }
  }
  if (envelope.method === 'item/started' || envelope.method === 'item/completed') {
    const item = params.item && typeof params.item === 'object' ? (params.item as Record<string, unknown>) : null
    const itemType = item && typeof item.type === 'string' ? item.type : null
    if (!threadId || !turnId || !itemType) return null
    return projectOfficialItem(itemType, item!, threadId, turnId)
  }
  if (envelope.method === 'item/agentMessage/delta') {
    const delta = typeof params.delta === 'string' ? params.delta : typeof params.text === 'string' ? params.text : null
    return threadId && turnId && delta !== null
      ? { type: 'agentMessage', threadId, turnId, text: delta, delta: true }
      : null
  }
  if (envelope.method === 'item/reasoning/summaryTextDelta' || envelope.method === 'item/reasoning/textDelta') {
    const delta = typeof params.delta === 'string' ? params.delta : typeof params.text === 'string' ? params.text : null
    return threadId && turnId && delta !== null ? { type: 'reasoning', threadId, turnId, summary: delta } : null
  }
  if (envelope.method === 'item/commandExecution/outputDelta') {
    return threadId && turnId ? { type: 'commandExecution', threadId, turnId, command: '', status: 'inProgress' } : null
  }
  if (envelope.method === 'item/fileChange/patchUpdated') {
    const changes = Array.isArray(params.changes) ? params.changes : []
    const first = changes[0] && typeof changes[0] === 'object' ? (changes[0] as Record<string, unknown>) : null
    return threadId && turnId && typeof first?.path === 'string'
      ? {
          type: 'fileChange',
          threadId,
          turnId,
          path: first.path,
          kind: first.kind === 'create' || first.kind === 'delete' ? first.kind : 'modify',
        }
      : null
  }
  if (envelope.method === 'item/mcpToolCall/progress') {
    return threadId && turnId ? { type: 'mcpToolCall', threadId, turnId, tool: '', status: 'inProgress' } : null
  }
  if (envelope.method === 'contextCompacted') {
    return threadId && turnId ? { type: 'contextCompaction', threadId, turnId } : null
  }
  return null
}

function projectOfficialItem(type: string, item: Record<string, unknown>, threadId: string, turnId: string): unknown {
  if (type === 'agentMessage')
    return { type, threadId, turnId, text: typeof item.text === 'string' ? item.text : '', delta: false }
  if (type === 'reasoning')
    return { type, threadId, turnId, summary: typeof item.summary === 'string' ? item.summary : '' }
  if (type === 'commandExecution') {
    return {
      type,
      threadId,
      turnId,
      command: typeof item.command === 'string' ? item.command : '',
      status: typeof item.status === 'string' ? item.status : 'unknown',
    }
  }
  if (type === 'fileChange') {
    const changes = Array.isArray(item.changes) ? item.changes : []
    const first = changes[0] && typeof changes[0] === 'object' ? (changes[0] as Record<string, unknown>) : null
    return {
      type,
      threadId,
      turnId,
      path: typeof first?.path === 'string' ? first.path : '',
      kind: first?.kind === 'create' || first?.kind === 'delete' ? first.kind : 'modify',
    }
  }
  if (type === 'contextCompaction') return { type, threadId, turnId }
  if (type === 'usage') {
    return {
      type,
      threadId,
      turnId,
      inputTokens: typeof item.inputTokens === 'number' ? item.inputTokens : 0,
      outputTokens: typeof item.outputTokens === 'number' ? item.outputTokens : 0,
    }
  }
  if (type === 'mcpToolCall')
    return {
      type,
      threadId,
      turnId,
      tool: typeof item.name === 'string' ? item.name : '',
      status: typeof item.status === 'string' ? item.status : 'unknown',
    }
  return { type, threadId, turnId, payload: item }
}
