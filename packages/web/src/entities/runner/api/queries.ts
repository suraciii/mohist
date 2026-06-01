import { useQuery } from '@tanstack/react-query'
import type { RunnerStatusRow, RunnerStatusSummary } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import { getRunners } from './client'

export function useRunners() {
  const { projectId } = useProject()
  return useQuery<RunnerStatusRow[]>({
    queryKey: ['runners', projectId],
    queryFn: () => getRunners(projectId).then(r => r.runners),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}

export function deriveRunnerSummary(rows: RunnerStatusRow[]): RunnerStatusSummary {
  const connectedIdleCount = rows.filter(r => r.status === 'idle').length

  const connectedBusyCount = rows.filter(r => r.status === 'busy').length

  const hasConnectedCapacity = connectedIdleCount > 0 || connectedBusyCount > 0

  return { connectedIdleCount, connectedBusyCount, hasConnectedCapacity, rows }
}

export function useRunnerSummary() {
  const { data: rows = [] } = useRunners()
  return deriveRunnerSummary(rows)
}
