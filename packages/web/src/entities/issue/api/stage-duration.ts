import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../../pages/insights/model/insights-range'

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

function buildStageDurationQueryString(range?: InsightsRange): string {
  if (!range) return ''
  return `?range=${range}`
}

export function fetchStageDuration(projectId: string, range?: InsightsRange) {
  return request<StageDurationMetricsResponse>(
    projectApiPath(projectId, `/issues/metrics/stage-duration${buildStageDurationQueryString(range)}`),
  )
}

export const stageDurationQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['issues', 'metrics', 'stage-duration', range, projectId] as const
      : ['issues', 'metrics', 'stage-duration', range] as const
  }
  return projectId
    ? ['issues', 'metrics', 'stage-duration', projectId] as const
    : ['issues', 'metrics', 'stage-duration'] as const
}

export function useStageDuration(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: stageDurationQueryKey(projectId, range),
    queryFn: () => fetchStageDuration(projectId!, range),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}