import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

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

export function fetchCumulativeFlow(projectId: string) {
  return request<CumulativeFlowResponse>(
    projectApiPath(projectId, '/issues/metrics/cumulative-flow'),
  )
}

export const cumulativeFlowQueryKey = (projectId?: string | null) =>
  projectId
    ? ['issues', 'metrics', 'cumulative-flow', projectId] as const
    : ['issues', 'metrics', 'cumulative-flow'] as const

export function useCumulativeFlow() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: cumulativeFlowQueryKey(projectId),
    queryFn: () => fetchCumulativeFlow(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
