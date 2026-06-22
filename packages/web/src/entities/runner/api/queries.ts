import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../../shared/api/client'
import type { RunnerStatusRow, RunnerStatusSummary } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import { getRunner, getRunners, updateRunnerSlots } from './client'

export function useRunners() {
  const { projectId } = useProject()
  return useQuery<RunnerStatusRow[]>({
    queryKey: ['runners', projectId],
    queryFn: () => getRunners(projectId).then(r => r.runners),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}

export function useRunner(runnerId: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery<RunnerStatusRow, ApiError>({
    queryKey: ['runner', projectId, runnerId],
    queryFn: () => getRunner(projectId, runnerId ?? '').then(r => r.runner),
    enabled: !!projectId && !!runnerId,
    retry: (failureCount, error) => {
      if (error.status === 404) return false
      return failureCount < 2
    },
  })
}

export function useUpdateRunnerSlots() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ runnerId, slots }: { runnerId: string; slots: number }) =>
      updateRunnerSlots(runnerId, slots),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['runners'] })
      qc.invalidateQueries({ queryKey: ['runner'] })
    },
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
