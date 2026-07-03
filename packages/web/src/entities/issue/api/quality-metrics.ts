import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../../pages/insights/model/insights-range'

export interface StageReworkRateDto {
  stage: string
  enteredCount: number
  reworkRate: number | null
}

export interface QualityMetricsWindowDto {
  from: string
  to: string
  sampleCount: number
  firstTimeRightRate: number | null
  stages: StageReworkRateDto[]
}

export interface QualityTrendPointDto {
  boundary: string
  sampleCount: number
  firstTimeRightRate: number | null
  reworkRate: number | null
}

export interface QualityTrendDto {
  bucket: string
  from: string
  to: string
  points: QualityTrendPointDto[]
}

export interface QualityMetricsResponse {
  window7d: QualityMetricsWindowDto
  window30d: QualityMetricsWindowDto
  previousFirstTimeRightRate?: number | null
  previousSampleCount?: number
  trend?: QualityTrendDto
}

function buildQualityMetricsQueryString(range?: InsightsRange): string {
  if (!range) return ''
  return `?range=${range}`
}

export function fetchQualityMetrics(projectId: string, range?: InsightsRange) {
  return request<QualityMetricsResponse>(
    projectApiPath(projectId, `/issues/metrics/quality${buildQualityMetricsQueryString(range)}`),
  )
}

export const qualityMetricsQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['issues', 'metrics', 'quality', range, projectId] as const
      : ['issues', 'metrics', 'quality', range] as const
  }
  return projectId
    ? ['issues', 'metrics', 'quality', projectId] as const
    : ['issues', 'metrics', 'quality'] as const
}

export function useQualityMetrics(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: qualityMetricsQueryKey(projectId, range),
    queryFn: () => fetchQualityMetrics(projectId!, range),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}