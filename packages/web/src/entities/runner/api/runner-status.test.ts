import { describe, expect, it } from 'vitest'
import { deriveRunnerSummary } from './queries'
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
})
