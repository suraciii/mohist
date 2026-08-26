import type { DispatchWorkItem, RunnerOptions } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { OpenCodeRuntime } from './opencode/index.js'
import type { PiRuntime } from './pi/index.js'
import type { PendingUpdateOperation } from './update-operation.js'
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
  shutdown?: ShutdownWorkState
}

export interface HostShutdownContext {
  readonly options: RunnerOptions
  readonly connection: ServerConnection
  readonly openCodeRuntime: () => OpenCodeRuntime | null
  readonly piRuntime: () => PiRuntime | null
  readonly inFlight: Map<string, InFlightEntry>
  readonly awaitingAck: Map<string, { work: DispatchWorkItem; entry: AwaitingAckEntry }>
  readonly fetchPendingUpdateOperation: (signal: AbortSignal) => Promise<PendingUpdateOperation | null>
  readonly shutdownHandoffBudgetMs: number
  readonly shutdownStopBudgetMs: number
}

export function createHostShutdown(context: HostShutdownContext): RunnerHostShutdown {
  async function shutdownInFlight(): Promise<void> {
    const entries = [...context.inFlight.values()]
    if (entries.length === 0) return
    const operation = await pendingUpdateOperationForShutdown()
    const deadline = Date.now() + context.shutdownStopBudgetMs
    await Promise.all(entries.map((entry) => requestCooperativeStop(entry, operation, deadline)))
    await withTimeout(Promise.allSettled(entries.map((entry) => entry.done)), Math.max(0, deadline - Date.now()))
    for (const entry of entries) {
      if (!context.inFlight.has(workKey(entry.work))) continue
      entry.controller.abort()
      context.inFlight.delete(workKey(entry.work))
    }
  }

  async function pendingUpdateOperationForShutdown(): Promise<PendingUpdateOperation | null> {
    const deadline = Date.now() + context.shutdownHandoffBudgetMs
    let attempt = 0
    while (Date.now() <= deadline && attempt < SHUTDOWN_HANDOFF_ATTEMPTS) {
      attempt += 1
      const request = new AbortController()
      try {
        const response = await withTimeout(
          context.fetchPendingUpdateOperation(request.signal).then((value) => ({ value })),
          Math.max(1, deadline - Date.now()),
        )
        if (!response) continue
        if (response.value?.runnerId && response.value.runnerId !== context.options.runnerId) return null
        return response.value
      } catch (error) {
        if (Date.now() >= deadline) break
        log.warn('update shutdown handoff failed; retrying within bounded budget', { attempt, exception: error })
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
    const operationId = operation && operationNamesWork(operation, entry.work) ? operation.operationId : null
    entry.shutdown = { requested: true, stopConfirmed: false, operationId }
    entry.controller.abort()
    if (!operationId) return
    entry.shutdown.stopFailure = STOP_FAILURE_RECOVERY
    try {
      await withTimeout(
        context.connection.reportRecoveryStopFailure(
          {
            runnerId: context.options.runnerId,
            ownerKind: entry.work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow',
            ownerId: entry.work.ownerKind === 'agent-job' ? (entry.work.agentJobId ?? '') : entry.work.workflowRunId,
            workId: entry.work.workId,
            taskRunId: entry.work.taskRunId ?? null,
            operationId,
            message: STOP_FAILURE_RECOVERY,
          },
          new AbortController().signal,
        ),
        Math.max(1, deadline - Date.now()),
      )
    } catch (error) {
      log.warn('update stop failure could not be reported', {
        work: entry.work.workId,
        exception: error,
      })
    }
  }

  return { shutdownInFlight }
}

function operationNamesWork(operation: PendingUpdateOperation, work: DispatchWorkItem): boolean {
  const ownerKind = work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId
  return operation.affectedWorks.some(
    (entry) => entry.ownerKind === ownerKind && entry.ownerId === ownerId && entry.workId === work.workId,
  )
}

function workKey(work: DispatchWorkItem): string {
  const ownerKind = work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId
  return `${ownerKind}:${ownerId}:${work.workId}`
}

export function positiveBudget(value: number | undefined, fallback: number): number {
  return value !== undefined && Number.isFinite(value) && value >= 0 ? value : fallback
}

export function isSyntheticStopResult(result: { status?: string; error?: { code?: string } | null }): boolean {
  return result.status === 'interrupted' || result.error?.code === 'interrupted'
}

export function isShutdownFailureResult(result: { status?: string }): boolean {
  return result.status === 'failed' || result.status === 'unknown'
}
