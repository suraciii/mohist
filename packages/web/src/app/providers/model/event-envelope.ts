import { AGENT_DETAIL_EVENTS } from '../../../entities/agent'
import type { AgentDetailEventMap } from '../../../entities/agent'

type AgentDetailEventName = keyof AgentDetailEventMap

export function isAgentDetailEvent(name: string): name is AgentDetailEventName {
  return (AGENT_DETAIL_EVENTS as readonly string[]).includes(name)
}

export function routeTranscriptEventName(name: string): string {
  switch (name) {
    case 'message.delta':
      return 'coder_text_chunk'
    case 'reasoning.delta':
      return 'coder_thought_chunk'
    case 'tool_call.started':
    case 'tool_call.updated':
    case 'tool_call.completed':
      return 'coder_tool_call'
    default:
      return name
  }
}

/**
 * Wire shape from the SignalR bus. The server now sends the full CloudEvents
 * 1.0.2 envelope; the Web reads {@link payload} for the original event body
 * and merges {@link extensions} routing metadata (projectid, issue, epic,
 * workflowrunid, stage, agentid, sessionid, runnerid). The user-visible
 * issue number rides under the `issue` key. Falls back to the
 * legacy raw-payload shape (where the event body sits in a top-level
 * `payload` field) for any unmigrated producers.
 *
 * Note on field casing: the server-side `CloudEventEnvelope` record uses
 * PascalCase property names (SpecVersion, DataContentType, ...) when
 * serialised by System.Text.Json, so the wire JSON has `specVersion`,
 * not the CloudEvents-spec lowercase `specversion`. The structural
 * check here matches what the server actually emits.
 */
function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function mergeRoutingLineage(
  payload: Record<string, unknown>,
  extensions: Record<string, unknown> | null,
): Record<string, unknown> {
  let normalized = payload
  const extensionProjectId = extensions?.projectid
  if (isNonEmptyString(extensionProjectId) && payload.projectId !== extensionProjectId) {
    normalized = { ...normalized, projectId: extensionProjectId }
  }

  const extensionIssue = extensions?.issue
  if (isNonEmptyString(extensionIssue)) {
    const issueNumber = Number(extensionIssue)
    if (Number.isSafeInteger(issueNumber) && issueNumber > 0 && normalized.issueNumber !== issueNumber) {
      normalized = { ...normalized, issueNumber }
    }
  }
  const extensionSessionId = extensions?.sessionid
  if (isNonEmptyString(extensionSessionId) && normalized.sessionId !== extensionSessionId) {
    normalized = { ...normalized, sessionId: extensionSessionId }
  }
  return normalized
}

export function unwrapEnvelope(rawData: unknown): Record<string, unknown> {
  if (!rawData || typeof rawData !== 'object') {
    return {}
  }
  const candidate = rawData as Record<string, unknown>
  // CloudEvents envelope marker: id + source + type + specVersion all
  // present as strings. duck-typing on 'payload' alone would mis-parse
  // any future event whose data payload happens to contain a nested
  // 'payload' field.
  if (
    typeof candidate.specVersion === 'string'
    && typeof candidate.id === 'string'
    && typeof candidate.source === 'string'
    && typeof candidate.type === 'string'
  ) {
    const payload = candidate.payload ?? candidate.data
    if (payload && typeof payload === 'object') {
      return mergeRoutingLineage(payload as Record<string, unknown>, asRecord(candidate.extensions))
    }
    return {}
  }
  // Legacy raw-payload shape (unmigrated producers).
  if (typeof candidate.type === 'string' && 'payload' in candidate) {
    const payload = candidate.payload
    if (payload && typeof payload === 'object') {
      return payload as Record<string, unknown>
    }
    return {}
  }
  return candidate
}

export function readEnvelopeField(candidate: Record<string, unknown>, camelCase: string, pascalCase: string): unknown {
  return candidate[camelCase] ?? candidate[pascalCase]
}

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

export function normalizeToolState(value: unknown, eventName: string): string | undefined {
  if (typeof value === 'string' && value) {
    switch (value) {
      case 'completed':
      case 'failed':
      case 'timeout':
      case 'cancelled':
      case 'started':
        return value
      case 'running':
      case 'in_progress':
      case 'pending':
        return 'started'
      default:
        return value
    }
  }
  if (eventName === 'tool_call.completed') return 'completed'
  if (eventName === 'tool_call.started') return 'started'
  return undefined
}

