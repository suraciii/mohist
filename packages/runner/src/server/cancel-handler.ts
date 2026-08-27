// The cancel handler does NOT consult event queue health — it is the one
// control WebSocket operation that must remain available while the event queue is unavailable. It captures the runtime via the host-owned
// invocation-time accessor at command time (a runtime initialized or
// replaced after control WebSocket client construction is therefore visible) and
// resolves the binding through the binding-only `followupTargetResolver`.
//
// The cancel reply carries `interruptUnconfirmed` whenever the bound
// runtime reports a stop it could not confirm. The flag is surfaced end-to-end so the API/user is never told a
// still-running turn has been safely stopped. OpenCode and Pi runtimes
// report their confirmation outcome; Pi's `cancel` reports
// `stopConfirmed: false` exactly when the upper layers must surface
// `interruptUnconfirmed: true`.
//
// When a Cancel is confirmed, the handler
// enqueues a binding-guarded `session.activity` fact through the host
// runtime-event queue so the grain's `ApplyRuntimeEventToDomain` →
// `ParseActivity` path settles activity: confirmed → `idle`, unconfirmed
// → `unknown` (an unconfirmed stop must never be reported as `idle`).
// The grain's `AppendEventsAsync(..., requireCurrentRuntimeBinding: true)`
// discards the fact if the binding has been superseded by a concurrent
// Reset / recovery. The queue is best-effort: if it is null or unavailable
// the cancel reply still flows to the caller, because cancel must remain
// available independently of evidence delivery.

import {
  sessionTargetFromWireTarget,
  type CancelAgentSessionPayload,
  type CancelAgentSessionReply,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
  type SessionTarget,
} from './session-target.js'
import {
  callCancel,
  ensureCommandRuntimeReady,
  readCancelFacts,
  resolveCommandRuntime,
  type CancelCallTarget,
  type CommandRuntimeAccessors,
} from './command-runtime.js'
import type { AgentSessionRuntimeEventQueue, RuntimeEventRecord } from './runtime-event-queue.js'
import type { ManagerExecutionRegistry, ManagerExecutionEntry } from '../runtime/manager-execution-registry.js'
import { runnerLogger } from '../system/logger.js'

const log = runnerLogger.child('session')

export interface CancelHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  openCodeRuntime?: CommandRuntimeAccessors['openCode']
  piRuntime?: CommandRuntimeAccessors['pi']
  agentSessionRuntimeEventQueue?: AgentSessionRuntimeEventQueue | null
  managerExecutionRegistry?: ManagerExecutionRegistry | null
  onManagerExecutionFinished?: (executionId: string) => Promise<void> | void
}

export function createCancelHandler(
  deps: CancelHandlerDeps,
): (payload: CancelAgentSessionPayload | null | undefined) => Promise<CancelAgentSessionReply> {
  const inFlight = new Map<string, Promise<CancelAgentSessionReply>>()
  return async (payload: CancelAgentSessionPayload | null | undefined) => {
    const key = operationKey(payload)
    if (!key) return await handleCancel(payload, deps)
    const existing = inFlight.get(key)
    if (existing) return await existing
    const operation = handleCancel(payload!, deps)
    inFlight.set(key, operation)
    try {
      return await operation
    } finally {
      if (inFlight.get(key) === operation) inFlight.delete(key)
    }
  }
}

