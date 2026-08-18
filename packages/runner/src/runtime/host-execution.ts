import { createTerminalRecoveryReceipt } from './recovery-receipt.js'
import { executeWork } from './host-task-log.js'
import { reportAndRequireDurableAck } from './work-report.js'
import { isShutdownFailureResult, isSyntheticStopResult } from './host-update-shutdown.js'
import { workKey } from './work-result-journal.js'
import { AWAITING_ACK_RETRY_INTERVAL_MS } from './host-timing.js'
import { runnerLogger } from '../system/logger.js'
import type { AwaitingAckEntry, InFlightEntry } from './host-state.js'
import type { DispatchWorkItem, RunnerOptions, WorkItemResult } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { WorkExecutor } from './executor.js'
import type { WorkResultJournal } from './work-result-journal.js'
import type { RecoveredStartedWork } from './recovered-started-work.js'
import type { RuntimeTurnRegistry } from './runtime-turn-registry.js'
import type { TerminalTaskLogDeliveryStore } from './terminal-task-log-delivery.js'
import type { HostTaskLogDeps } from './host-task-log.js'
import type { RunnerHostShutdown } from './host-shutdown-types.js'

const log = runnerLogger.child('host')

/**
 * Read-only context surface the host exposes to the execution helpers. The
 * helpers only call back into the host through this interface, so the host
 * keeps its private fields private while the worker-pool and result-
 * reporting logic lives in a sibling file under the size ratchet.
 */
export interface HostExecutionContext {
  readonly options: RunnerOptions
  readonly connection: ServerConnection
  readonly receiptId: () => string
  readonly taskLogDeps: () => HostTaskLogDeps
  readonly workExecutorRef: () => WorkExecutor | null
  readonly workResultJournal: WorkResultJournal
  readonly runtimeTurnRegistry: RuntimeTurnRegistry
  readonly recoveredStartedWork: RecoveredStartedWork
  readonly terminalTaskLogDelivery: TerminalTaskLogDeliveryStore
  readonly terminalTaskLogDeliveryInFlight: Set<string>
  readonly syncOpenCodeWorkOwners: () => void
  readonly inFlight: Map<string, InFlightEntry>
  readonly awaitingAck: Map<string, { work: DispatchWorkItem; entry: AwaitingAckEntry }>
  readonly hostShutdown: RunnerHostShutdown
  /**
   * The runner's currently-registered capability snapshot revision for a
   * runtime (issue-557 T-006). Used to reject a dispatch frozen against
   * an older catalog instead of executing with changed capability
   * semantics. Returns null when the runtime has no authoritative catalog.
   */
  readonly currentCatalogRevision: (runtime: string) => string | null
}

/**
 * The runtime a work item executes against, for capability-revision
 * validation. Mirrors `isWorkflowAgentWork` in work-report.ts so the
 * two runtime classification helpers stay consistent.
 */
function workRuntime(work: DispatchWorkItem): string | null {
  if (work.agentDefinition?.runtime) return work.agentDefinition.runtime.trim().toLowerCase()
  const uses = work.uses?.trim().toLowerCase()
  if (uses === 'mohist/opencode' || uses === 'mohist/pi') return uses.replace('mohist/', '')
  if ((work.ownerKind ?? '').trim().toLowerCase() === 'agent-job') {
    const runtime = typeof work.with?.runtime === 'string' ? work.with.runtime.trim().toLowerCase() : ''
    return runtime || null
  }
  return null
}

/**
 * A digestible rejection for a dispatch frozen against a catalog the
 * runner no longer holds. Carried to the server with `requeue` so the
 * work is re-pended for re-resolution rather than recorded as a
 * terminal (or silently executed) outcome.
 */
function staleCapabilityResult(work: DispatchWorkItem): WorkItemResult {
  const message = `dispatch capability revision '${work.capabilityRevision ?? ''}' no longer matches the runner's current catalog; the work was requeued for re-resolution`
  return {
    status: 'failed',
    message,
    requeue: true,
    error: { code: 'stale-capability-snapshot', message },
    exitCode: 1,
  }
}

export function markResultPersistencePending(context: HostExecutionContext, key: string): void {
  const held = context.inFlight.get(key)
  if (held) held.awaitingResultPersistence = true
}

export function promoteDurableJournalResults(context: HostExecutionContext, retryAt: number | null = null): string[] {
  if (!context.workResultJournal.ready()) return []
  const promoted: string[] = []
  for (const entry of context.workResultJournal.completed()) {
    const key = workKey(entry.work)
    if (context.awaitingAck.has(key)) continue
    context.inFlight.delete(key)
    context.awaitingAck.set(key, {
      work: entry.work,
      entry: {
        result: entry.result!,
        ...(entry.receipt ? { receipt: entry.receipt } : {}),
        attempts: 0,
        retryAt,
      },
    })
    promoted.push(key)
  }
  context.syncOpenCodeWorkOwners()
  return promoted
}

export async function promoteAndReportDurableJournalResults(context: HostExecutionContext): Promise<void> {
  for (const key of promoteDurableJournalResults(context)) {
    const held = context.awaitingAck.get(key)
    if (!held) continue
    try {
      await reportOnce(context, key)
    } catch (error) {
      scheduleReportRetry(context, key)
      log.warn('first work report failed; will retry', { work: held.work.workId, exception: error })
    }
  }
}

