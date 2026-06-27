import type { QueryClient } from '@tanstack/react-query'
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

export const approvalWaitQueryKey = (projectId?: string | null) =>
  projectId
    ? ['issues', 'metrics', 'approval-wait', projectId] as const
    : ['issues', 'metrics', 'approval-wait'] as const

export function invalidateApprovalWait(queryClient: QueryClient) {
  queryClient.invalidateQueries({ queryKey: approvalWaitQueryKey() })
}

export function useApprovalWait() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: approvalWaitQueryKey(projectId),
    queryFn: () => fetchApprovalWait(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
