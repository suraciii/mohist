import { request, withProject } from '../../../shared/api/client'

export interface LogTailResult {
  file: string
  cursor: number
  lines: string[]
  truncated: boolean
  reset: boolean
}

export interface WorkflowEvent {
  id: string
  projectId: string
  issueId: string | null
  issueNumber: number
  workflowRunId: string | null
  category: string
  type: string
  stage: string | null
  taskId: string | null
  checkName: string | null
  runnerId: string | null
  status: string | null
  message: string | null
  payload: unknown
  createdAt: string
}

export function getLogTail(cursor?: number, limit?: number, maxBytes?: number) {
  const search = new URLSearchParams()
  if (cursor != null) search.set('cursor', String(cursor))
  if (limit != null) search.set('limit', String(limit))
  if (maxBytes != null) search.set('maxBytes', String(maxBytes))
  const qs = search.toString()
  return request<LogTailResult>(`/logs/tail${qs ? `?${qs}` : ''}`)
}

export function getRecentEvents(params?: { projectId?: string | null; limit?: number }) {
  const search = new URLSearchParams()
  if (params?.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<WorkflowEvent[]>(`/events/recent${qs ? `?${qs}` : ''}`, withProject(undefined, params?.projectId))
}
