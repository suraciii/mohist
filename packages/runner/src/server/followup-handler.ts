// The follow-up handler routes both input and operation-correlated
// terminal outcomes through the host-owned
// `AgentSessionRuntimeEventQueue`.
//
// Behaviour:
//   - drops silently on null / missing payload, missing/empty input, no
//     resolver, resolver returning null, resolver throwing (logged)
//   - resolves the runtime accessor at invocation time (host-owned
//     late binding), so a runtime built or replaced after
//     control WebSocket client construction is visible to later commands
//   - admits a follow-up command only when (a) the binding resolves and
//     (b) the captured runtime is ready and (c) the volatile queue is healthy;
//     otherwise returns `{ accepted: false, error: "unavailable" }`
//     without enqueuing input or invoking the runtime
//   - dispatches to the binding's runtime:
//     the wire binding's `runtime` field selects between the OpenCode
//     and Pi backends; an unknown or not-ready runtime reports
//     `unavailable` and the command is not silently dropped
//   - enqueues a `session.input` record through `enqueueBeforeExecution`
//     and waits for its authoritative Server receipt before invoking
//     `runtime.followup`; queue admission or receipt failure returns
//     `unavailable` without invoking the runtime
//   - invokes `runtime.followup` exactly once; the resolve/reject
//     handler enqueues the corresponding session.activity record
//   - later evidence delivery failure does NOT change the accepted result
//     and does NOT re-invoke the prompt; the volatile queue retries it only
//     while this process remains alive

import { errorMessage } from '../core/errors.js'
import {
  resolveSessionTarget,
  type FollowupTarget,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
  type ReceiveFollowupPayload,
  type SessionTarget,
} from './session-target.js'
import type { AgentSessionRuntimeEventQueue, RuntimeEventRecord } from './runtime-event-queue.js'
import {
  callFollowup,
  ensureCommandRuntimeReady,
  resolveCommandRuntime,
  type CommandRuntimeAccessors,
} from './command-runtime.js'
import type { PiRuntimeEvent, PiTurnObserver } from '../runtime/pi/index.js'
import type { RuntimeTurnEvent, RuntimeTurnObserver } from '../runtime/opencode/index.js'
import type { ServerConnection } from './connection.js'
import { SkillResolver } from '../runtime/skill-resolver.js'
import { ManagerExecutionBoundary } from '../runtime/manager-execution-boundary.js'
import { ManagerExecutionRegistry } from '../runtime/manager-execution-registry.js'
import { mapRuntimeErrorKind } from '../runtime/error-kind-mapping.js'
import { buildExecutionEnvelope } from '../runtime/execution-envelope.js'
import { inlineSlackCollaborationSkill, readExecutionSourceContext } from '../runtime/slack-execution-context.js'
import {
  ReplyActionObservationTracker,
  ReplyGuardCoordinator,
  type ReplyGuardAdvisoryResult,
} from '../runtime/reply-guard.js'
import { runnerLogger } from '../system/logger.js'
import {
  attachmentManifestEnvelope,
  deliverAcceptedAttachments,
  parseAttachmentDescriptors,
  type DeliveredAttachment,
} from '../runtime/attachment-delivery.js'
import { BindingRecoveryCoordinator, resolveOrRecoverBinding } from '../runtime/binding-recovery.js'

const log = runnerLogger.child('session')

export interface FollowupHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  agentSessionRuntimeEventQueue?: AgentSessionRuntimeEventQueue | null
  openCodeRuntime?: CommandRuntimeAccessors['openCode']
  piRuntime?: CommandRuntimeAccessors['pi']
  connection?: ServerConnection | null
  runnerId?: string | null
  runnerRoot?: string
  managerExecutionRegistry?: ManagerExecutionRegistry | null
  onManagerExecutionFinished?: (executionId: string) => Promise<void> | void
  createManagerExecutionBoundary?: typeof ManagerExecutionBoundary.create
  randomId?: () => string
  skillResolver?: SkillResolver
  strictExecutionSourceValidation?: boolean
  bindingRecoveryCoordinator?: BindingRecoveryCoordinator | null
}