export function normalizeTranscriptDetail(
  candidate: Record<string, unknown>,
  eventName: string,
  innerPayload?: Record<string, unknown>,
): Record<string, unknown> {
  const runtimeSessionId = readEnvelopeField(candidate, 'runtimeSessionId', 'RuntimeSessionId')
    ?? readEnvelopeField(candidate, 'agentSessionId', 'AgentSessionId')
    ?? (innerPayload && readEnvelopeField(innerPayload, 'runtimeSessionId', 'RuntimeSessionId'))
  const runtime = readEnvelopeField(candidate, 'runtime', 'Runtime')
    ?? (innerPayload && readEnvelopeField(innerPayload, 'runtime', 'Runtime'))
  const sessionId = readEnvelopeField(candidate, 'sessionId', 'SessionId')
    ?? (innerPayload && readEnvelopeField(innerPayload, 'sessionId', 'SessionId'))
  const workId = readEnvelopeField(candidate, 'workId', 'WorkId')
  const normalized: Record<string, unknown> = {
    ...candidate,
    ...(innerPayload ?? {}),
    type: eventName,
  }
  const toolCall = asRecord(normalized.toolCall)
  if (toolCall) {
    normalized.toolCallId ??= toolCall.toolCallId ?? toolCall.id
    normalized.toolName ??= toolCall.toolName ?? toolCall.name
    normalized.title ??= toolCall.title
    normalized.rawInput ??= toolCall.input ?? toolCall.rawInput
    normalized.rawOutput ??= toolCall.output ?? toolCall.rawOutput
    normalized.rawOutputMetadata ??= toolCall.outputMetadata ?? toolCall.rawOutputMetadata
    normalized.metadata ??= toolCall.metadata
    normalized.details ??= toolCall.details
    normalized.normalizedName ??= toolCall.normalizedName
    normalized.displayTitle ??= toolCall.displayTitle
    normalized.displaySubtitle ??= toolCall.displaySubtitle
    normalized.category ??= toolCall.category
  }
  if (eventName.startsWith('tool_call.')) {
    normalized.state = normalizeToolState(
      normalized.state ?? normalized.status ?? toolCall?.state ?? toolCall?.status,
      eventName,
    )
  }
  if (innerPayload) {
    normalized.payload = innerPayload
  }
  if (normalized.runtimeSessionId === undefined && runtimeSessionId !== undefined) {
    normalized.runtimeSessionId = runtimeSessionId
  }
  if (normalized.runtime === undefined && runtime !== undefined) {
    normalized.runtime = runtime
  }
  if (normalized.sessionId === undefined) {
    normalized.sessionId = sessionId
  }
  if (normalized.executionId === undefined) {
    normalized.executionId = workId
  }
  return normalized
}

export function unwrapTranscriptEnvelope(rawData: unknown): { eventName: string; payload: unknown; detail: unknown } | null {
  if (!rawData || typeof rawData !== 'object') {
    return null
  }
  const candidate = rawData as Record<string, unknown>
  const eventName = readEnvelopeField(candidate, 'type', 'Type')
    ?? readEnvelopeField(candidate, 'eventType', 'EventType')
    ?? readEnvelopeField(candidate, 'name', 'Name')
  if (typeof eventName !== 'string') {
    return null
  }
  const innerPayload = readEnvelopeField(candidate, 'payload', 'Payload') ?? readEnvelopeField(candidate, 'data', 'Data')
  const hasRuntimeRowMetadata = readEnvelopeField(candidate, 'sessionId', 'SessionId') !== undefined
    || readEnvelopeField(candidate, 'sequence', 'Sequence') !== undefined
    || readEnvelopeField(candidate, 'createdAt', 'CreatedAt') !== undefined
  if (hasRuntimeRowMetadata && innerPayload && typeof innerPayload === 'object') {
    const payload = innerPayload as Record<string, unknown>
    return {
      eventName,
      payload,
      detail: normalizeTranscriptDetail(candidate, eventName, payload),
    }
  }
  return {
    eventName,
    payload: candidate,
    detail: normalizeTranscriptDetail(candidate, eventName),
  }
}
