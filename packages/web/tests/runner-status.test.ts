import { describe, expect, it } from 'vitest'
import { deriveRunnerSummary } from '../src/entities/runner/api/queries'
import type { RunnerStatusRow } from '../src/entities/runner/model/types'

function makeRow(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
  return {
    id: 'runner-test',
    kind: 'external',
    hostname: 'test-host',
    scope: { type: 'global' },
    status: 'idle',
    capabilities: [],
    coderModels: [],
    coderModelCount: 0,
    activeWorks: [],
    ...overrides,
  }
}

describe('deriveRunnerSummary', () => {
  it('returns empty capacity for no rows', () => {
    const summary = deriveRunnerSummary([])
    expect(summary.hasConnectedCapacity).toBe(false)
    expect(summary.connectedIdleCount).toBe(0)
    expect(summary.connectedBusyCount).toBe(0)
  })

  it('treats connected idle runner as available capacity', () => {
    const rows = [makeRow({ status: 'idle', connectionState: 'connected' })]
    const summary = deriveRunnerSummary(rows)
    expect(summary.hasConnectedCapacity).toBe(true)
    expect(summary.connectedIdleCount).toBe(1)
    expect(summary.connectedBusyCount).toBe(0)
  })

  it('treats idle runner as available capacity when status is idle', () => {
    const rows = [makeRow({ status: 'idle', connectionState: 'disconnected' })]
    const summary = deriveRunnerSummary(rows)
    expect(summary.hasConnectedCapacity).toBe(true)
    expect(summary.connectedIdleCount).toBe(1)
    expect(summary.connectedBusyCount).toBe(0)
  })

  it('treats connected busy runner as available capacity', () => {
    const rows = [
      makeRow({
        status: 'busy',
        connectionState: 'connected',
        activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
      }),
    ]
    const summary = deriveRunnerSummary(rows)
    expect(summary.hasConnectedCapacity).toBe(true)
    expect(summary.connectedIdleCount).toBe(0)
    expect(summary.connectedBusyCount).toBe(1)
  })

  it('excludes stale runner from connected capacity', () => {
    const rows = [makeRow({ status: 'stale', connectionState: null })]
    const summary = deriveRunnerSummary(rows)
    expect(summary.hasConnectedCapacity).toBe(false)
  })

  it('excludes offline runner from connected capacity', () => {
    const rows = [makeRow({ status: 'offline', connectionState: null })]
    const summary = deriveRunnerSummary(rows)
    expect(summary.hasConnectedCapacity).toBe(false)
  })

  it('counts disconnected busy runner as connected capacity when status is busy', () => {
    const rows = [
      makeRow({
        status: 'busy',
        connectionState: 'disconnected',
        activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
      }),
    ]
    const summary = deriveRunnerSummary(rows)
    expect(summary.hasConnectedCapacity).toBe(true)
    expect(summary.connectedBusyCount).toBe(1)
  })

  it('counts both idle and busy connected runners', () => {
    const rows = [
      makeRow({ id: 'r1', status: 'idle', connectionState: 'connected' }),
      makeRow({
        id: 'r2',
        status: 'busy',
        connectionState: 'connected',
        activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
      }),
    ]
    const summary = deriveRunnerSummary(rows)
    expect(summary.hasConnectedCapacity).toBe(true)
    expect(summary.connectedIdleCount).toBe(1)
    expect(summary.connectedBusyCount).toBe(1)
  })

  it('includes all rows in summary rows', () => {
    const rows = [makeRow({ id: 'r1' }), makeRow({ id: 'r2' })]
    const summary = deriveRunnerSummary(rows)
    expect(summary.rows).toHaveLength(2)
  })
})