async function reconcileStartedStop(
  payload: CancelAgentSessionPayload,
  deps: CancelHandlerDeps,
): Promise<'active' | 'idle' | 'missing' | 'indeterminate'> {
  const sessionTarget = payload.target ? sessionTargetFromWireTarget(payload.target) : null
  const binding = sessionTarget?.binding
  if (!binding || !binding.workDir) return 'indeterminate'

  const managerExecution =
    deps.managerExecutionRegistry?.findForCancel(
      sessionTarget.kind === 'workflow' ? (sessionTarget.agentSessionId ?? '') : sessionTarget.sessionId,
      binding.runtime,
      binding.runtimeSessionId,
    ) ?? null
  const handle =
    managerExecution?.handle ??
    resolveCommandRuntime(binding, {
      openCode: deps.openCodeRuntime,
      pi: deps.piRuntime,
    })
  if (!handle || !handle.runtime.ready()) return 'indeterminate'

  try {
    const result =
      handle.kind === 'opencode'
        ? await handle.runtime.resolveSession({
            target: {
              runtime: 'opencode',
              runtimeSessionId: binding.runtimeSessionId,
              workDir: binding.workDir,
            },
          })
        : await handle.runtime.resolveSession({
            target: {
              runtime: 'pi',
              runtimeSessionId: binding.runtimeSessionId,
              workDir: binding.workDir,
            },
          })
    if (result.ok) return result.value.activeTurn ? 'active' : 'idle'
    return readErrorKind(result) === 'missing-session' ? 'missing' : 'indeterminate'
  } catch (error) {
    log.error('cancel stop reconciliation probe threw', {
      exception: error,
      session: binding.runtimeSessionId,
    })
    return 'indeterminate'
  }
}

async function handleCancel(
  payload: CancelAgentSessionPayload | null | undefined,
  deps: CancelHandlerDeps,
): Promise<CancelAgentSessionReply> {
  if (!payload || !payload.target) {
    return { state: 'unavailable' }
  }

  const sessionTarget = sessionTargetFromWireTarget(payload.target)
  if (!sessionTarget) return { state: 'unavailable' }
  const binding = sessionTarget.binding
  if (!binding) return { state: 'unavailable' }

  const resolver = deps.followupTargetResolver ?? null
  if (!resolver) return { state: 'unavailable' }
  const managerExecution =
    deps.managerExecutionRegistry?.findForCancel(
      sessionTarget.kind === 'workflow' ? (sessionTarget.agentSessionId ?? '') : sessionTarget.sessionId,
      binding.runtime,
      binding.runtimeSessionId,
    ) ?? null
  const handle =
    managerExecution?.handle ??
    resolveCommandRuntime(binding, {
      openCode: deps.openCodeRuntime,
      pi: deps.piRuntime,
    })
  if (!handle) return { state: 'unavailable' }
  if (!(await ensureCommandRuntimeReady(handle))) {
    return { state: 'unavailable' }
  }

  let resolved: FollowupTargetResolution
  try {
    resolved = await resolver(sessionTarget)
  } catch (error) {
    log.error('cancel target resolver threw', { exception: error })
    return { state: 'unavailable' }
  }

  if (!resolved) {
    const settled = await settleWithoutLiveTarget(payload, deps)
    if (managerExecution && settled.state !== 'not-cancellable' && settled.state !== 'unavailable')
      await finishManagerExecution(managerExecution, deps)
    return settled
  }

  try {
    const workDir = binding.workDir
    if (!workDir) return { state: 'unavailable' }
    const cancelTarget: CancelCallTarget = {
      runtime: binding.runtime,
      runtimeSessionId: binding.runtimeSessionId,
      workDir,
    }
    const result = await callCancel(handle, cancelTarget)
    if (!result.ok) {
      const kind = readErrorKind(result)
      if (kind === 'unavailable-runtime') {
        return { state: 'unavailable' }
      }
      if (kind === 'missing-session') {
        const settled = await settleWithoutLiveTarget(payload, deps)
        if (managerExecution && settled.state !== 'not-cancellable' && settled.state !== 'unavailable')
          await finishManagerExecution(managerExecution, deps)
        return settled
      }
      log.error('cancel runtime.cancel rejected', {
        reason: readErrorMessage(result),
        session: binding.runtimeSessionId,
      })
      return { state: 'stop-requested' }
    }
    const facts = readCancelFacts(result)
    if (!facts || !facts.cancelled) {
      return { state: 'not-cancellable' }
    }
    const confirmed = facts.stopConfirmed === true
    try {
      await recordCancelActivity(
        deps.agentSessionRuntimeEventQueue ?? null,
        sessionTarget,
        binding.runtimeSessionId,
        payload.turnId,
        payload.operationId,
        { ...facts, stopConfirmed: confirmed },
      )
    } catch (outboxError) {
      log.error('failed to enqueue cancel activity', { session: binding.runtimeSessionId, exception: outboxError })
      return { state: 'stop-requested' }
    }
    const reply =
      handle.kind === 'pi' && facts.stopConfirmed === false
        ? ({ state: 'unknown', interruptUnconfirmed: true } as const)
        : confirmed
          ? ({ state: 'stopped' } as const)
          : ({ state: 'stop-requested' } as const)
    if (managerExecution && (confirmed || (handle.kind === 'pi' && facts.stopConfirmed === false)))
      await finishManagerExecution(managerExecution, deps)
    return reply
  } catch (error) {
    log.error('cancel runtime.cancel threw', { exception: error, session: binding.runtimeSessionId })
    return { state: 'stop-requested' }
  }
}

