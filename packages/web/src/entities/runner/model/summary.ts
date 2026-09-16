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

function countLabel(count: number, singular: string, plural = singular): string {
  return `${count} ${count === 1 ? singular : plural}`
}

export function runnerSummaryFacts(summary: RunnerStatusSummary): string[] {
  if (summary.rows.length === 0) return []

  const facts = [
    countLabel(summary.readyCount, 'admission ready'),
    countLabel(summary.blockedCount, 'admission blocked'),
  ]
  if (summary.onlineCount > 0) facts.push(countLabel(summary.onlineCount, 'online'))
  if (summary.staleCount > 0) facts.push(countLabel(summary.staleCount, 'stale'))
  if (summary.offlineCount > 0) facts.push(countLabel(summary.offlineCount, 'offline'))
  if (summary.disconnectedCount > 0) facts.push(countLabel(summary.disconnectedCount, 'control disconnected'))
  if (summary.drainingCount > 0) facts.push(countLabel(summary.drainingCount, 'draining'))
  if (summary.fullCount > 0) facts.push(countLabel(summary.fullCount, 'capacity full'))
  facts.push(
    summary.hasUnknownCapacity
      ? `unknown/${summary.capacityTotal} slots`
      : `${summary.capacityUsed ?? 0}/${summary.capacityTotal} slots`,
  )
  facts.push(countLabel(summary.activeWorkCount, 'active work', 'active works'))
  return facts
}

export function runnerSummaryText(summary: RunnerStatusSummary): string {
  return runnerSummaryFacts(summary).join(' · ')
}
