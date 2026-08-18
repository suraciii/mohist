import type { DispatchWorkItem, RunnerOptions, WorkItemResult } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { OpenCodeRuntime } from './opencode/index.js'
import type { PiRuntime } from './pi/index.js'
import { callCancel, readCancelFacts, resolveCommandRuntime } from '../server/command-runtime.js'
import { createInterruptedRecoveryReceipt, type PendingUpdateOperation } from './recovery-receipt.js'
import type { RuntimeTurnRegistry } from './runtime-turn-registry.js'
import type { WorkResultJournal } from './work-result-journal.js'
import { workKey as journalWorkKey } from './work-result-journal.js'
import { runnerLogger } from '../system/logger.js'
import { withTimeout } from './host-timing.js'
import type { AwaitingAckEntry, InFlightEntry } from './host-state.js'
import type { RunnerHostShutdown } from './host-shutdown-types.js'

const log = runnerLogger.child('host')

export const SHUTDOWN_HANDOFF_BUDGET_MS = 1_000
const SHUTDOWN_HANDOFF_ATTEMPTS = 2

const STOP_FAILURE_RECOVERY =
  'The Runner could not confirm the stop before shutdown; the recorded recovery path remains active.'

export interface ShutdownWorkState {
  requested: boolean
  stopConfirmed: boolean
  operationId: string | null
  stopFailure?: string | null
}

export interface ShutdownInFlightEntry {
  work: DispatchWorkItem
  controller: AbortController
  done: Promise<void>
  terminalPersisted?: boolean
  shutdown?: ShutdownWorkState
}

export interface ShutdownAwaitingAckEntry {
  readonly result: WorkItemResult
  readonly receipt?: ReturnType<typeof createInterruptedRecoveryReceipt>
  attempts: number
  retryAt: number | null
}

export interface HostShutdownContext {
  readonly options: RunnerOptions
  readonly connection: ServerConnection
  readonly openCodeRuntime: () => OpenCodeRuntime | null
  readonly piRuntime: () => PiRuntime | null
  readonly inFlight: Map<string, InFlightEntry>
  readonly awaitingAck: Map<string, { work: DispatchWorkItem; entry: AwaitingAckEntry }>
  readonly runtimeTurnRegistry: RuntimeTurnRegistry
  readonly workResultJournal: WorkResultJournal
  readonly receiptId: () => string
  readonly reportOnce: (key: string, signal?: AbortSignal) => Promise<void>
  readonly scheduleReportRetry: (key: string) => void
  readonly syncOpenCodeWorkOwners: () => void
  readonly fetchPendingUpdateOperation: (signal: AbortSignal) => Promise<PendingUpdateOperation | null>
  readonly shutdownHandoffBudgetMs: number
  readonly shutdownStopBudgetMs: number
}

const workKey = journalWorkKey

/**
 * Build the recorder used by the shutdown handoff and receipt delivery path.
 * Holding every callback on the context keeps the rest of the runner free of
 * host-scoped private-field dereferences.
 */