export interface FollowupDeliveryResult {
  accepted: boolean
  error?: 'missing' | 'unavailable'
}

export function createFollowupHandler(
  deps: FollowupHandlerDeps,
): (payload: ReceiveFollowupPayload | null | undefined) => Promise<FollowupDeliveryResult> {
  const inFlight = new Map<string, Promise<FollowupDeliveryResult>>()
  const bindingRecoveryCoordinator = deps.bindingRecoveryCoordinator ?? new BindingRecoveryCoordinator()
  const handlerDeps = { ...deps, bindingRecoveryCoordinator }
  return async (payload: ReceiveFollowupPayload | null | undefined) => {
    const key = followupOperationKey(payload)
    if (!key) return await handleFollowup(payload, handlerDeps)

    const existing = inFlight.get(key)
    if (existing) return await existing

    const operation = handleFollowup(payload, handlerDeps)
    inFlight.set(key, operation)
    try {
      return await operation
    } finally {
      if (inFlight.get(key) === operation) inFlight.delete(key)
    }
  }
}

export function defaultFollowupRecordId(): string {
  return `fup_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`
}

async function handleFollowup(
  payload: ReceiveFollowupPayload | null | undefined,
  deps: FollowupHandlerDeps,
): Promise<FollowupDeliveryResult> {
  if (!payload) return unavailable()
  const sourceContext = readExecutionSourceContext(payload, {
    strict: deps.strictExecutionSourceValidation === true,
  })
  if (sourceContext.kind === 'invalid') return unavailable()
  if (sourceContext.kind === 'legacy') log.warn('accepted source-less follow-up through the bounded legacy path')
  const slackContext = sourceContext.slackExecutionContext
  const text = typeof payload.text === 'string' ? payload.text : ''
  const descriptors = parseAttachmentDescriptors(payload.attachments)
  if (text.trim().length === 0 && descriptors.length === 0) return unavailable()
  const resolver = deps.followupTargetResolver ?? null
  const outbox = deps.agentSessionRuntimeEventQueue ?? null
  if (!resolver || !outbox) return unavailable()
  if (!outbox.ready()) return unavailable()

  const sessionTarget = resolveSessionTarget(payload)
  if (!sessionTarget) return unavailable()
  if (!sessionTargetId(sessionTarget) || !payload.turnId) return unavailable()

  const managerContext = isManagerSlackContext(slackContext)
  if (managerContext !== Boolean(payload.managerExecutionGrant)) return unavailable()

  const operationId = payload.operationId

  let target: FollowupTargetResolution
  try {
    const resolved = resolver(sessionTarget)
    target = isPromise(resolved) ? await resolved : resolved
  } catch (error) {
    log.error('followup target resolver threw', { exception: error })
    return unavailable()
  }
  if (!target) return { accepted: false, error: 'missing' }

  const binding = sessionTarget.binding
  if (!binding) return { accepted: false, error: 'missing' }

  let managerExecution: ManagerExecutionBoundary | null = null
  let handle = resolveCommandRuntime(binding, {
    openCode: deps.openCodeRuntime,
    pi: deps.piRuntime,
  })
  if (managerContext) {
    if (!deps.runnerRoot) return unavailable()
    try {
      managerExecution = await (deps.createManagerExecutionBoundary ?? ManagerExecutionBoundary.create)(
        payload.managerExecutionGrant!,
        deps.runnerRoot,
        {
          workDir: target.workDir,
        },
      )
      if (binding.runtime.toLowerCase() === 'opencode') {
        const isolated = await managerExecution.openCodeRuntime(target.workDir, new AbortController().signal)
        if (!isolated) {
          await managerExecution.dispose()
          return unavailable()
        }
        handle = { kind: 'opencode', runtime: isolated }
      }
    } catch (error) {
      await managerExecution?.dispose().catch(() => undefined)
      log.error('Manager follow-up boundary could not be established', {
        exception: error,
      })
      return unavailable()
    }
  }
  if (!handle) {
    await managerExecution?.dispose().catch(() => undefined)
    return unavailable()
  }
  if (!(await ensureCommandRuntimeReady(handle))) {
    await managerExecution?.dispose().catch(() => undefined)
    return unavailable()
  }

  let selectedTarget = target
  const connection = deps.connection ?? null
  const runnerId = deps.runnerId ?? null
  if (!managerContext && connection && runnerId) {
    const expected = {
      runnerId: binding.runnerId,
      runtime: handle.kind,
      runtimeSessionId: target.runtimeSessionId,
      workDir: target.workDir,
    } as const
    const recovery = await resolveOrRecoverBinding({
      runnerId,
      expected,
      runtime: handle,
      probe: async (candidate) => {
        const result =
          handle.kind === 'opencode'
            ? await handle.runtime.resolveSession({
                target: {
                  runtime: 'opencode',
                  runtimeSessionId: candidate.runtimeSessionId,
                  workDir: candidate.workDir,
                },
              })
            : await handle.runtime.resolveSession({
                target: {
                  runtime: 'pi',
                  runtimeSessionId: candidate.runtimeSessionId,
                  workDir: candidate.workDir,
                },
              })
        return result.ok
          ? { ok: true, activeTurn: result.value.activeTurn }
          : { ok: false, kind: result.error.kind, message: result.error.message }
      },
      replace: async (current, replacement) => {
        const body = {
          expectedRunnerId: current.runnerId,
          expectedRuntime: current.runtime,
          expectedRuntimeSessionId: current.runtimeSessionId,
          replacementRuntimeSessionId: replacement.runtimeSessionId,
          expectedQueuedTurnId: payload.turnId,
        }
        const signal = new AbortController().signal
        if (sessionTarget.kind === 'workflow') {
          await connection.recoverMissingWorkflowAgentSession(
            sessionTarget.projectId,
            sessionTarget.workflowRunId,
            sessionTarget.sessionName,
            body,
            signal,
          )
        } else {
          await connection.recoverMissingAgentSession(sessionTarget.projectId, sessionTarget.sessionId, body, signal)
        }
      },
      recoveryKey: `${sessionTargetId(sessionTarget)}:${expected.runtimeSessionId ?? 'unbound'}`,
      coordinator: deps.bindingRecoveryCoordinator ?? undefined,
    })
    if (!recovery.ok || !recovery.binding.runtimeSessionId) return unavailable()
    selectedTarget = { ...target, runtimeSessionId: recovery.binding.runtimeSessionId }
  }

  const definition = sessionTarget.kind === 'generic' ? target.definition : undefined
  const resolvedSkills = await (deps.skillResolver ?? new SkillResolver()).resolve(
    definition?.skills,
    selectedTarget.workDir,
  )
  if (!resolvedSkills.ok) {
    await managerExecution?.dispose().catch(() => undefined)
    return unavailable()
  }
  const skills = slackContext
    ? [...resolvedSkills.skills, inlineSlackCollaborationSkill(slackContext)]
    : resolvedSkills.skills

  const attachmentDelivery = await deliverAcceptedAttachments(
    {
      projectId: sessionTarget.projectId,
      agentSessionId: sessionTarget.kind === 'generic' ? sessionTarget.sessionId : (sessionTarget.agentSessionId ?? ''),
      inputId: payload.inputId ?? '',
      workDir: selectedTarget.workDir,
      connection,
      signal: new AbortController().signal,
    },
    descriptors,
  )
  const deliveredAttachments = attachmentDelivery.attachments
  const composedPrompt = attachmentManifestEnvelope(
    buildExecutionEnvelope(text, definition?.instructions, skills, slackContext),
    deliveredAttachments,
  )
  const fileParts = deliveredAttachments.flatMap((entry) =>
    entry.status === 'delivered' && entry.filePart ? [entry.filePart] : [],
  )

  try {
    await enqueueFollowupInput(
      outbox,
      sessionTarget,
      selectedTarget,
      payload,
      text,
      deps.randomId ?? defaultFollowupRecordId,
    )
  } catch (error) {
    log.error('followup input admission failed', {
      exception: error,
      session: sessionTarget.kind,
    })
    await managerExecution?.dispose().catch(() => undefined)
    return unavailable()
  }

  const followupRequest = {
    target: {
      runtime: binding.runtime,
      runtimeSessionId: selectedTarget.runtimeSessionId,
      workDir: selectedTarget.workDir,
    },
    prompt: composedPrompt,
    ...(fileParts.length > 0 ? { fileParts } : {}),
    ...(definition
      ? {
          options: {
            model: definition.model ?? null,
            variant: definition.variant ?? null,
            reasoningEffort: definition.reasoningEffort ?? null,
            ...(skills.length > 0 ? { skills } : {}),
          },
        }
      : {}),
    ...(managerExecution ? { managerExecution } : {}),
  }
  if (managerExecution && deps.managerExecutionRegistry) {
    deps.managerExecutionRegistry.register({
      executionId: payload.managerExecutionGrant!.executionId,
      boundary: managerExecution,
      handle,
      sessionId: sessionTargetId(sessionTarget),
      runtimeSessionId: selectedTarget.runtimeSessionId,
      workDir: selectedTarget.workDir,
    })
  }

  const observerState = buildFollowupObserver(
    outbox,
    sessionTarget,
    selectedTarget,
    payload.operationId,
    payload.turnId,
    managerExecution,
  )
  let terminalFinalized = false
  try {
    const completion = callFollowup(handle, followupRequest, observerState.observer).then(
      async (result) => {
        if (terminalFinalized) return
        terminalFinalized = true
        const observerError = await observerState.flush()
        if (!result.ok) {
          const message = readErrorMessage(result)
          recordFollowupActivity(
            outbox,
            sessionTarget,
            selectedTarget,
            payload.operationId,
            payload.turnId,
            isUncertainFollowupFailure(readErrorKind(result)) ? 'unknown' : 'idle',
            message,
            undefined,
            managerExecution,
            readRuntimeErrorCategory(handle.kind, result),
          )
          if (readErrorKind(result) === 'unavailable-runtime') {
            log.error('followup runtime unavailable', {
              reason: message,
              session: selectedTarget.runtimeSessionId,
            })
          }
          return
        }
        if (observerError) {
          recordFollowupActivity(
            outbox,
            sessionTarget,
            selectedTarget,
            payload.operationId,
            payload.turnId,
            'unknown',
            observerError,
            undefined,
            managerExecution,
          )
          return
        }
        if (handle.kind === 'opencode' && !managerExecution?.hasExpired()) {
          try {
            await evaluateFollowupReplyGuard({
              handle,
              selectedTarget,
              payload,
              observer: observerState,
              managerExecution,
            })
          } catch (error) {
            log.error('followup reply guard failed', {
              exception: error,
              session: selectedTarget.runtimeSessionId,
            })
          }
        }
        recordFollowupActivity(
          outbox,
          sessionTarget,
          selectedTarget,
          payload.operationId,
          payload.turnId,
          'idle',
          undefined,
          readFollowupOutput(result),
          managerExecution,
        )
      },
      (error) => {
        if (terminalFinalized) return
        terminalFinalized = true
        log.error('followup runtime.followup rejected', {
          exception: error,
          session: selectedTarget.runtimeSessionId,
        })
        recordFollowupActivity(
          outbox,
          sessionTarget,
          selectedTarget,
          payload.operationId,
          payload.turnId,
          'unknown',
          error,
          undefined,
          managerExecution,
        )
      },
    )
    void completion.finally(async () => {
      if (managerExecution) {
        if (deps.managerExecutionRegistry) await deps.managerExecutionRegistry.dispose(managerExecution)
        else await managerExecution.dispose().catch(() => undefined)
        await deps.onManagerExecutionFinished?.(payload.managerExecutionGrant?.executionId ?? '')
      }
    })
  } catch (error) {
    log.error('followup runtime.followup threw', {
      exception: error,
      session: selectedTarget.runtimeSessionId,
    })
    recordFollowupActivity(
      outbox,
      sessionTarget,
      selectedTarget,
      payload.operationId,
      payload.turnId,
      'unknown',
      error,
      undefined,
      managerExecution,
    )
    if (managerExecution) {
      if (deps.managerExecutionRegistry) await deps.managerExecutionRegistry.dispose(managerExecution)
      else await managerExecution.dispose().catch(() => undefined)
      await deps.onManagerExecutionFinished?.(payload.managerExecutionGrant?.executionId ?? '')
    }
    return unavailable()
  }
  return { accepted: true }
}

