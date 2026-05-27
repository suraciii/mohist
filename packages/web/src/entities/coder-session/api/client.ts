import { request, withProject } from '../../../shared/api/client'
import type { CoderSessionDetail, CoderSessionSummary, WorkflowLogItem } from '../model/types'

export function getCoderSessions(number: number, projectId?: string | null) {
  return request<CoderSessionSummary[]>(withProject(`/issues/${number}/coder-sessions`, projectId))
}

export function getCoderSessionDetail(number: number, sessionId: string, projectId?: string | null) {
  return request<CoderSessionDetail>(withProject(`/issues/${number}/coder-sessions/${sessionId}`, projectId))
}

export function getWorkflowLogs(number: number, projectId?: string | null) {
  return request<WorkflowLogItem[]>(withProject(`/issues/${number}/logs`, projectId))
}
