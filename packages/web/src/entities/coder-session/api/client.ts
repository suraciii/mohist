import { request, withProject } from '../../../shared/api/client'
import type { CoderSessionDetail, CoderSessionSummary, WorkflowLogItem } from '../model/types'

export function getCoderSessions(number: number, projectId?: string | null) {
  return request<CoderSessionSummary[]>(`/issues/${number}/coder-sessions`, withProject(undefined, projectId))
}

export function getCoderSessionDetail(number: number, sessionId: string, projectId?: string | null) {
  return request<CoderSessionDetail>(`/issues/${number}/coder-sessions/${sessionId}`, withProject(undefined, projectId))
}

export function getWorkflowLogs(number: number, projectId?: string | null) {
  return request<WorkflowLogItem[]>(`/issues/${number}/logs`, withProject(undefined, projectId))
}
