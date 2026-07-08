import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../shared/insights-range'

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

export const completionTrendQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['issues', 'metrics', 'completion', 'week', range, projectId] as const
      : ['issues', 'metrics', 'completion', 'week', range] as const
  }
  return projectId
    ? ['issues', 'metrics', 'completion', 'week', projectId] as const
    : ['issues', 'metrics', 'completion', 'week'] as const
}

export const completionThroughputQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['issues', 'metrics', 'completion', 'day', range, projectId] as const
      : ['issues', 'metrics', 'completion', 'day', range] as const
  }
  return projectId
    ? ['issues', 'metrics', 'completion', 'day', projectId] as const
    : ['issues', 'metrics', 'completion', 'day'] as const
}

export function completionTrendQueryOptions(projectId: string | null | undefined, range?: InsightsRange) {
  return {
    queryKey: completionTrendQueryKey(projectId, range),
    queryFn: () => fetchCompletionTrend(projectId!, 'week', range),
    enabled: !!projectId,
    staleTime: 60_000,
  } as const
}

export function completionThroughputQueryOptions(projectId: string | null | undefined, range?: InsightsRange) {
  return {
    queryKey: completionThroughputQueryKey(projectId, range),
    queryFn: () => fetchCompletionTrend(projectId!, 'day', range),
    enabled: !!projectId,
    staleTime: 60_000,
  } as const
}

export function useCompletionTrend(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery(completionTrendQueryOptions(projectId, range))
}

export function useCompletionThroughput(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery(completionThroughputQueryOptions(projectId, range))
}