async function revokeFinishedManagerExecution(
  executionId: string | undefined,
  deps: FollowupHandlerDeps,
): Promise<void> {
  try {
    await deps.onManagerExecutionFinished?.(executionId ?? '')
  } catch (error) {
    log.error('failed to revoke Manager execution after duplicate delivery', {
      exception: error,
    })
  }
}

async function evaluateFollowupReplyGuard(args: {
  handle: NonNullable<ReturnType<typeof resolveCommandRuntime>>
  selectedTarget: FollowupTarget
  payload: ReceiveFollowupPayload
  observer: ReturnType<typeof buildFollowupObserver>
  managerExecution: ManagerExecutionBoundary | null
}): Promise<void> {
  const guard = new ReplyGuardCoordinator({
    runtime: {
      kind: args.handle.kind,
      isAvailable: () => args.handle.runtime.ready(),
    },
    runtimeSessionId: args.selectedTarget.runtimeSessionId,
    workDir: args.selectedTarget.workDir,
    slackExecutionContext: args.payload.slackExecutionContext,
    observation: args.observer.observation,
    signal: new AbortController().signal,
    runAdvisory: async (request) => {
      const result = await callFollowup(
        args.handle,
        {
          target: {
            runtime: args.handle.kind,
            runtimeSessionId: request.runtimeSessionId,
            workDir: request.workDir,
          },
          prompt: request.prompt,
          managerExecution: args.managerExecution,
          options: { skills: [request.collaborationSkill] },
        },
        args.observer.observer,
        request.signal,
      )
      await args.observer.flush()
      if (!result.ok) return replyGuardAdvisoryResult(readErrorKind(result))
      return { kind: 'completed' }
    },
  })
  await guard.evaluate(undefined)
}

