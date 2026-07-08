import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../shared/insights-range'

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

function buildAgentUsageQueryString(range?: InsightsRange): string {
  if (!range) return ''
  return `?range=${range}`
}

export function fetchAgentUsage(projectId: string, range?: InsightsRange) {
  return request<AgentUsageTimeseriesDto>(
    projectApiPath(projectId, `/agent/usage${buildAgentUsageQueryString(range)}`),
  )
}

export const agentUsageQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['agent', 'usage', range, projectId] as const
      : ['agent', 'usage', range] as const
  }
  return projectId
    ? ['agent', 'usage', projectId] as const
    : ['agent', 'usage'] as const
}

export function agentUsageQueryOptions(projectId: string | null | undefined, range?: InsightsRange) {
  return {
    queryKey: agentUsageQueryKey(projectId, range),
    queryFn: () => fetchAgentUsage(projectId!, range),
    enabled: !!projectId,
    staleTime: 60_000,
  } as const
}

export function useAgentUsage(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery(agentUsageQueryOptions(projectId, range))
}