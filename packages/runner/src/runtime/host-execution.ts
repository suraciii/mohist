import { executeWork } from './host-task-log.js'
import { reportAndRequireDurableAck } from './work-report.js'
import { isShutdownFailureResult, isSyntheticStopResult } from './host-update-shutdown.js'
import { AWAITING_ACK_RETRY_INTERVAL_MS } from './host-timing.js'
import { runnerLogger } from '../system/logger.js'
import type { AwaitingAckEntry, InFlightEntry } from './host-state.js'
import type { DispatchWorkItem, RunnerOptions, WorkItemResult } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { WorkExecutor } from './executor.js'
import type { TerminalTaskLogDeliveryStore } from './terminal-task-log-delivery.js'
import type { HostTaskLogDeps } from './host-task-log.js'
import type { ManagerExecutionBoundary } from './manager-execution-boundary.js'
import type { RunnerHostShutdown } from './host-shutdown-types.js'

const log = runnerLogger.child('host')

export interface HostExecutionContext {
  readonly options: RunnerOptions
  readonly connection: ServerConnection
  readonly taskLogDeps: () => HostTaskLogDeps
  readonly workExecutorRef: () => WorkExecutor | null
  readonly terminalTaskLogDelivery: TerminalTaskLogDeliveryStore
  readonly terminalTaskLogDeliveryInFlight: Set<string>
  readonly syncOpenCodeWorkOwners: () => void
  readonly inFlight: Map<string, InFlightEntry>
  readonly awaitingAck: Map<string, { work: DispatchWorkItem; entry: AwaitingAckEntry }>
  readonly hostShutdown: RunnerHostShutdown
  readonly currentCatalogRevision: (runtime: string) => string | null
  readonly managerExecutionFor: (key: string) => ManagerExecutionBoundary | null
  readonly releaseManagerExecution: (key: string) => Promise<void>
}

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

function managerEpochChangedResult(): WorkItemResult {
  const message = 'manager_epoch_changed: the Server deployment epoch changed before this execution was confirmed'
  return {
    status: 'unknown',
    message,
    error: { code: 'manager_epoch_changed', message },
    exitCode: 1,
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
  await reportAndRequireDurableAck(
    context.connection,
    held.work,
    held.entry.result,
    held.entry.result.agentBinding,
    signal,
  )
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
    if (entry.retryAt !== null && (earliestRetryAt === null || entry.retryAt < earliestRetryAt))
      earliestRetryAt = entry.retryAt
  }
  if (earliestRetryAt === null) return context.options.pollIntervalMs
  return Math.min(context.options.pollIntervalMs, Math.max(0, earliestRetryAt - Date.now()))
}

export async function executeAndTransition(
  context: HostExecutionContext,
  work: DispatchWorkItem,
  signal: AbortSignal,
  key: string,
  entry: InFlightEntry,
): Promise<void> {
  try {
    await executeAndTransitionCore(context, work, signal, key, entry)
  } finally {
    await context.releaseManagerExecution(key)
  }
}

async function executeAndTransitionCore(
  context: HostExecutionContext,
  work: DispatchWorkItem,
  signal: AbortSignal,
  key: string,
  entry: InFlightEntry,
): Promise<void> {
  let result: WorkItemResult
  try {
    const runtime = workRuntime(work)
    if (work.capabilityRevision && runtime && context.currentCatalogRevision(runtime) !== work.capabilityRevision) {
      log.warn('rejecting stale capability snapshot before execution', {
        work: work.workId,
        runtime,
        frozen: work.capabilityRevision,
        current: context.currentCatalogRevision(runtime),
      })
      result = staleCapabilityResult(work)
    } else {
      result = await executeWork(
        context.taskLogDeps(),
        context.workExecutorRef()!,
        context.terminalTaskLogDeliveryInFlight,
        work,
        signal,
        context.managerExecutionFor(key),
      )
    }
  } catch (error) {
    if (signal.aborted && !entry.managerInvalidated) return
    if (entry.managerInvalidated) result = managerEpochChangedResult()
    else {
      const boundary = context.managerExecutionFor(key)
      const message = boundary ? boundary.mask(String(error)) : String(error)
      log.error('work failed before report', {
        work: work.workId,
        exception: message,
      })
      result = { status: 'failed', message }
    }
  }

  if (entry.managerInvalidated) result = managerEpochChangedResult()
  if (
    entry.shutdown?.requested &&
    (isSyntheticStopResult(result) || (!entry.shutdown.stopConfirmed && isShutdownFailureResult(result)))
  ) {
    context.inFlight.delete(key)
    context.syncOpenCodeWorkOwners()
    return
  }

  context.inFlight.delete(key)
  context.awaitingAck.set(key, {
    work,
    entry: { result, attempts: 0, retryAt: null },
  })
  context.syncOpenCodeWorkOwners()
  try {
    await reportOnce(context, key)
  } catch (error) {
    scheduleReportRetry(context, key)
    log.warn('first work report failed; will retry', {
      work: work.workId,
      exception: error,
    })
  }
}