function replyGuardAdvisoryResult(errorKind: string): ReplyGuardAdvisoryResult {
  if (errorKind === 'unavailable-runtime') return { kind: 'unavailable' }
  if (errorKind === 'interrupted' || errorKind === 'deadline-exceeded') return { kind: 'interrupted' }
  return { kind: 'failed' }
}

function sessionTargetKey(target: SessionTarget): string {
  return `session:${sessionTargetId(target)}`
}

function followupOperationKey(payload: ReceiveFollowupPayload | null | undefined): string | null {
  if (!payload?.operationId) return null
  const target = resolveSessionTarget(payload)
  return target ? `${sessionTargetKey(target)}:${payload.operationId}` : null
}

function isUncertainFollowupFailure(kind: string): boolean {
  return kind === 'unavailable-runtime' || kind === 'deadline-exceeded' || kind === 'generation-drain-timeout'
}

function isManagerSlackContext(value: unknown): boolean {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const anchor = (value as { readonly replyAnchor?: unknown }).replyAnchor
  if (!anchor || typeof anchor !== 'object' || Array.isArray(anchor)) return false
  const candidate = anchor as {
    readonly projectId?: unknown
    readonly ownerKind?: unknown
  }
  return candidate.projectId === '__mohist_slack_manager__' && candidate.ownerKind === 'manager'
}

