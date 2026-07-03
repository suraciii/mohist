import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../../pages/insights/model/insights-range'

export interface CumulativeFlowDayDto {
  day: string
  backlog: number
  plan: number
  build: number
  check: number
  integrate: number
  done: number
}

export interface CumulativeFlowResponse {
  snapshots: CumulativeFlowDayDto[]
  rangeFrom: string
  rangeTo: string
}

function buildCumulativeFlowQueryString(range?: InsightsRange): string {
  if (!range) return ''
  return `?range=${range}`
}

export function fetchCumulativeFlow(projectId: string, range?: InsightsRange) {
  return request<CumulativeFlowResponse>(
    projectApiPath(projectId, `/issues/metrics/cumulative-flow${buildCumulativeFlowQueryString(range)}`),
  )
}

export const cumulativeFlowQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['issues', 'metrics', 'cumulative-flow', range, projectId] as const
      : ['issues', 'metrics', 'cumulative-flow', range] as const
  }
  return projectId
    ? ['issues', 'metrics', 'cumulative-flow', projectId] as const
    : ['issues', 'metrics', 'cumulative-flow'] as const
}

export function useCumulativeFlow(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: cumulativeFlowQueryKey(projectId, range),
    queryFn: () => fetchCumulativeFlow(projectId!, range),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}