import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { RunnerOptions } from '../src/core/types.js'
import type { ServerConnection } from '../src/server/connection.js'
import { createHostShutdown } from '../src/runtime/host-update-shutdown.js'
import type { InFlightEntry } from '../src/runtime/host-state.js'
import type { PendingUpdateOperation } from '../src/runtime/update-operation.js'

const options = {
  runnerId: 'runner-1',
  serverUrl: 'http://server',
  runnerRoot: '/runner',
  pollIntervalMs: 1,
  heartbeatIntervalMs: 1,
  dispatchLivenessProbeIntervalMs: 1,
} satisfies RunnerOptions

function work(workId: string) {
  return {
    workflowRunId: 'workflow-1',
    workId,
    workType: 'task',
    ownerKind: 'workflow',
  }
}

function entry(workId: string): InFlightEntry {
  return {
    work: work(workId),
    controller: new AbortController(),
    done: new Promise<void>(() => undefined),
  }
}

function operation(affectedWorkId: string): PendingUpdateOperation {
  return {
    operationId: 'operation-1',
    runnerId: 'runner-1',
    createdAt: '2026-01-01T00:00:00.000Z',
    affectedWorks: [
      { ownerKind: 'workflow', ownerId: 'workflow-1', workId: affectedWorkId, workType: 'task' },
    ],
  }
}

function makeHost(args: {
  entries?: InFlightEntry[]
  fetch?: (signal: AbortSignal) => Promise<PendingUpdateOperation | null>
  report?: ServerConnection['reportRecoveryStopFailure']
  handoffBudgetMs?: number
  stopBudgetMs?: number
} = {}) {
  const inFlight = new Map((args.entries ?? [entry('work-1')]).map((value) => [`workflow:workflow-1:${value.work.workId}`, value]))
  const reportRecoveryStopFailure = args.report ?? vi.fn(async () => undefined)
  const shutdown = createHostShutdown({
    options,
    connection: { reportRecoveryStopFailure } as unknown as ServerConnection,
    openCodeRuntime: () => null,
    piRuntime: () => null,
    inFlight,
    awaitingAck: new Map(),
    fetchPendingUpdateOperation: args.fetch ?? vi.fn(async () => null),
    shutdownHandoffBudgetMs: args.handoffBudgetMs ?? 100,
    shutdownStopBudgetMs: args.stopBudgetMs ?? 100,
  })
  return { shutdown, inFlight, reportRecoveryStopFailure }
}

describe('createHostShutdown', () => {
  beforeEach(() => vi.useFakeTimers())

  it('hands matched update work to recovery, reports stop failure, and finally removes it', async () => {
    const running = entry('work-1')
    const { shutdown, inFlight, reportRecoveryStopFailure } = makeHost({
      entries: [running],
      fetch: vi.fn(async () => operation('work-1')),
    })

    const stopping = shutdown.shutdownInFlight()
    await vi.runAllTimersAsync()
    await stopping

    expect(running.controller.signal.aborted).toBe(true)
    expect(running.shutdown).toEqual({
      requested: true,
      stopConfirmed: false,
      operationId: 'operation-1',
      stopFailure: 'The Runner could not confirm the stop before shutdown; the recorded recovery path remains active.',
    })
    expect(reportRecoveryStopFailure).toHaveBeenCalledWith(
      expect.objectContaining({ operationId: 'operation-1', workId: 'work-1' }),
      expect.any(AbortSignal),
    )
    expect(inFlight.size).toBe(0)
  })

  it.each([
    ['ordinary shutdown', null],
    ['unaffected update work', operation('other-work')],
  ])('%s aborts and removes work without a recovery stop-failure report', async (_name, pending) => {
    const running = entry('work-1')
    const { shutdown, inFlight, reportRecoveryStopFailure } = makeHost({
      entries: [running],
      fetch: vi.fn(async () => pending),
    })

    const stopping = shutdown.shutdownInFlight()
    await vi.runAllTimersAsync()
    await stopping

    expect(running.shutdown?.operationId).toBeNull()
    expect(reportRecoveryStopFailure).not.toHaveBeenCalled()
    expect(inFlight.size).toBe(0)
  })

  it('bounds pending-operation lookup retries and still removes in-flight work after budget exhaustion', async () => {
    const fetch = vi.fn(async () => {
      throw new Error('transient lookup failure')
    })
    const { shutdown, inFlight, reportRecoveryStopFailure } = makeHost({
      fetch,
      handoffBudgetMs: 10,
      stopBudgetMs: 10,
    })

    const stopping = shutdown.shutdownInFlight()
    await vi.runAllTimersAsync()
    await stopping

    expect(fetch).toHaveBeenCalledTimes(2)
    expect(reportRecoveryStopFailure).not.toHaveBeenCalled()
    expect(inFlight.size).toBe(0)
  })

  it('swallows a bounded stop-failure report failure and finally removes the work', async () => {
    const { shutdown, inFlight } = makeHost({
      fetch: vi.fn(async () => operation('work-1')),
      report: vi.fn(async () => {
        throw new Error('report unavailable')
      }),
    })

    const stopping = shutdown.shutdownInFlight()
    await vi.runAllTimersAsync()
    await expect(stopping).resolves.toBeUndefined()
    expect(inFlight.size).toBe(0)
  })
})