export function createHostShutdown(context: HostShutdownContext): RunnerHostShutdown {
  async function persistInterrupted(
    entry: ShutdownInFlightEntry,
    operationId: string,
    deliveryBudgetMs?: number,
  ): Promise<boolean> {
    if (entry.terminalPersisted) return true
    const key = workKey(entry.work)
    const binding = context.runtimeTurnRegistry.get(key)
    if (!binding) return false
    const receipt = createInterruptedRecoveryReceipt(
      entry.work,
      binding,
      context.options.runnerId,
      operationId,
      context.receiptId(),
    )
    if (!receipt) return false
    try {
      await context.workResultJournal.interrupt(entry.work, receipt)
    } catch (error) {
      log.warn('update interruption receipt could not be persisted', {
        work: entry.work.workId,
        context: 'update-interruption',
        exception: error,
      })
      context.workResultJournal.disable()
      return false
    }
    entry.terminalPersisted = true
    context.inFlight.delete(key)
    context.runtimeTurnRegistry.remove(key)
    context.awaitingAck.set(key, {
      work: entry.work,
      entry: { result: { status: 'interrupted' }, receipt, attempts: 0, retryAt: null },
    })
    context.syncOpenCodeWorkOwners()
    try {
      if (deliveryBudgetMs === undefined) {
        await context.reportOnce(key)
      } else {
        await reportWithinBudget(key, deliveryBudgetMs)
      }
    } catch (error) {
      context.scheduleReportRetry(key)
      log.warn('update interruption receipt delivery failed; will retry', {
        work: entry.work.workId,
        context: 'update-interruption',
        exception: error,
      })
    }
    return true
  }

  async function shutdownInFlight(): Promise<void> {
    const entries = [...context.inFlight.values()]
    if (entries.length === 0) return

    // This is the only source of update context. The CLI confirmation is not
    // delivered to the Runner process, so a failed or empty handoff is
    // intentionally indistinguishable from an ordinary restart here.
    const operation = await pendingUpdateOperationForShutdown()
    const deadline = Date.now() + context.shutdownStopBudgetMs
    await Promise.all(entries.map((entry) => requestCooperativeStop(entry, operation, deadline)))

    await withTimeout(Promise.allSettled(entries.map((entry) => entry.done)), Math.max(0, deadline - Date.now()))

    // A runtime may have confirmed the physical stop while its action wrapper
    // is still unwinding. The receipt can be committed after the bounded wait;
    // the execution path checks terminalPersisted and cannot create a second
    // terminal record.
    for (const entry of entries) {
      if (entry.shutdown?.stopConfirmed && entry.shutdown.operationId && context.inFlight.has(workKey(entry.work))) {
        await persistInterrupted(entry, entry.shutdown.operationId, Math.max(0, deadline - Date.now()))
      }
      if (context.inFlight.has(workKey(entry.work))) {
        entry.controller.abort()
        context.inFlight.delete(workKey(entry.work))
        context.runtimeTurnRegistry.remove(workKey(entry.work))
      }
    }
    context.syncOpenCodeWorkOwners()
  }

  async function pendingUpdateOperationForShutdown(): Promise<PendingUpdateOperation | null> {
    const deadline = Date.now() + context.shutdownHandoffBudgetMs
    let attempt = 0
    while (Date.now() <= deadline && attempt < SHUTDOWN_HANDOFF_ATTEMPTS) {
      attempt += 1
      const remaining = Math.max(1, deadline - Date.now())
      const request = new AbortController()
      try {
        const response = await withTimeout(
          context.fetchPendingUpdateOperation(request.signal).then((value) => ({ value })),
          remaining,
        )
        if (response) {
          if (response.value?.runnerId && response.value.runnerId !== context.options.runnerId) {
            log.warn('update shutdown handoff named a different Runner; ignoring operation', {
              context: 'update-interruption',
              expectedRunnerId: context.options.runnerId,
              actualRunnerId: response.value.runnerId,
            })
            return null
          }
          return response.value
        }
        // A timed-out request does not establish that this is an ordinary
        // restart; spend the remaining handoff budget on the brief retry.
        continue
      } catch (error) {
        if (Date.now() >= deadline) break
        log.warn('update shutdown handoff failed; retrying within bounded budget', {
          context: 'update-interruption',
          attempt,
          exception: error,
        })
        await Promise.resolve()
      } finally {
        request.abort()
      }
    }
    return null
  }

  async function requestCooperativeStop(
    entry: ShutdownInFlightEntry,
    operation: PendingUpdateOperation | null,
    deadline: number,
  ): Promise<void> {
    const key = workKey(entry.work)
    const operationId = operation && operationNamesWork(operation, entry.work) ? operation.operationId : null
    entry.shutdown = { requested: true, stopConfirmed: false, operationId }
    const binding = context.runtimeTurnRegistry.get(key)
    let confirmed = false
    let stopFailure: string | null = null
    if (binding?.runtimeSessionId) {
      const handle = resolveCommandRuntime(
        { runtime: binding.runtime },
        {
          openCode: context.openCodeRuntime,
          pi: context.piRuntime,
        },
      )
      if (handle) {
        const remaining = Math.max(1, deadline - Date.now())
        try {
          const result = await withTimeout(
            callCancel(handle, {
              runtime: binding.runtime,
              runtimeSessionId: binding.runtimeSessionId,
              workDir: binding.workDir,
            }),
            remaining,
          )
          confirmed = result !== null && readCancelFacts(result)?.stopConfirmed === true
        } catch (error) {
          stopFailure = STOP_FAILURE_RECOVERY
          log.warn('cooperative stop failed during shutdown', {
            work: entry.work.workId,
            context: operationId ? 'update-interruption' : 'shutdown',
            reason: 'runtime-stop-unconfirmed',
            ...(operationId ? { updateOperationId: operationId } : {}),
            exception: error,
          })
        }
      }
    }
    if (operationId && !confirmed && stopFailure === null) {
      stopFailure = STOP_FAILURE_RECOVERY
    }
    entry.shutdown.stopConfirmed = confirmed
    if (stopFailure !== null) {
      entry.shutdown.stopFailure = stopFailure
      if (operationId) {
        try {
          await withTimeout(
            context.connection.reportRecoveryStopFailure(
              {
                runnerId: context.options.runnerId,
                ownerKind: entry.work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow',
                ownerId:
                  entry.work.ownerKind === 'agent-job' ? (entry.work.agentJobId ?? '') : entry.work.workflowRunId,
                workId: entry.work.workId,
                taskRunId: entry.work.taskRunId ?? null,
                operationId,
                message: stopFailure,
              },
              new AbortController().signal,
            ),
            Math.max(1, deadline - Date.now()),
          )
        } catch (error) {
          log.warn('update stop failure could not be persisted', {
            work: entry.work.workId,
            context: 'update-interruption',
            updateOperationId: operationId,
            exception: error,
          })
        }
      }
    }
    // The runtime stop is authoritative when it confirms. The child signal is
    // still aborted to release action wrappers and non-runtime work promptly.
    entry.controller.abort()
  }

  /**
   * Reports a single awaitingAck entry. Accepted and stale reports are both
   * durable acknowledgements. An untracked response leaves the original
   * result in place for reconciliation rather than silently dropping it.
   */
  async function reportWithinBudget(key: string, budgetMs: number): Promise<void> {
    if (budgetMs <= 0) throw new Error('shutdown receipt delivery budget expired')
    const controller = new AbortController()
    const timer = setTimeout(
      () => controller.abort(new Error(`shutdown receipt delivery timed out after ${budgetMs}ms`)),
      budgetMs,
    )
    timer.unref?.()
    try {
      const completed = await withTimeout(context.reportOnce(key, controller.signal), budgetMs)
      if (completed === null) throw new Error('shutdown receipt delivery budget expired')
    } finally {
      clearTimeout(timer)
    }
  }

  return { shutdownInFlight, persistInterrupted }
}

