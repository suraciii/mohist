import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

export interface ApprovalWaitMetricsWindow {
  from: string
  to: string
}

export interface ApprovalWaitMetricsResponse {
  window: ApprovalWaitMetricsWindow
  sampleCount: number
  averageSeconds: number | null
  medianSeconds: number | null
  maxSeconds: number | null
}

export function fetchApprovalWait(projectId: string) {
  return request<ApprovalWaitMetricsResponse>(
    projectApiPath(projectId, '/issues/metrics/approval-wait'),
  )
}

export function useApprovalWait() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', 'metrics', 'approval-wait', projectId],
    queryFn: () => fetchApprovalWait(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
