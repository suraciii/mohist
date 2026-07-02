import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

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

export function fetchQualityMetrics(projectId: string) {
  return request<QualityMetricsResponse>(
    projectApiPath(projectId, '/issues/metrics/quality'),
  )
}

export const qualityMetricsQueryKey = (projectId?: string | null) =>
  projectId
    ? ['issues', 'metrics', 'quality', projectId] as const
    : ['issues', 'metrics', 'quality'] as const

export function useQualityMetrics() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: qualityMetricsQueryKey(projectId),
    queryFn: () => fetchQualityMetrics(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
