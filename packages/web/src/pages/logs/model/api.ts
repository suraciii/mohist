import { request } from '../../../shared/api/client'

export interface LogEntry {
  level: string | null
  time: string | null
  service: string | null
  message: string
  raw: string
}

export interface LogTailResult {
  lines: LogEntry[]
  cursor: number | null
  nextCursor: number | null
  source: string | null
  truncated: boolean
  reset: boolean
  unavailable: boolean
  expectedLocation: string | null
  reason: string | null
}

export function getLogTail(cursor?: number, limit?: number, maxBytes?: number) {
  const search = new URLSearchParams()
  if (cursor != null) search.set('cursor', String(cursor))
  if (limit != null) search.set('limit', String(limit))
  if (maxBytes != null) search.set('maxBytes', String(maxBytes))
  const qs = search.toString()
  return request<LogTailResult>(`/logs/tail${qs ? `?${qs}` : ''}`)
}