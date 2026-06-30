import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

export interface AgentUsageBucketDto {
  bucketStart: string
  bucketEnd: string
  inputTokens: number
  outputTokens: number
  totalTokens: number
  costAmount: number
  costCurrency: string | null
}

export interface CumulativeCostPerShipPointDto {
  dayEnd: string
  cumulativeCost: number | null
  currency: string | null
  cumulativeShippedCount: number
  costPerShip: number | null
}

export interface AgentUsageTimeseriesDto {
  rangeFrom: string
  rangeTo: string
  bucketGranularity: string
  buckets: AgentUsageBucketDto[]
  cumulativeCostPerShip?: CumulativeCostPerShipPointDto[] | null
}

export function fetchAgentUsage(projectId: string) {
  return request<AgentUsageTimeseriesDto>(
    projectApiPath(projectId, '/agent/usage'),
  )
}

export const agentUsageQueryKey = (projectId?: string | null) =>
  projectId
    ? ['agent', 'usage', projectId] as const
    : ['agent', 'usage'] as const

export function useAgentUsage() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: agentUsageQueryKey(projectId),
    queryFn: () => fetchAgentUsage(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}
