import { describe, expect, it, vi } from 'vitest'
import type { ActionRegistry } from '../actions/registry.js'
import type { ServerConnection } from '../server/connection.js'
import type { RunnerOptions, DispatchWorkItem } from '../core/types.js'
import type { RunnerSignalRClient } from '../server/runner-signalr.js'
import type { PiRuntime } from './pi/index.js'
import type { BuildInfo } from './build-info.js'
import { RunnerHostRecovery, type InFlightEntry } from './host.recovery.js'
import { RuntimeTurnRegistry } from './runtime-turn-registry.js'
import { WorkResultJournal, workKey } from './work-result-journal.js'

const work: DispatchWorkItem = {
  workflowRunId: 'workflow-1',
  taskRunId: 'task-1',
  workId: 'work-1',
  workType: 'task',
  ownerKind: 'workflow',
  uses: 'mohist/pi',
}

function createRecovery(
  overrides: Partial<ConstructorParameters<typeof RunnerHostRecovery>[0]> = {},
) {
  const inFlight = new Map<string, InFlightEntry>()
  const awaitingAck = new Map<string, { work: DispatchWorkItem; entry: any }>()
  const registry = new RuntimeTurnRegistry()
  registry.register(workKey(work), {
    agentSessionId: 'session-1',
    agentTurnId: 'turn-1',
    runtime: 'pi',
    runtimeSessionId: 'runtime-session-1',
    workDir: '/workspace',
  })

  const events: string[] = []
  const journal = {
    interrupt: vi.fn(async () => {
      events.push('journal-interrupt')
    }),
    acknowledge: vi.fn(async () => {
      events.push('journal-acknowledge')
    }),
  } as unknown as WorkResultJournal
  const connection = {
    sendRecoveryReceipt: vi.fn(async () => {
      events.push('send-recovery-receipt')
      return { appliedReceiptId: 'receipt-1', status: 'accepted' }
    }),
    reportRecoveryStopFailure: vi.fn(async () => undefined),
  } as unknown as ServerConnection
  const runtime = {
    cancel: vi.fn(async () => ({
      ok: true,
      value: { facts: { cancelled: true, stopConfirmed: true } },
    })),
  } as unknown as PiRuntime
  const entry: InFlightEntry = {
    done: Promise.resolve(),
    work,
    controller: new AbortController(),
  }
  inFlight.set(workKey(work), entry)

  const recovery = new RunnerHostRecovery({
    options: { runnerId: 'runner-1', pollIntervalMs: 10 } as RunnerOptions,
    buildGitHash: null,
    buildInfo: null as BuildInfo | null,
    actions: [] as unknown as ActionRegistry,
    connection,
    signalR: {} as RunnerSignalRClient,
    inFlight,
    awaitingAck,
    runtimeTurnRegistry: registry,
    workResultJournal: journal,
    fetchPendingUpdateOperation: vi.fn(async () => ({
      operationId: 'runner-update:1',
      createdAt: '2026-08-15T00:00:00.000Z',
      affectedWorks: [
        { ownerKind: 'workflow', ownerId: work.workflowRunId, workId: work.workId, taskRunId: work.taskRunId, workType: work.workType },
      ],
    })),
    shutdownHandoffBudgetMs: 100,
    shutdownStopBudgetMs: 100,
    receiptId: () => 'receipt-1',
    getOpenCodeRuntime: () => null,
    getPiRuntime: () => runtime,
    waitForConnectionRetry: vi.fn(async () => undefined),
    disconnectForReconnect: vi.fn(async () => undefined),
    syncOpenCodeWorkOwners: vi.fn(),
    ...overrides,
  })
  return { recovery, inFlight, awaitingAck, registry, journal, connection, runtime, events }
}

describe('RunnerHostRecovery shutdown handoff', () => {
  it('persists a Pi-shaped confirmed interruption before delivering the receipt', async () => {
    const state = createRecovery()
    ;(state.journal.interrupt as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      state.events.push('journal-interrupt')
    })
    ;(state.journal.acknowledge as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      state.events.push('journal-acknowledge')
    })

    await state.recovery.shutdownInFlight()

    expect(state.runtime.cancel).toHaveBeenCalledWith({
      target: {
        runtime: 'pi',
        runtimeSessionId: 'runtime-session-1',
        workDir: '/workspace',
      },
    })
    expect(state.journal.interrupt).toHaveBeenCalledTimes(1)
    expect(state.connection.sendRecoveryReceipt).toHaveBeenCalledWith(
      expect.objectContaining({
        receiptId: 'receipt-1',
        workflowRunId: work.workflowRunId,
        workId: work.workId,
        payload: {
          type: 'update-interrupted',
          updateOperationId: 'runner-update:1',
          stopConfirmed: true,
        },
      }),
      expect.any(AbortSignal),
    )
    expect(state.events).toEqual(['journal-interrupt', 'send-recovery-receipt', 'journal-acknowledge'])
    expect(state.inFlight.size).toBe(0)
    expect(state.awaitingAck.size).toBe(0)
  })

  it('keeps an ordinary restart free of interruption receipts', async () => {
    const state = createRecovery({
      fetchPendingUpdateOperation: vi.fn(async () => null),
    })

    await state.recovery.shutdownInFlight()

    expect(state.runtime.cancel).toHaveBeenCalledTimes(1)
    expect(state.journal.interrupt).not.toHaveBeenCalled()
    expect(state.connection.sendRecoveryReceipt).not.toHaveBeenCalled()
    expect(state.inFlight.size).toBe(0)
  })

  it('persists update context for an unconfirmed transport stop without raw error text', async () => {
    const state = createRecovery()
    ;(state.runtime.cancel as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('session.abort fetch failed'))

    await state.recovery.shutdownInFlight()

    expect(state.connection.reportRecoveryStopFailure).toHaveBeenCalledWith(
      expect.objectContaining({
        operationId: 'runner-update:1',
        workId: work.workId,
        message: 'The Runner could not confirm the stop before shutdown; the recorded recovery path remains active.',
      }),
      expect.any(AbortSignal),
    )
    const reportStopFailure = state.connection.reportRecoveryStopFailure as unknown as ReturnType<typeof vi.fn>
    expect(reportStopFailure.mock.calls[0][0].message).not.toContain('session.abort fetch failed')
    expect(state.journal.interrupt).not.toHaveBeenCalled()
  })
})
