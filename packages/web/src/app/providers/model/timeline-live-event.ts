import type { TimelineLiveEvent } from '../../../entities/issue'

export function readIssueNumber(parsed: Record<string, unknown>): number | null {
  const issueNumber = parsed.issueNumber
  return typeof issueNumber === 'number' ? issueNumber : null
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}

function readEnvelopePayload(rawData: unknown, parsed: Record<string, unknown>): Record<string, unknown> {
  const envelope = asRecord(rawData)
  if (!envelope || envelope.specversion !== '1.0') return parsed

  return asRecord(envelope.data) ?? parsed
}

function readEnvelopeIssueNumber(rawData: unknown): number | null {
  const envelope = asRecord(rawData)
  const issue = envelope?.issue
  if (typeof issue !== 'string' || issue.trim() === '') return null

  const issueNumber = Number(issue)
  return Number.isSafeInteger(issueNumber) && issueNumber > 0 ? issueNumber : null
}

export function readTimelineEventId(rawData: unknown): string | null {
  if (!rawData || typeof rawData !== 'object') return null
  const candidate = rawData as Record<string, unknown>
  const id = candidate.id ?? candidate.eventId
  return typeof id === 'string' && id ? id : null
}

export function readTimelineTime(rawData: unknown, parsed: Record<string, unknown>): string | null {
  if (rawData && typeof rawData === 'object') {
    const candidate = rawData as Record<string, unknown>
    const t = candidate.time ?? candidate.Time
    if (typeof t === 'string' && t) return t
  }
  const fallback = parsed.time ?? parsed.createdAt ?? parsed.createdAtUtc ?? parsed.timestamp
  return typeof fallback === 'string' && fallback ? fallback : null
}

export function buildTimelineLiveEvent(
  eventName: string,
  rawData: unknown,
  parsed: Record<string, unknown>,
): TimelineLiveEvent {
  const payload = readEnvelopePayload(rawData, parsed)
  return {
    issueNumber: readEnvelopeIssueNumber(rawData) ?? readIssueNumber(payload),
    type: eventName,
    time: readTimelineTime(rawData, payload),
    eventId: readTimelineEventId(rawData),
    payload,
  }
}