export function positiveBudget(value: number | undefined, fallback: number): number {
  return value !== undefined && Number.isFinite(value) && value > 0 ? Math.floor(value) : fallback
}

export function isSyntheticStopResult(result: WorkItemResult): boolean {
  const code = result.error?.code?.toLowerCase()
  if (result.status.toLowerCase() === 'unknown') return true
  if (code === 'interrupted' || code === 'timeout' || code === 'deadline-exceeded') return true
  const message = `${result.message ?? ''} ${result.error?.message ?? ''}`.toLowerCase()
  return message.includes('could not be confirmed stopped') || message.includes('did not confirm')
}

export function isShutdownFailureResult(result: WorkItemResult): boolean {
  const code = result.error?.code?.toLowerCase() ?? ''
  const message = `${result.message ?? ''} ${result.error?.message ?? ''}`.toLowerCase()
  return (
    code === 'turn-failed' ||
    code === 'runtime-unavailable' ||
    code === 'session-binding-failed' ||
    message.includes('abort') ||
    message.includes('transport') ||
    message.includes('unreachable')
  )
}

function operationNamesWork(operation: PendingUpdateOperation, work: DispatchWorkItem): boolean {
  const ownerKind = work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId
  return operation.affectedWorks.some(
    (candidate) =>
      candidate.ownerKind.toLowerCase() === ownerKind &&
      candidate.ownerId === ownerId &&
      candidate.workId === work.workId &&
      (candidate.taskRunId === undefined || candidate.taskRunId === null || candidate.taskRunId === work.taskRunId),
  )
}