export async function reportOnce(
  context: HostExecutionContext,
  key: string,
  signal: AbortSignal = new AbortController().signal,
): Promise<void> {
  const held = context.awaitingAck.get(key)
  if (!held) return
  held.entry.attempts += 1
  if (held.entry.receipt) {
    const acknowledgement = await context.connection.sendRecoveryReceipt(held.entry.receipt, signal)
    if (acknowledgement.appliedReceiptId !== held.entry.receipt.receiptId)
      throw new Error('recovery receipt acknowledgement identity mismatch')
    if (acknowledgement.status === 'retryable') throw new Error('recovery receipt acknowledgement is retryable')
  } else {
    await reportAndRequireDurableAck(context.connection, held.work, held.entry.result)
  }
  await context.workResultJournal.acknowledge(held.work)
  context.awaitingAck.delete(key)
  context.syncOpenCodeWorkOwners()
}

export function scheduleReportRetry(context: HostExecutionContext, key: string): void {
  const held = context.awaitingAck.get(key)
  if (held) held.entry.retryAt = Date.now() + AWAITING_ACK_RETRY_INTERVAL_MS
}

export async function retryDueReports(context: HostExecutionContext): Promise<void> {
  const now = Date.now()
  const due = [...context.awaitingAck.entries()].filter(
    ([, held]) => held.entry.retryAt !== null && held.entry.retryAt <= now,
  )

  await Promise.all(
    due.map(async ([key, held]) => {
      held.entry.retryAt = null
      try {
        await reportOnce(context, key)
      } catch (error) {
        scheduleReportRetry(context, key)
        log.warn('work report retry failed', {
          work: held.work.workId,
          attempt: held.entry.attempts,
          exception: error,
        })
      }
    }),
  )
}

export function nextReconciliationInterval(context: HostExecutionContext): number {
  let earliestRetryAt: number | null = null
  for (const { entry } of context.awaitingAck.values()) {
    if (entry.retryAt !== null && (earliestRetryAt === null || entry.retryAt < earliestRetryAt)) {
      earliestRetryAt = entry.retryAt
    }
  }
  earliestRetryAt = context.recoveredStartedWork.earlierRetryAt(earliestRetryAt)
  if (earliestRetryAt === null) return context.options.pollIntervalMs
  return Math.min(context.options.pollIntervalMs, Math.max(0, earliestRetryAt - Date.now()))
}

export async function retryPendingWorkResultPersistence(context: HostExecutionContext): Promise<void> {
  if (!context.workResultJournal.needsPersistenceRecovery()) return
  const persistence = await context.workResultJournal.retryPendingPersistence()
  if (persistence.state === 'pending') {
    log.warn('work result journal persistence recovery is still unavailable', { exception: persistence.error })
    return
  }
  await promoteAndReportDurableJournalResults(context)
}

export async function executeAndTransition(
  context: HostExecutionContext,
  work: DispatchWorkItem,
  signal: AbortSignal,
  key: string,
  entry: InFlightEntry,
): Promise<void> {
  let result: WorkItemResult
  try {
    const runtime = workRuntime(work)
    if (work.capabilityRevision && runtime) {
      const current = context.currentCatalogRevision(runtime)
      if (current !== work.capabilityRevision) {
        log.warn('rejecting stale capability snapshot before execution', {
          work: work.workId,
          runtime,
          frozen: work.capabilityRevision,
          current,
        })
        result = staleCapabilityResult(work)
      } else {
        result = await executeWork(
          context.taskLogDeps(),
          context.workExecutorRef()!,
          context.terminalTaskLogDeliveryInFlight,
          work,
          signal,
        )
      }
    } else {
      result = await executeWork(
        context.taskLogDeps(),
        context.workExecutorRef()!,
        context.terminalTaskLogDeliveryInFlight,
        work,
        signal,
      )
    }
  } catch (error) {
    if (signal.aborted) return
    log.error('work failed before report', { work: work.workId, exception: error })
    result = { status: 'failed', message: String(error) }
  }

  if (entry.terminalPersisted) {
    context.runtimeTurnRegistry.remove(key)
    return
  }

  // Runtime cancellation returns an internal interrupted result so the
  // action can unwind. It is not a task outcome. A normal result that wins
  // the race remains authoritative and follows the terminal-result path.
  if (
    entry.shutdown?.requested &&
    (isSyntheticStopResult(result) || (!entry.shutdown.stopConfirmed && isShutdownFailureResult(result)))
  ) {
    if (entry.shutdown.stopConfirmed && entry.shutdown.operationId) {
      await context.hostShutdown.persistInterrupted(entry, entry.shutdown.operationId)
    } else {
      context.inFlight.delete(key)
      context.runtimeTurnRegistry.remove(key)
      context.syncOpenCodeWorkOwners()
    }
    return
  }

  const binding = context.runtimeTurnRegistry.get(key)
  const receipt = binding
    ? createTerminalRecoveryReceipt(work, binding, context.options.runnerId, result, context.receiptId())
    : undefined
  // A returned result is authoritative even when shutdown raced with its
  // delivery. Persist it before the host releases the work; only an abort
  // that prevented a result from returning stays as the started fence above.
  let persistence: Awaited<ReturnType<WorkResultJournal['complete']>>
  try {
    persistence = await context.workResultJournal.complete(work, result, receipt ?? undefined)
  } catch (error) {
    log.error('work result journal could not persist settled result', { work: work.workId, exception: error })
    // Keep the work in `inFlight` and stop admission. Reporting a result
    // without a durable local copy would turn a restart into result loss.
    markResultPersistencePending(context, key)
    context.workResultJournal.disable()
    return
  }

  if (persistence.state === 'pending' || !context.workResultJournal.ready()) {
    markResultPersistencePending(context, key)
    if (persistence.state === 'pending') {
      log.warn('work result journal persistence deferred; retaining result in memory', {
        work: work.workId,
        exception: persistence.error,
      })
    }
    context.runtimeTurnRegistry.remove(key)
    return
  }
  context.syncOpenCodeWorkOwners()

  await promoteAndReportDurableJournalResults(context)
  if (!context.workResultJournal.ready()) markResultPersistencePending(context, key)
}
