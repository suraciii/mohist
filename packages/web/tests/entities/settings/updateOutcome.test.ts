// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import {
  CLI_STAGE_LABELS,
  getActiveStageIndex,
  getOutcomeCapabilityMessage,
  getOutcomeLabel,
  getStageIndex,
  isActiveUpdateStatus,
  isSupersededStatus,
  isSystemUpdateOutcome,
  isSystemUpdateStage,
  isTerminalUpdateStatus,
  OUTCOME_LABELS,
} from '../../../src/entities/settings/model/updateOutcome'
import { SYSTEM_UPDATE_STAGES } from '../../../src/entities/settings/model/types'
import type { SystemUpdateStatus } from '../../../src/entities/settings/model/types'

function makeStatus(overrides: Partial<SystemUpdateStatus> = {}): SystemUpdateStatus {
  return {
    jobId: 'job-1',
    status: 'running',
    stage: 'Building',
    updateAvailable: true,
    runningGitHash: 'abc',
    sourceHead: 'def',
    sourcePath: '/repo',
    serverUnit: 'mohist.service',
    runnerUnit: 'mohist-runner.service',
    reason: null,
    logs: [],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:01Z',
    completedAt: null,
    ...overrides,
  }
}

describe('System update outcome helpers', () => {
  describe('OUTCOME_LABELS', () => {
    it('exposes user-facing labels for each outcome', () => {
      expect(OUTCOME_LABELS.succeeded).toBe('Succeeded')
      expect(OUTCOME_LABELS.recovered).toBe('Recovered with warnings')
      expect(OUTCOME_LABELS.failed).toBe('Failed')
      expect(OUTCOME_LABELS.cancelled).toBe('Cancelled')
    })
  })

  describe('isSystemUpdateOutcome', () => {
    it('accepts canonical outcome values', () => {
      expect(isSystemUpdateOutcome('succeeded')).toBe(true)
      expect(isSystemUpdateOutcome('recovered')).toBe(true)
      expect(isSystemUpdateOutcome('failed')).toBe(true)
      expect(isSystemUpdateOutcome('cancelled')).toBe(true)
    })

    it('rejects non-canonical outcome values', () => {
      expect(isSystemUpdateOutcome('pending')).toBe(false)
      expect(isSystemUpdateOutcome(null)).toBe(false)
      expect(isSystemUpdateOutcome(undefined)).toBe(false)
      expect(isSystemUpdateOutcome(42)).toBe(false)
    })
  })

  describe('getOutcomeLabel', () => {
    it('returns null for empty outcome', () => {
      expect(getOutcomeLabel(null)).toBeNull()
      expect(getOutcomeLabel(undefined)).toBeNull()
    })

    it('maps each outcome to its user-facing label', () => {
      expect(getOutcomeLabel('succeeded')).toBe('Succeeded')
      expect(getOutcomeLabel('recovered')).toBe('Recovered with warnings')
      expect(getOutcomeLabel('failed')).toBe('Failed')
    })
  })

  describe('isSupersededStatus', () => {
    it('is true only for superseded status', () => {
      expect(isSupersededStatus('superseded')).toBe(true)
      expect(isSupersededStatus('succeeded')).toBe(false)
      expect(isSupersededStatus(null)).toBe(false)
      expect(isSupersededStatus(undefined)).toBe(false)
    })
  })

  describe('isTerminalUpdateStatus', () => {
    it('treats succeeded, failed, recovered, superseded and cancelled as terminal', () => {
      expect(isTerminalUpdateStatus('succeeded')).toBe(true)
      expect(isTerminalUpdateStatus('failed')).toBe(true)
      expect(isTerminalUpdateStatus('recovered')).toBe(true)
      expect(isTerminalUpdateStatus('superseded')).toBe(true)
      expect(isTerminalUpdateStatus('cancelled')).toBe(true)
    })

    it('does not treat running or waiting-for-reconnect as terminal', () => {
      expect(isTerminalUpdateStatus('running')).toBe(false)
      expect(isTerminalUpdateStatus('waiting-for-reconnect')).toBe(false)
    })
  })

  describe('isActiveUpdateStatus', () => {
    it('treats running and waiting-for-reconnect as active', () => {
      expect(isActiveUpdateStatus('running')).toBe(true)
      expect(isActiveUpdateStatus('waiting-for-reconnect')).toBe(true)
    })

    it('does not treat terminal statuses as active', () => {
      expect(isActiveUpdateStatus('succeeded')).toBe(false)
      expect(isActiveUpdateStatus('failed')).toBe(false)
      expect(isActiveUpdateStatus('recovered')).toBe(false)
      expect(isActiveUpdateStatus('superseded')).toBe(false)
      expect(isActiveUpdateStatus('cancelled')).toBe(false)
    })
  })
})

