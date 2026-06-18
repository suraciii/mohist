export type DeliveryFailureKind = 'conflict' | 'base-moved' | 'retry-safe'

export interface DeliveryFailureGuidance {
  failureKind: DeliveryFailureKind
  label: string
  nextAction: string
}

export const DELIVERY_FAILURE_KINDS: readonly DeliveryFailureKind[] = [
  'conflict',
  'base-moved',
  'retry-safe',
] as const

const DELIVERY_FAILURE_GUIDANCE: Record<DeliveryFailureKind, DeliveryFailureGuidance> = {
  conflict: {
    failureKind: 'conflict',
    label: 'Conflict needs attention',
    nextAction: 'Conflicts could not be resolved automatically. Inspect the conflicting files, resolve them on the issue branch, and rerun prepare.',
  },
  'base-moved': {
    failureKind: 'base-moved',
    label: 'Base branch moved',
    nextAction: 'The base branch moved during publish. Prepare the branch again, then publish.',
  },
  'retry-safe': {
    failureKind: 'retry-safe',
    label: 'Transient failure',
    nextAction: 'Retry the task — the failure is unrelated to conflicts or base movement.',
  },
}

export function isDeliveryFailureKind(value: unknown): value is DeliveryFailureKind {
  return value === 'conflict' || value === 'base-moved' || value === 'retry-safe'
}

export function getDeliveryFailureGuidance(kind: DeliveryFailureKind): DeliveryFailureGuidance {
  return DELIVERY_FAILURE_GUIDANCE[kind]
}

export interface DeliveryFailureResolution {
  failureKind: DeliveryFailureKind | null
  guidance: DeliveryFailureGuidance | null
}

export function resolveDeliveryFailureFromOutput(
  output: unknown,
): DeliveryFailureResolution {
  if (output == null) return { failureKind: null, guidance: null }
  const candidate = extractFailureKindCandidate(output)
  if (!candidate) return { failureKind: null, guidance: null }
  return resolveDeliveryFailure(candidate)
}

export function resolveDeliveryFailureFromMessage(
  message: string | null | undefined,
): DeliveryFailureResolution {
  if (!message) return { failureKind: null, guidance: null }
  const match = message.match(/\((conflict|base-moved|retry-safe)\)/)
  if (!match) return { failureKind: null, guidance: null }
  return resolveDeliveryFailure(match[1])
}

function extractFailureKindCandidate(value: unknown): unknown {
  if (value == null) return null
  if (typeof value === 'string') {
    const trimmed = value.trim()
    if (!trimmed) return null
    if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
      try {
        return extractFailureKindCandidate(JSON.parse(trimmed))
      } catch {
        // fall through and try the regex
      }
    }
    const match = trimmed.match(/\((conflict|base-moved|retry-safe)\)/i)
    if (match && isDeliveryFailureKind(match[1])) return match[1]
    return null
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      const found = extractFailureKindCandidate(item)
      if (found != null) return found
    }
    return null
  }
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>
    const direct = record.failureKind ?? record.FailureKind
    if (isDeliveryFailureKind(direct)) return direct
    if ('output' in record) {
      const fromOutput = extractFailureKindCandidate(record.output)
      if (fromOutput != null) return fromOutput
    }
    if ('message' in record) {
      const fromMessage = extractFailureKindCandidate(record.message)
      if (fromMessage != null) return fromMessage
    }
  }
  return null
}

function resolveDeliveryFailure(candidate: unknown): DeliveryFailureResolution {
  if (typeof candidate !== 'string' || !isDeliveryFailureKind(candidate)) {
    return { failureKind: null, guidance: null }
  }
  return {
    failureKind: candidate,
    guidance: DELIVERY_FAILURE_GUIDANCE[candidate],
  }
}
