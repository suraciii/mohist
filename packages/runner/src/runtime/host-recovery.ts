import type { DispatchWorkItem, WorkItemResult, WorkflowTaskCompletionBoundary } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import { probeRuntimeBinding, reattachRuntimeTurn, resolvePersistedWorkBinding } from './binding-recovery.js'
import type { OpenCodeRuntime } from './opencode/index.js'
import type { PiRuntime } from './pi/index.js'
import { runnerLogger } from '../system/logger.js'
import { projectReattachedRuntimeResult, runtimeForKind, runtimeKindForWork } from './host-helpers.js'
import { runnerRestartedResult } from './work-report.js'
import type { WorkResultJournal } from './work-result-journal.js'

const log = runnerLogger.child('host')

type RecoveryInterruption = ReturnType<typeof runnerRestartedResult>['interruption']

export interface HostRecoveryContext {
  readonly connection: ServerConnection
  readonly runnerId: string
  readonly openCodeRuntime: OpenCodeRuntime | null
  readonly piRuntime: PiRuntime | null
  readonly workResultJournal: WorkResultJournal
  readonly removeInFlight: (key: string) => void
  readonly queueAwaitingAck: (
    key: string,
    work: DispatchWorkItem,
    result: WorkItemResult,
    boundary?: WorkflowTaskCompletionBoundary,
  ) => void
  readonly syncOpenCodeWorkOwners: () => void
  readonly reportOnce: (key: string) => Promise<void>
  readonly scheduleReportRetry: (key: string) => void
}

/** Reconciles a started journal entry without ever re-executing its prompt. */
export async function reconcileStartedDispatch(
  context: HostRecoveryContext,
  work: DispatchWorkItem,
  signal: AbortSignal,
  key: string,
): Promise<void> {
  let result: WorkItemResult
  let interruption: RecoveryInterruption | undefined
  try {
    const reconciliation = await reconcileStartedWork(context, work, signal)
    if (signal.aborted || reconciliation === null) {
      context.removeInFlight(key)
      context.syncOpenCodeWorkOwners()
      return
    }
    result = reconciliation.result
    interruption = reconciliation.interruption
  } catch (error) {
    context.removeInFlight(key)
    context.syncOpenCodeWorkOwners()
    log.warn('started work reconciliation deferred; retaining fence', { work: work.workId, exception: error })
    return
  }

  await completeAndQueueResult(context, work, result, key, interruption)
}

async function reconcileStartedWork(
  context: HostRecoveryContext,
  work: DispatchWorkItem,
  signal: AbortSignal,
): Promise<{ result: WorkItemResult; interruption?: RecoveryInterruption } | null> {
  if (signal.aborted) return null
  const runtimeKind = runtimeKindForWork(work)
  if (!runtimeKind) return runnerRestartedResult(work)

  const binding = await resolvePersistedWorkBinding(work, context.connection, context.runnerId, signal)
  if (binding.kind === 'unavailable') return null
  if (binding.kind !== 'bound') return runnerRestartedResult(work)

  const runtime = runtimeForKind(runtimeKind, context.openCodeRuntime, context.piRuntime)
  if (!runtime) return null
  const probe = await probeRuntimeBinding(runtime, binding.binding)
  if (!probe.ok) {
    // A transport/runtime failure is uncertainty, not proof that the physical turn died.
    if (probe.kind !== 'missing-session') return null
    return runnerRestartedResult(work)
  }
  if (!probe.activeTurn) return runnerRestartedResult(work)

  const adopted = await reattachRuntimeTurn(runtime, binding.binding, signal)
  if (signal.aborted) return null
  return { result: projectReattachedRuntimeResult(work, runtimeKind, adopted) }
}

/** Persists a terminal result before moving it into the awaiting-ack set. */
async function completeAndQueueResult(
  context: HostRecoveryContext,
  work: DispatchWorkItem,
  result: WorkItemResult,
  key: string,
  interruption?: RecoveryInterruption,
): Promise<void> {
  try {
    if (interruption) await context.workResultJournal.completeInterrupted(work, result, interruption)
    else await context.workResultJournal.complete(work, result)
  } catch (error) {
    log.error('work result journal could not persist settled result', { work: work.workId, exception: error })
    context.workResultJournal.disable()
    return
  }

  context.removeInFlight(key)
  context.queueAwaitingAck(key, work, result)
  context.syncOpenCodeWorkOwners()
  try {
    await context.reportOnce(key)
  } catch (error) {
    context.scheduleReportRetry(key)
    log.warn('first work report failed; will retry', { work: work.workId, exception: error })
  }
}
