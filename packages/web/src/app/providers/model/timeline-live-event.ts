import type { TimelineLiveEvent } from '../../../entities/issue'

export function readIssueNumber(parsed: Record<string, unknown>): number | null {
  const issueNumber = parsed.issueNumber ?? parsed.issueNo ?? parsed.number
  return typeof issueNumber === 'number' ? issueNumber : null
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
  return {
    issueNumber: readIssueNumber(parsed),
    issueId: typeof parsed.issueId === 'string' ? parsed.issueId : null,
    type: eventName,
    time: readTimelineTime(rawData, parsed),
    eventId: readTimelineEventId(rawData),
    payload: parsed,
  }
}
