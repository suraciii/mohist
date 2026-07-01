import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

export interface StageDurationMetricsWindow {
  from: string
  to: string
}

export interface StageDurationStageDto {
  stage: string
  sampleCount: number
  averageSeconds: number | null
  medianSeconds: number | null
}

export interface StageDurationWaitBreakoutDto {
  averageApprovalGateWaitSeconds: number | null
  averageInactiveGapSeconds: number | null
}

export interface StageDurationMetricsResponse {
  window: StageDurationMetricsWindow
  stages: StageDurationStageDto[]
  flowEfficiencyRatio: number | null
  waitBreakout: StageDurationWaitBreakoutDto | null
}

export function fetchStageDuration(projectId: string) {
  return request<StageDurationMetricsResponse>(
    projectApiPath(projectId, '/issues/metrics/stage-duration'),
  )
}

export const stageDurationQueryKey = (projectId?: string | null) =>
  projectId
    ? ['issues', 'metrics', 'stage-duration', projectId] as const
    : ['issues', 'metrics', 'stage-duration'] as const

export function useStageDuration() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: stageDurationQueryKey(projectId),
    queryFn: () => fetchStageDuration(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}