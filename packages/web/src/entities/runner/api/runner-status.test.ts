import { describe, expect, it } from 'vitest'
import { deriveRunnerSummary } from './queries'
import { runnerSummaryText } from '../model/summary'
import type { RunnerStatusEntry } from '../model/types'

function makeRow(overrides: Partial<RunnerStatusEntry> = {}): RunnerStatusEntry {
  return {
    identity: {
      id: 'runner-test',
      hostname: 'test-host',
      kind: 'external',
      component: 'mohist-runner',
      sourceRevision: null,
      releaseId: null,
      generation: 1,
    },
    presence: { state: 'online', lastObservedAt: '2026-01-01T12:00:00Z' },
    control: { state: 'connected', generation: 'connection-1' },
    admission: { state: 'ready', reasonCodes: [] },
    capabilities: [],
    runtimes: [],
    capacity: { used: 0, total: 2 },
    activeWorks: [],
    drain: null,
    nextActions: [],
    ...overrides,
  }
}

describe('deriveRunnerSummary', () => {
  it('keeps first-install inventory guidance separate from rows', () => {
    const summary = deriveRunnerSummary({
      observedAt: '2026-01-01T00:00:00Z',
      inventory: {
        state: 'first-install',
        nextActions: [{ code: 'install-runner', message: 'Install Runner', command: 'mo install runner' }],
      },
      runners: [],
    })
    expect(summary.rows).toHaveLength(0)
    expect(summary.inventory?.state).toBe('first-install')
    expect(summary.readyCount).toBe(0)
  })

  it('counts independent admission and active-owner facts', () => {
    const rows = [
      makeRow({
        capacity: { used: 1, total: 2 },
        activeWorks: [
          {
            workId: 'w1',
            ownerKind: 'workflow',
            ownerId: 'wf-1',
            workType: 'workflow',
            stage: null,
            title: null,
            issue: null,
          },
        ],
      }),
      makeRow({
        identity: { ...makeRow().identity, id: 'runner-2' },
        admission: { state: 'blocked', reasonCodes: ['capacity-full'] },
        capacity: { used: 2, total: 2 },
      }),
    ]
    const summary = deriveRunnerSummary(rows)
    expect(summary.readyCount).toBe(1)
    expect(summary.blockedCount).toBe(1)
    expect(summary.activeWorkCount).toBe(1)
    expect(summary.hasAdmissibleCapacity).toBe(true)
  })

  it('does not infer admissible capacity from unknown used capacity', () => {
    const summary = deriveRunnerSummary([makeRow({ capacity: { used: null, total: 2 } })])
    expect(summary.hasAdmissibleCapacity).toBe(false)
  })

  it('retains presence, control, drain, full-capacity, and active-work facts in one summary', () => {
    const summary = deriveRunnerSummary([
      makeRow({
        identity: { ...makeRow().identity, id: 'offline' },
        presence: { state: 'offline', lastObservedAt: null },
        control: { state: 'disconnected', generation: null },
        admission: { state: 'blocked', reasonCodes: ['presence-offline', 'control-disconnected'] },
        capacity: { used: null, total: 4 },
      }),
      makeRow({
        identity: { ...makeRow().identity, id: 'draining' },
        presence: { state: 'stale', lastObservedAt: '2026-01-01T00:00:00Z' },
        admission: { state: 'blocked', reasonCodes: ['draining', 'capacity-full'] },
        drain: { active: true, kind: 'update', updateInterruptId: 'interrupt-1' },
        capacity: { used: 2, total: 2 },
        activeWorks: [
          {
            workId: 'work-1',
            ownerKind: 'agent-job',
            ownerId: 'job-1',
            workType: 'agent-job',
            stage: null,
            title: null,
            issue: null,
          },
        ],
      }),
    ])

    expect(summary.onlineCount).toBe(0)
    expect(summary.staleCount).toBe(1)
    expect(summary.offlineCount).toBe(1)
    expect(summary.disconnectedCount).toBe(1)
    expect(summary.drainingCount).toBe(1)
    expect(summary.fullCount).toBe(1)
    expect(summary.activeWorkCount).toBe(1)
    expect(summary.capacityUsed).toBeNull()
    expect(summary.capacityTotal).toBe(6)
    expect(summary.hasUnknownCapacity).toBe(true)
    expect(runnerSummaryText(summary)).toContain('1 capacity full')
    expect(runnerSummaryText(summary)).toContain('1 active work')
  })
})
