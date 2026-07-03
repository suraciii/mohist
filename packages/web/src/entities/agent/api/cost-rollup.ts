import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'
import type { InsightsRange } from '../../../pages/insights/model/insights-range'

export interface AgentCostMetricDto {
  amount: number | null
  currency: string | null
  sampleCount: number
}

export interface AgentCostWindowedFigureDto {
  spend: AgentCostMetricDto
  perIssueCost: AgentCostMetricDto
}

export interface AgentCostRollupDto {
  totalCost: AgentCostMetricDto
  todayCost: AgentCostMetricDto
  doneIssuesCount: number
  costPerShip: AgentCostMetricDto
  currentWindow?: AgentCostWindowedFigureDto
  previousWindow?: AgentCostWindowedFigureDto
}

function buildCostRollupQueryString(range?: InsightsRange): string {
  if (!range) return ''
  return `?range=${range}`
}

export function fetchCostRollup(projectId: string, range?: InsightsRange) {
  return request<AgentCostRollupDto>(
    projectApiPath(projectId, `/agent/cost${buildCostRollupQueryString(range)}`),
  )
}

export const costRollupQueryKey = (projectId?: string | null, range?: InsightsRange | null) => {
  if (range) {
    return projectId
      ? ['agent', 'cost-rollup', range, projectId] as const
      : ['agent', 'cost-rollup', range] as const
  }
  return projectId
    ? ['agent', 'cost-rollup', projectId] as const
    : ['agent', 'cost-rollup'] as const
}

export function useCostRollup(range?: InsightsRange) {
  const { projectId } = useProject()
  return useQuery({
    queryKey: costRollupQueryKey(projectId, range),
    queryFn: () => fetchCostRollup(projectId!, range),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}