function buildFollowupObserver(
  outbox: AgentSessionRuntimeEventQueue,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
  turnId: string | undefined,
  managerExecution: ManagerExecutionBoundary | null = null,
): {
  observer: PiTurnObserver | RuntimeTurnObserver | null
  observation: ReplyActionObservationTracker
  flush: () => Promise<unknown>
} {
  const observation = new ReplyActionObservationTracker()
  const pending: Promise<void>[] = []
  let observerError: unknown = null
  let openCodeEventOrdinal = 0
  if (!operationId) {
    const observer: PiTurnObserver | RuntimeTurnObserver = {
      onEvent: (event: PiRuntimeEvent | RuntimeTurnEvent) => observation.observe(event),
    }
    return { observer, observation, flush: async () => null }
  }
  const completedAt = new Date().toISOString()
  const observer: PiTurnObserver | RuntimeTurnObserver = {
    onEvent: (event: PiRuntimeEvent | RuntimeTurnEvent) => {
      observation.observe(event)
      const ordinal = 'id' in event ? 0 : ++openCodeEventOrdinal
      const eventPayload = {
        ...event.payload,
        turnId,
        source: 'followup',
        operationId,
        runtimeSessionId: target.runtimeSessionId,
        completedAt,
      }
      const record: RuntimeEventRecord = {
        id: `followup-event:${operationId}:${followupEventId(event, ordinal)}`,
        producerFamily: 'session-followup',
        target: sessionTargetToRuntimeTarget(sessionTarget),
        runtimeSessionId: target.runtimeSessionId,
        sessionTurnId: turnId,
        work: null,
        event: {
          type: event.type,
          payload: managerExecution ? (managerExecution.redact(eventPayload) as Record<string, unknown>) : eventPayload,
        },
        acknowledgementPolicy: 'successful-response',
      }
      const enqueue = outbox.enqueueProducedFact(record)
      pending.push(enqueue)
      enqueue.catch((outboxError) => {
        observerError ??= outboxError
        log.error('failed to enqueue followup runtime event', {
          session: target.runtimeSessionId,
          exception: outboxError,
        })
      })
    },
  }
  return {
    observer,
    observation,
    flush: async () => {
      await Promise.allSettled(pending)
      return observerError
    },
  }
}