describe('System update stage helpers', () => {
  it('exposes the canonical shared Web/API stage names', () => {
    expect(SYSTEM_UPDATE_STAGES).toEqual([
      'Building',
      'Restarting server',
      'Waiting for reconnect',
      'Restoring runner',
      'Verifying runtime',
    ])
  })

  it('exposes the CLI stage labels alongside Web labels', () => {
    expect(CLI_STAGE_LABELS).toEqual([
      'Updating CLI',
      'Preparing workflow runner',
      'Updating Mohist Server',
      'Waiting for Mohist to become usable',
      'Restoring workflow runner',
      'Verifying workflow runtime',
    ])
  })

  it('isSystemUpdateStage only accepts shared stage names', () => {
    for (const stage of SYSTEM_UPDATE_STAGES) {
      expect(isSystemUpdateStage(stage)).toBe(true)
    }
    expect(isSystemUpdateStage('Waiting for Mohist to become usable')).toBe(false)
    expect(isSystemUpdateStage(null)).toBe(false)
    expect(isSystemUpdateStage(undefined)).toBe(false)
  })

  it('getStageIndex returns the index of a known stage', () => {
    expect(getStageIndex('Building')).toBe(0)
    expect(getStageIndex('Restarting server')).toBe(1)
    expect(getStageIndex('Waiting for reconnect')).toBe(2)
    expect(getStageIndex('Restoring runner')).toBe(3)
    expect(getStageIndex('Verifying runtime')).toBe(4)
  })

  it('getStageIndex returns -1 for unknown or empty stages', () => {
    expect(getStageIndex('Unknown stage')).toBe(-1)
    expect(getStageIndex(null)).toBe(-1)
    expect(getStageIndex(undefined)).toBe(-1)
  })

  it('getActiveStageIndex returns the last index for terminal statuses', () => {
    const lastIndex = SYSTEM_UPDATE_STAGES.length - 1
    expect(getActiveStageIndex('succeeded', 'Building')).toBe(lastIndex)
    expect(getActiveStageIndex('recovered', 'Verifying runtime')).toBe(lastIndex)
    expect(getActiveStageIndex('failed', 'Restarting server')).toBe(lastIndex)
    expect(getActiveStageIndex('superseded', 'Waiting for reconnect')).toBe(lastIndex)
  })

  it('getActiveStageIndex returns the stage index for active statuses', () => {
    expect(getActiveStageIndex('running', 'Building')).toBe(0)
    expect(getActiveStageIndex('running', 'Waiting for reconnect')).toBe(2)
    expect(getActiveStageIndex('waiting-for-reconnect', 'Restoring runner')).toBe(3)
  })
})

describe('System update outcome capability messages', () => {
  it('returns Failed capability message for failed outcomes with unavailable capability', () => {
    const status = makeStatus({
      status: 'failed',
      outcome: 'failed',
      unavailableCapability: 'runner',
    })
    expect(getOutcomeCapabilityMessage(status)).toBe('Failed capability: runner')
  })

  it('returns reason for recovered outcomes', () => {
    const status = makeStatus({
      status: 'recovered',
      outcome: 'recovered',
      reason: 'Skill assets missing',
    })
    expect(getOutcomeCapabilityMessage(status)).toBe('Skill assets missing')
  })

  it('returns default warning for recovered outcomes without reason', () => {
    const status = makeStatus({
      status: 'recovered',
      outcome: 'recovered',
      reason: null,
    })
    expect(getOutcomeCapabilityMessage(status)).toMatch(/warnings/i)
  })

  it('returns reason for failed outcomes without unavailable capability', () => {
    const status = makeStatus({
      status: 'failed',
      outcome: 'failed',
      reason: 'Update could not complete',
    })
    expect(getOutcomeCapabilityMessage(status)).toBe('Update could not complete')
  })

  it('returns null when no outcome-specific message is available', () => {
    const status = makeStatus({ outcome: null })
    expect(getOutcomeCapabilityMessage(status)).toBeNull()
  })
})
