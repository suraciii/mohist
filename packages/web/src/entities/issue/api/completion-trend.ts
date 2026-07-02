import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

export interface CompletionBucketPoint {
  boundary: string
  completed: number
  failed: number
}

export interface CompletionTotalDto {
  completed: number
  failed: number
  sampleCount: number
}

export interface CompletionTrendResponse {
  bucket: string
  window: { from: string; to: string }
  buckets: CompletionBucketPoint[]
  currentTotal?: CompletionTotalDto
  previousTotal?: CompletionTotalDto
}

type CompletionBucket = 'day' | 'week'

export function fetchCompletionTrend(projectId: string, bucket: CompletionBucket = 'week') {
  return request<CompletionTrendResponse>(
    projectApiPath(projectId, `/issues/metrics/completion?bucket=${bucket}`),
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

export function useCompletionThroughput() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['issues', 'metrics', 'completion', 'day', projectId],
    queryFn: () => fetchCompletionTrend(projectId!, 'day'),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
