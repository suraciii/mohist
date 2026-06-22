import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

export interface CompletionBucketPoint {
  boundary: string
  completed: number
  failed: number
}

export interface CompletionTrendResponse {
  bucket: string
  window: { from: string; to: string }
  buckets: CompletionBucketPoint[]
}

export function fetchCompletionTrend(projectId: string) {
  return request<CompletionTrendResponse>(
    projectApiPath(projectId, '/issues/metrics/completion?bucket=week'),
  )
}

export function useCompletionTrend() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', 'metrics', 'completion', 'week', projectId],
    queryFn: () => fetchCompletionTrend(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
