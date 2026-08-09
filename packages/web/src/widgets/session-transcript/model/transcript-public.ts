const SENSITIVE_KEYS = new Set([
  'command',
  'cwd',
  'details',
  'displaysubtitle',
  'file',
  'filepath',
  'input',
  'metadata',
  'memory',
  'oldpath',
  'output',
  'path',
  'prompt',
  'rawinput',
  'rawoutput',
  'system',
  'target',
  'workspace',
  'workdir',
])

function normalizedKey(key: string): string {
  return key.replaceAll('_', '').replaceAll('-', '').toLowerCase()
}

function isSensitiveKey(key: string): boolean {
  const normalized = normalizedKey(key)
  return SENSITIVE_KEYS.has(normalized)
    || normalized.includes('prompt')
    || normalized.includes('memory')
    || normalized.includes('workspace')
    || normalized.includes('rawinput')
    || normalized.includes('rawoutput')
}

function sanitizeText(value: string): string {
  return value
    .replace(/\[mohist-[^\]]+\][\s\S]*?\[\/mohist-[^\]]+\]/gi, '')
    .trim()
}

function sanitizeValue(value: unknown, key?: string): unknown {
  if (key && isSensitiveKey(key)) return undefined
  if (typeof value === 'string') return sanitizeText(value)
  if (Array.isArray(value)) return value.map((entry) => sanitizeValue(entry))
  if (!value || typeof value !== 'object') return value

  return Object.fromEntries(
    Object.entries(value as Record<string, unknown>)
      .filter(([entryKey]) => !isSensitiveKey(entryKey))
      .map(([entryKey, entryValue]) => [entryKey, sanitizeValue(entryValue, entryKey)])
      .filter(([, entryValue]) => entryValue !== undefined),
  )
}

const COMMON_KEYS = [
  'sequence',
  'createdAt',
  'recordedAt',
  'observedAt',
  'sourceId',
  'eventId',
  'sessionId',
  'runtimeSessionId',
  'runtime',
  'turnId',
  'inputId',
  'executionId',
  'messageId',
]

const EVENT_KEYS: Record<string, string[]> = {
  'session.input': ['text', 'kind', 'sentAt', 'acceptance'],
  'message.delta': ['text'],
  'coder_text_chunk': ['text'],
  'reasoning.delta': ['text'],
  'coder_thought_chunk': ['text'],
  'tool_call.started': ['toolName', 'title', 'state', 'toolCallId', 'normalizedName', 'displayTitle', 'category', 'status', 'error', 'failureReason', 'exitCode'],
  'tool_call.updated': ['toolName', 'title', 'state', 'toolCallId', 'normalizedName', 'displayTitle', 'category', 'status', 'error', 'failureReason', 'exitCode'],
  'tool_call.completed': ['toolName', 'title', 'state', 'toolCallId', 'normalizedName', 'displayTitle', 'category', 'status', 'error', 'failureReason', 'exitCode'],
  'coder_tool_call': ['toolName', 'title', 'state', 'toolCallId', 'normalizedName', 'displayTitle', 'category', 'status', 'error', 'failureReason', 'exitCode'],
  'session.activity': ['activity', 'status', 'failureReason', 'failureCategory'],
  'coder_recovery_status': ['status', 'reason', 'attempt'],
  'session.liveness': ['status', 'lastActivityType', 'failureReason'],
  'provider.retry': ['phase', 'attempt', 'maxAttempts', 'message'],
  'compaction': ['strategy', 'summary'],
  'compaction_event': ['strategy', 'summary'],
  'com.mohist.agent-session.context-compacted': ['strategy', 'summary'],
  'com.mohist.agent-session.context-exhausted': ['reason', 'summary'],
  'context_reset': ['reason', 'summary'],
  'session.context_reset': ['reason', 'summary'],
  'context_health_update': ['healthStatus'],
  'com.mohist.agent-session.context-health-updated': ['healthStatus'],
  'usage.updated': ['costAmount', 'costCurrency', 'contextWindowSize', 'contextWindowUsed', 'contextUsagePercent', 'healthStatus'],
  'model.resolved': ['resolvedModel'],
}

export function sanitizePublicAgentEvent(
  eventName: string,
  detail: Record<string, unknown>,
): Record<string, unknown> {
  const visible: Record<string, unknown> = { type: eventName }
  for (const key of [...COMMON_KEYS, ...(EVENT_KEYS[eventName] ?? [])]) {
    if (detail[key] === undefined || isSensitiveKey(key)) continue
    const value = sanitizeValue(detail[key], key)
    if (value !== undefined) visible[key] = value
  }
  return visible
}
