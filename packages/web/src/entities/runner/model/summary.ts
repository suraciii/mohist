import type { RunnerStatusEntry, RunnerStatusSummary } from './types'

export interface RunnerStatusFacts {
  presence: RunnerStatusEntry['presence']['state']
  control: RunnerStatusEntry['control']['state']
  admission: RunnerStatusEntry['admission']['state']
  draining: boolean
  capacityFull: boolean
  activeWorkCount: number
}

export function deriveRunnerStatusFacts(row: RunnerStatusEntry): RunnerStatusFacts {
  const reasonCodes = new Set(row.admission.reasonCodes)
  const capacityFull =
    reasonCodes.has('capacity-full') ||
    (row.capacity?.used != null && row.capacity.total > 0 && row.capacity.used >= row.capacity.total)

  return {
    presence: row.presence.state,
    control: row.control.state,
    admission: row.admission.state,
    draining: row.drain?.active === true || reasonCodes.has('draining'),
    capacityFull,
    activeWorkCount: row.activeWorks.length,
  }
}

export function runnerStatusLabels(row: RunnerStatusEntry): string[] {
  const facts = deriveRunnerStatusFacts(row)
  const labels: string[] = []

  if (facts.presence === 'offline') labels.push('offline')
  else if (facts.presence === 'stale') labels.push('stale')
  if (facts.control === 'disconnected') labels.push('control disconnected')
  if (facts.admission === 'blocked') labels.push('admission blocked')
  if (facts.draining) labels.push('draining')
  if (facts.capacityFull) labels.push('capacity full')
  if (facts.activeWorkCount > 0) {
    labels.push(`${facts.activeWorkCount} active work${facts.activeWorkCount === 1 ? '' : 's'}`)
  }
  if (labels.length === 0) labels.push('admission ready')

  return labels
}

const RUNNER_REASON_LABELS: Record<string, string> = {
  'admission-observation-missing': 'admission observation missing',
  'capacity-full': 'capacity full',
  'capacity-unknown': 'capacity unknown',
  'control-disconnected': 'control disconnected',
  draining: 'draining',
  'presence-offline': 'presence offline',
  'presence-stale': 'presence stale',
}

function countLabel(count: number, singular: string, plural = singular): string {
  return `${count} ${count === 1 ? singular : plural}`
}

export function runnerFleetLabel(summary: RunnerStatusSummary): string {
  switch (summary.fleet.state) {
    case 'capacity-available':
      return 'Capacity available'
    case 'capacity-full':
      return 'Capacity full'
    case 'availability-unknown':
      return 'Availability unknown'
    case 'admission-blocked':
      return 'Admission blocked'
    case 'no-runners-configured':
      return 'No Runners configured'
  }
}

export function runnerSummaryFacts(summary: RunnerStatusSummary): string[] {
  const facts: string[] = []
  const pool = summary.fleet.eligiblePool
  if (pool) facts.push(`${pool.used}/${pool.total} occupied`)
  else if (summary.fleet.state === 'availability-unknown') facts.push('Occupancy unknown')

  for (const group of summary.fleet.excludedGroups) {
    const count = countLabel(group.count, group.kind === 'admission-blocked' ? 'admission blocked' : group.kind)
    const capacity =
      group.configuredSlots == null ? 'configured slots unknown' : `${group.configuredSlots} configured slots`
    const occupancy = group.kind === 'offline' || group.kind === 'unknown-occupancy' ? ', occupancy unknown' : ''
    facts.push(`${count} / ${capacity}${occupancy}`)
  }

  if (summary.fleet.reasons.length > 0) {
    facts.push(`reasons: ${summary.fleet.reasons.map((reason) => RUNNER_REASON_LABELS[reason] ?? reason).join(', ')}`)
  }
  if (summary.disconnectedCount > 0) facts.push(countLabel(summary.disconnectedCount, 'control disconnected'))
  if (summary.fullCount > 0) facts.push(countLabel(summary.fullCount, 'capacity full'))
  facts.push(countLabel(summary.activeWorkCount, 'active work', 'active works'))
  return facts
}

export function runnerSummaryText(summary: RunnerStatusSummary): string {
  return runnerSummaryFacts(summary).join(' · ')
}
