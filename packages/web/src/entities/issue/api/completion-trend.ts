import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../../pages/insights/model/insights-range'

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

function buildCompletionTrendQueryString(bucket: CompletionBucket, range?: InsightsRange): string {
  const params = new URLSearchParams({ bucket })
  if (range) params.set('range', range)
  const qs = params.toString()
  return qs ? `?${qs}` : ''
}

export function fetchCompletionTrend(projectId: string, bucket: CompletionBucket = 'week', range?: InsightsRange) {
  return request<CompletionTrendResponse>(
    projectApiPath(projectId, `/issues/metrics/completion${buildCompletionTrendQueryString(bucket, range)}`),
  )
}

export function useCompletionTrend(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: range
      ? ['issues', 'metrics', 'completion', 'week', range, projectId]
      : ['issues', 'metrics', 'completion', 'week', projectId],
    queryFn: () => fetchCompletionTrend(projectId!, 'week', range),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}

export function useCompletionThroughput(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: range
      ? ['issues', 'metrics', 'completion', 'day', range, projectId]
      : ['issues', 'metrics', 'completion', 'day', projectId],
    queryFn: () => fetchCompletionTrend(projectId!, 'day', range),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}