async function settleWithoutLiveTarget(
  payload: CancelAgentSessionPayload,
  deps: CancelHandlerDeps,
): Promise<CancelAgentSessionReply> {
  const reconciliation = await reconcileStartedStop(payload, deps)
  if (reconciliation === 'missing') return { state: 'ended' }
  if (reconciliation === 'idle') return { state: 'idle' }
  if (reconciliation === 'active') return { state: 'not-cancellable' }
  return { state: 'unavailable' }
}

async function recordCancelActivity(
  outbox: AgentSessionRuntimeEventQueue | null,
  sessionTarget: SessionTarget,
  runtimeSessionId: string,
  turnId: string | undefined,
  operationId: string | undefined,
  facts: { readonly cancelled: boolean; readonly stopConfirmed: boolean },
): Promise<void> {
  if (!outbox) return
  if (sessionTarget.kind === 'workflow') return
  const activity = facts.stopConfirmed ? 'idle' : 'unknown'
  const completedAt = new Date().toISOString()
  const record: RuntimeEventRecord = {
    id: `cancel-activity:${runtimeSessionId}:${activity}:${completedAt}:${Math.random().toString(36).slice(2, 10)}`,
    producerFamily: 'generic-followup',
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId,
    work: null,
    event: {
      type: 'session.activity',
      payload: {
        activity,
        status: facts.stopConfirmed ? 'completed' : 'failed',
        source: 'cancel',
        ...(turnId ? { turnId } : {}),
        ...(operationId ? { stopOperationId: operationId } : {}),
        stopConfirmed: facts.stopConfirmed,
        runtimeSessionId,
        completedAt,
      },
    },
    acknowledgementPolicy: 'successful-response',
  }
  await outbox.enqueueProducedFact(record)
}

async function finishManagerExecution(entry: ManagerExecutionEntry, deps: CancelHandlerDeps): Promise<void> {
  if (deps.managerExecutionRegistry) await deps.managerExecutionRegistry.dispose(entry.boundary)
  else await entry.boundary.dispose().catch(() => undefined)
  try {
    await deps.onManagerExecutionFinished?.(entry.executionId)
  } catch (error) {
    log.error('failed to revoke Manager execution after cancellation', { exception: error, session: entry.sessionId })
  }
}

function operationKey(payload: CancelAgentSessionPayload | null | undefined): string | null {
  return payload?.sessionId && payload.operationId ? `${payload.sessionId}:${payload.operationId}` : null
}

function sessionTargetToRuntimeTarget(target: SessionTarget): RuntimeEventRecord['target'] {
  if (target.kind === 'workflow') {
    return {
      kind: 'workflow',
      projectId: target.projectId,
      workflowRunId: target.workflowRunId,
      sessionName: target.sessionName,
    }
  }
  return { kind: 'generic', projectId: target.projectId, sessionId: target.sessionId }
}

function readErrorKind(result: { readonly error?: { readonly kind?: string } }): string {
  return result.error?.kind ?? ''
}

function readErrorMessage(result: { readonly error?: { readonly message?: string } }): string {
  return result.error?.message ?? 'runtime error'
}