function followupEventId(event: PiRuntimeEvent | RuntimeTurnEvent, ordinal: number): string {
  if ('id' in event && typeof event.id === 'string') return event.id
  return `ordinal-${ordinal}`
}

function readFollowupOutput(result: { readonly ok: true; readonly value: unknown }): string | null {
  const value = result.value as {
    readonly finalAssistantText?: unknown
    readonly facts?: { readonly finalAssistantText?: unknown }
  }
  const text = value.facts?.finalAssistantText ?? value.finalAssistantText
  return typeof text === 'string' && text.length > 0 ? text : null
}

function readErrorMessage(result: { readonly error?: { readonly message?: string } }): string {
  return result.error?.message ?? 'followup runtime error'
}

function readErrorKind(result: { readonly error?: { readonly kind?: string } }): string {
  return result.error?.kind ?? ''
}

function readRuntimeErrorCategory(
  runtime: 'opencode' | 'pi',
  result: { readonly ok: boolean; readonly error?: { readonly kind?: string } },
): string | undefined {
  if (result.ok) return undefined
  const kind = result.error?.kind
  return kind ? mapRuntimeErrorKind(runtime, kind) : undefined
}

async function enqueueFollowupInput(
  outbox: AgentSessionRuntimeEventQueue,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  payload: ReceiveFollowupPayload,
  text: string,
  randomId: () => string,
): Promise<void> {
  // Issue-522 T-001 D1: when the server mints a stable inputId and
  // turnId on the AgentSession grain before dispatching the follow-up,
  // use them as the runtime-event record id and canonical correlation
  // key. The Runner still emits the `session.input` event so the
  // Server can promote the Queued Turn to Executing (D8), but the
  // Server does NOT create a duplicate SessionInput/AgentTurn — the
  // record id is the one the Server already minted.
  const recordId = payload.inputId ?? randomId()
  const record: RuntimeEventRecord = {
    id: recordId,
    producerFamily: 'session-followup',
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId: target.runtimeSessionId,
    sessionTurnId: payload.turnId,
    work: null,
    event: {
      type: 'session.input',
      payload: {
        role: 'user',
        text,
        kind: 'followup',
        sentAt: new Date().toISOString(),
        ...(payload.operationId ? { operationId: payload.operationId } : {}),
        ...(payload.inputId ? { inputId: payload.inputId } : {}),
        ...(payload.turnId ? { turnId: payload.turnId } : {}),
        runtimeSessionId: target.runtimeSessionId,
        source: 'followup',
      },
    },
    acknowledgementPolicy: 'matching-receipt',
  }
  await outbox.enqueueBeforeExecution(record)
  const awaitReceipt = outbox.awaitInputReceipt
  if (!awaitReceipt) throw new Error('runtime-event queue cannot await input receipt')
  await awaitReceipt.call(outbox, record.id)
}

