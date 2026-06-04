import { request } from '../../../shared/api/client'

export interface LogTailResult {
  file: string
  cursor: number
  lines: string[]
  truncated: boolean
  reset: boolean
}

export function getLogTail(cursor?: number, limit?: number, maxBytes?: number) {
  const search = new URLSearchParams()
  if (cursor != null) search.set('cursor', String(cursor))
  if (limit != null) search.set('limit', String(limit))
  if (maxBytes != null) search.set('maxBytes', String(maxBytes))
  const qs = search.toString()
  return request<LogTailResult>(`/logs/tail${qs ? `?${qs}` : ''}`)
}
