import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../shared/insights-range'

export interface DeliveryTimePointDto {
  issueNumber: number
  completedAt: string
  leadDays: number
  cycleDays: number | null
}

export interface DeliveryTimeMetricsResponse {
  points: DeliveryTimePointDto[]
  previousCycleDays?: number | null
}

function buildDeliveryTimeQueryString(range?: InsightsRange): string {
  if (!range) return ''
  return `?range=${range}`
}

export function fetchDeliveryTime(projectId: string, range?: InsightsRange) {
  return request<DeliveryTimeMetricsResponse>(
    projectApiPath(projectId, `/issues/metrics/delivery-time${buildDeliveryTimeQueryString(range)}`),
  )
}

export const deliveryTimeQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['issues', 'metrics', 'delivery-time', range, projectId] as const
      : ['issues', 'metrics', 'delivery-time', range] as const
  }
  return projectId
    ? ['issues', 'metrics', 'delivery-time', projectId] as const
    : ['issues', 'metrics', 'delivery-time'] as const
}

export function useDeliveryTime(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: deliveryTimeQueryKey(projectId, range),
    queryFn: () => fetchDeliveryTime(projectId!, range),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}