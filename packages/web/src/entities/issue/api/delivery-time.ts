import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

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

export function fetchDeliveryTime(projectId: string) {
  return request<DeliveryTimeMetricsResponse>(
    projectApiPath(projectId, '/issues/metrics/delivery-time'),
  )
}

export const deliveryTimeQueryKey = (projectId?: string | null) =>
  projectId
    ? ['issues', 'metrics', 'delivery-time', projectId] as const
    : ['issues', 'metrics', 'delivery-time'] as const

export function useDeliveryTime() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: deliveryTimeQueryKey(projectId),
    queryFn: () => fetchDeliveryTime(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
