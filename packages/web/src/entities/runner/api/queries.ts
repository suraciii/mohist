import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../../shared/api/client'
import type { RunnerStatusEntry, RunnerStatusListResponse, RunnerStatusSummary } from '../model/types'
import { deriveRunnerStatusFacts } from '../model/summary'
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
  const observedAt = Array.isArray(source) ? null : (source?.observedAt ?? null)
  const readyCount = rows.filter((row) => row.admission.state === 'ready').length
  const blockedCount = rows.length - readyCount
  const facts = rows.map(deriveRunnerStatusFacts)
  const onlineCount = facts.filter((fact) => fact.presence === 'online').length
  const staleCount = facts.filter((fact) => fact.presence === 'stale').length
  const offlineCount = facts.filter((fact) => fact.presence === 'offline').length
  const disconnectedCount = facts.filter((fact) => fact.control === 'disconnected').length
  const drainingCount = facts.filter((fact) => fact.draining).length
  const fullCount = facts.filter((fact) => fact.capacityFull).length
  const activeWorkCount = facts.reduce((count, fact) => count + fact.activeWorkCount, 0)
  const excluded = {
    offline: [] as RunnerStatusEntry[],
    'admission-blocked': [] as RunnerStatusEntry[],
    'unknown-occupancy': [] as RunnerStatusEntry[],
  }
  const eligible: RunnerStatusEntry[] = []

  for (const row of rows) {
    if (row.presence.state !== 'online') {
      excluded.offline.push(row)
      continue
    }
    if (row.capacity?.used == null) {
      excluded['unknown-occupancy'].push(row)
      continue
    }
    if (row.admission.state === 'ready' || row.admission.reasonCodes.every((reason) => reason === 'capacity-full')) {
      eligible.push(row)
      continue
    }
    excluded['admission-blocked'].push(row)
  }

  const eligiblePool =
    eligible.length > 0
      ? eligible.reduce(
          (pool, row) => ({ used: pool.used + row.capacity!.used!, total: pool.total + row.capacity!.total }),
          { used: 0, total: 0 },
        )
      : null
  const excludedGroups = (Object.keys(excluded) as Array<keyof typeof excluded>)
    .filter((kind) => excluded[kind].length > 0)
    .map((kind) => ({
      kind,
      count: excluded[kind].length,
      configuredSlots: excluded[kind].reduce<number | null>(
        (slots, row) => (slots == null || row.capacity == null ? null : slots + row.capacity.total),
        0,
      ),
    }))
  const fleetState =
    rows.length === 0
      ? 'no-runners-configured'
      : eligible.some((row) => row.capacity!.used! < row.capacity!.total)
        ? 'capacity-available'
        : excluded['unknown-occupancy'].length > 0
          ? 'availability-unknown'
          : eligible.length > 0
            ? 'capacity-full'
            : 'admission-blocked'
  const reasons =
    fleetState === 'availability-unknown'
      ? [
          ...new Set(
            excluded['unknown-occupancy'].flatMap((row) => [
              'capacity-unknown',
              ...row.admission.reasonCodes.filter((reason) => reason !== 'capacity-full'),
            ]),
          ),
        ]
      : fleetState === 'admission-blocked'
        ? [...new Set(rows.flatMap((row) => row.admission.reasonCodes.filter((reason) => reason !== 'capacity-full')))]
        : []
  const fleet = { state: fleetState, eligiblePool, excludedGroups, reasons, observedAt } as const
  const hasUnknownCapacity = eligiblePool == null
  const capacityUsed = eligiblePool?.used ?? null
  const capacityTotal = eligiblePool?.total ?? 0
  const hasAdmissibleCapacity = fleet.state === 'capacity-available'
  return {
    fleet,
    readyCount,
    blockedCount,
    onlineCount,
    staleCount,
    offlineCount,
    disconnectedCount,
    drainingCount,
    fullCount,
    activeWorkCount,
    capacityUsed,
    capacityTotal,
    hasUnknownCapacity,
    hasAdmissibleCapacity,
    rows,
    inventory,
  }
}

export function useRunnerSummary(): RunnerStatusSummary {
  const { data, isLoading, isError } = useRunners()
  return { ...deriveRunnerSummary(data), isLoading, isError }
}