function recordFollowupActivity(
  outbox: AgentSessionRuntimeEventQueue,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
  turnId: string | undefined,
  activity: 'idle' | 'unknown',
  error?: unknown,
  output?: string | null,
  managerExecution: ManagerExecutionBoundary | null = null,
  failureCategory?: string,
): void {
  if (!operationId) return
  const managerCredentialExpired = managerExecution?.hasExpired() === true
  const terminalActivity = managerCredentialExpired ? 'unknown' : activity
  const completedAt = new Date().toISOString()
  const record: RuntimeEventRecord = {
    id: `followup-activity:${operationId}:${terminalActivity}:${completedAt}`,
    producerFamily: 'session-followup',
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId: target.runtimeSessionId,
    sessionTurnId: turnId,
    work: null,
    event: {
      type: 'session.activity',
      payload: {
        activity: terminalActivity,
        status: managerCredentialExpired ? 'unknown' : terminalActivity === 'idle' ? 'completed' : 'failed',
        ...(managerCredentialExpired
          ? { reason: 'manager-credential-expired', failureCategory: 'unknown' }
          : failureCategory
            ? { failureCategory }
            : {}),
        ...(error
          ? {
              failureReason: managerExecution
                ? managerExecution.mask(error instanceof Error ? error.message : errorMessage(error))
                : error instanceof Error
                  ? error.message
                  : errorMessage(error),
            }
          : {}),
        ...(output
          ? {
              message: managerExecution ? managerExecution.mask(output) : output,
              output: managerExecution ? managerExecution.mask(output) : output,
            }
          : {}),
        source: 'followup',
        operationId,
        ...(turnId ? { turnId } : {}),
        runtimeSessionId: target.runtimeSessionId,
        completedAt,
      },
    },
    acknowledgementPolicy: 'successful-response',
  }
  outbox.enqueueProducedFact(record).catch((outboxError) => {
    log.error('failed to enqueue followup terminal evidence', {
      session: target.runtimeSessionId,
      exception: outboxError,
    })
  })
}

function sessionTargetToRuntimeTarget(target: SessionTarget): RuntimeEventRecord['target'] {
  return { kind: 'session', sessionId: sessionTargetId(target) }
}

function sessionTargetId(target: SessionTarget): string {
  return target.kind === 'workflow' ? (target.agentSessionId ?? '') : target.sessionId
}

function isPromise<T>(value: T | Promise<T>): value is Promise<T> {
  return typeof (value as Promise<T> | null)?.then === 'function'
}

function unavailable(): FollowupDeliveryResult {
  return { accepted: false, error: 'unavailable' }
}
