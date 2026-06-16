import type { SystemUpdateOutcome, SystemUpdateStatus } from './types'
import { SYSTEM_UPDATE_STAGES } from './types'

export const OUTCOME_LABELS: Record<SystemUpdateOutcome, string> = {
  succeeded: 'Succeeded',
  recovered: 'Recovered with warnings',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

export function isSystemUpdateOutcome(value: unknown): value is SystemUpdateOutcome {
  return value === 'succeeded' || value === 'recovered' || value === 'failed' || value === 'cancelled'
}

export function getOutcomeLabel(outcome: SystemUpdateOutcome | null | undefined): string | null {
  if (!outcome) return null
  return OUTCOME_LABELS[outcome] ?? null
}

export function isSupersededStatus(status: string | null | undefined): boolean {
  return status === 'superseded'
}

export function isTerminalUpdateStatus(status: string | null | undefined): boolean {
  return status === 'succeeded' || status === 'failed' || status === 'recovered' || status === 'superseded' || status === 'cancelled'
}

export function isActiveUpdateStatus(status: string | null | undefined): boolean {
  return status === 'running' || status === 'waiting-for-reconnect'
}

export function isSystemUpdateStage(value: string | null | undefined): value is (typeof SYSTEM_UPDATE_STAGES)[number] {
  return typeof value === 'string' && (SYSTEM_UPDATE_STAGES as readonly string[]).includes(value)
}

export function getStageIndex(stage: string | null | undefined): number {
  if (!stage) return -1
  return (SYSTEM_UPDATE_STAGES as readonly string[]).indexOf(stage)
}

export function getActiveStageIndex(status: string | null | undefined, stage: string | null | undefined): number {
  if (isTerminalUpdateStatus(status)) {
    return SYSTEM_UPDATE_STAGES.length - 1
  }
  return getStageIndex(stage)
}

export function getOutcomeCapabilityMessage(status: SystemUpdateStatus): string | null {
  if (status.outcome === 'failed' && status.unavailableCapability) {
    return `Failed capability: ${status.unavailableCapability}`
  }
  if (status.outcome === 'recovered') {
    return status.reason ?? 'Update completed with warnings. Some components may need attention.'
  }
  if (status.outcome === 'failed') {
    return status.reason ?? 'Update failed.'
  }
  return null
}

export const CLI_STAGE_LABELS: readonly string[] = [
  'Updating CLI',
  'Preparing workflow runner',
  'Updating Mohist Server',
  'Waiting for Mohist to become usable',
  'Restoring workflow runner',
  'Verifying workflow runtime',
] as const
