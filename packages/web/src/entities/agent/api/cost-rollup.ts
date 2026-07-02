import { useQuery } from '@tanstack/react-query'
import { projectApiPath, request } from '../../../shared/api/client'
import { useProject } from '../../project/@x/project-context'

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

export function fetchCostRollup(projectId: string) {
  return request<AgentCostRollupDto>(
    projectApiPath(projectId, '/agent/cost'),
  )
}

export const costRollupQueryKey = (projectId?: string | null) =>
  projectId
    ? ['agent', 'cost-rollup', projectId] as const
    : ['agent', 'cost-rollup'] as const

export function useCostRollup() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: costRollupQueryKey(projectId),
    queryFn: () => fetchCostRollup(projectId!),
    enabled: !!projectId,
    staleTime: 60_000,
  })
}