import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../../shared/api/client'
import type { RunnerStatusEntry, RunnerStatusListResponse, RunnerStatusSummary } from '../model/types'
import { getRunner, getRunners, updateRunnerSlots } from './client'

export function runnersQueryOptions() {
  return {
    queryKey: ['runners'] as const,
    queryFn: getRunners,
    refetchInterval: 5000,
  }
}

export function useRunners() {
  return useQuery<RunnerStatusListResponse>(runnersQueryOptions())
}

export function runnerQueryOptions(runnerId: string | null | undefined) {
  return {
    queryKey: ['runner', runnerId] as const,
    queryFn: () => getRunner(runnerId ?? ''),
    enabled: !!runnerId,
    retry: (failureCount: number, error: ApiError) => {
      if (error.status === 404) return false
      return failureCount < 2
    },
  }
}

export function useRunner(runnerId: string | null | undefined) {
  return useQuery({
    ...runnerQueryOptions(runnerId),
    select: (response) => response.runner,
  })
}

export function useUpdateRunnerSlots() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ runnerId, slots }: { runnerId: string; slots: number }) => updateRunnerSlots(runnerId, slots),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['runners'] })
      qc.invalidateQueries({ queryKey: ['runner'] })
    },
  })
}

export function deriveRunnerSummary(
  source: RunnerStatusListResponse | RunnerStatusEntry[] | undefined,
): RunnerStatusSummary {
  const rows = Array.isArray(source) ? source : (source?.runners ?? [])
  const inventory = Array.isArray(source) ? null : (source?.inventory ?? null)
  const readyCount = rows.filter((row) => row.admission.state === 'ready').length
  const blockedCount = rows.length - readyCount
  const activeWorkCount = rows.reduce((count, row) => count + row.activeWorks.length, 0)
  const hasAdmissibleCapacity = rows.some(
    (row) => row.admission.state === 'ready' && row.capacity?.used != null && row.capacity.used < row.capacity.total,
  )

  return { readyCount, blockedCount, activeWorkCount, hasAdmissibleCapacity, rows, inventory }
}

export function useRunnerSummary() {
  const { data } = useRunners()
  return deriveRunnerSummary(data)
}
