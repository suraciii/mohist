// The follow-up handler routes both input and operation-correlated
// terminal outcomes through the host-owned
// `AgentSessionRuntimeEventOutbox`.
//
// Behaviour:
//   - drops silently on null / missing payload, missing/empty input, no
//     resolver, resolver returning null, resolver throwing (logged)
//   - resolves the runtime accessor at invocation time (host-owned
//     late binding), so a runtime built or replaced after
//     control WebSocket client construction is visible to later commands
//   - admits a follow-up command only when (a) the binding resolves and
//     (b) the captured runtime is ready and (c) the outbox is healthy;
//     otherwise returns `{ accepted: false, error: "unavailable" }`
//     without enqueuing input or invoking the runtime
//   - dispatches to the binding's runtime:
//     the wire binding's `runtime` field selects between the OpenCode
//     and Pi backends; an unknown or not-ready runtime reports
//     `unavailable` and the command is not silently dropped
//   - enqueues a `session.input` record through `enqueueBeforeExecution`
//     before invoking `runtime.followup`; a local persistence failure
//     returns `unavailable` without invoking the runtime, so command
//     delivery can be retried
//   - invokes `runtime.followup` exactly once; the resolve/reject
//     handler enqueues the corresponding session.activity record
//   - server upload failure does NOT change the accepted result and
//     does NOT re-invoke the prompt — the durable record is now under
//     the outbox's retry/recovery policy

import { errorMessage } from '../core/errors.js'
import {
  resolveSessionTarget,
  type FollowupTarget,
  type FollowupTargetResolution,
  type FollowupTargetResolver,
  type ReceiveFollowupPayload,
  type SessionTarget,
} from './session-target.js'
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from './runtime-event-outbox.js'
import {
  callFollowup,
  ensureCommandRuntimeReady,
  resolveCommandRuntime,
  type CommandRuntimeAccessors,
} from './command-runtime.js'
import type { PiRuntimeEvent, PiTurnObserver } from '../runtime/pi/index.js'
import type { RuntimeTurnEvent, RuntimeTurnObserver } from '../runtime/opencode/index.js'
import { resolveOrRecoverBinding, type BindingRecoveryCoordinator } from '../runtime/binding-recovery.js'
import type { FollowupOperationJournalStore } from '../runtime/followup-operation-journal.js'
import type { ServerConnection } from './connection.js'
import { SkillResolver } from '../runtime/skill-resolver.js'
import { buildExecutionEnvelope } from '../runtime/execution-envelope.js'
import { inlineSlackCollaborationSkill, readSlackExecutionContext } from '../runtime/slack-execution-context.js'
import { runnerLogger } from '../system/logger.js'
import {
  attachmentManifestEnvelope,
  deliverAcceptedAttachments,
  parseAttachmentDescriptors,
  type DeliveredAttachment,
} from '../runtime/attachment-delivery.js'

const log = runnerLogger.child('session')

export interface FollowupHandlerDeps {
  followupTargetResolver?: FollowupTargetResolver | null
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  openCodeRuntime?: CommandRuntimeAccessors['openCode']
  piRuntime?: CommandRuntimeAccessors['pi']
  connection?: ServerConnection | null
  runnerId?: string | null
  followupOperationJournal?: FollowupOperationJournalStore | null
  randomId?: () => string
  bindingRecoveryCoordinator?: BindingRecoveryCoordinator | null
  skillResolver?: SkillResolver
}

export interface FollowupDeliveryResult {
  accepted: boolean
  error?: 'missing' | 'unavailable'
}

export function createFollowupHandler(
  deps: FollowupHandlerDeps,
): (payload: ReceiveFollowupPayload | null | undefined) => Promise<FollowupDeliveryResult> {
  const inFlight = new Map<string, Promise<FollowupDeliveryResult>>()
  return async (payload: ReceiveFollowupPayload | null | undefined) => {
    const key = followupOperationKey(payload)
    if (!key) return await handleFollowup(payload, deps)

    const existing = inFlight.get(key)
    if (existing) return await existing

    const operation = handleFollowup(payload, deps)
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
  const text = typeof payload.text === 'string' ? payload.text : ''
  const descriptors = parseAttachmentDescriptors(payload.attachments)
  if (text.trim().length === 0 && descriptors.length === 0) return unavailable()
  const resolver = deps.followupTargetResolver ?? null
  const outbox = deps.agentSessionRuntimeEventOutbox ?? null
  if (!resolver || !outbox) return unavailable()
  if (!outbox.ready()) return unavailable()

  const sessionTarget = resolveSessionTarget(payload)
  if (!sessionTarget) return unavailable()
  if (!sessionTargetId(sessionTarget) || !payload.turnId) return unavailable()

  const operationId = payload.operationId
  const operationKey = operationId ? sessionTargetKey(sessionTarget) : null

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

  const handle = resolveCommandRuntime(binding, {
    openCode: deps.openCodeRuntime,
    pi: deps.piRuntime,
  })
  if (!handle) return unavailable()
  if (!(await ensureCommandRuntimeReady(handle))) return unavailable()

  let selectedTarget = target
  const connection = deps.connection ?? null
  const runnerId = deps.runnerId ?? null
  if (connection && runnerId) {
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
                target: { runtime: 'pi', runtimeSessionId: candidate.runtimeSessionId, workDir: candidate.workDir },
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
        }
        if (sessionTarget.kind === 'workflow') {
          await connection.recoverMissingWorkflowAgentSession(
            sessionTarget.projectId,
            sessionTarget.workflowRunId,
            sessionTarget.sessionName,
            body,
            new AbortController().signal,
          )
        } else {
          await connection.recoverMissingAgentSession(
            sessionTarget.projectId,
            sessionTarget.sessionId,
            body,
            new AbortController().signal,
          )
        }
      },
      recoveryKey: expected.runtimeSessionId!,
      coordinator: deps.bindingRecoveryCoordinator ?? undefined,
    })
    if (!recovery.ok) return unavailable()
    selectedTarget = { ...target, runtimeSessionId: recovery.binding.runtimeSessionId! }
  }

  const definition = sessionTarget.kind === 'generic' ? target.definition : undefined
  const slackContext = readSlackExecutionContext(payload)
  if (slackContext.kind === 'invalid') return unavailable()
  const resolvedSkills = await (deps.skillResolver ?? new SkillResolver()).resolve(
    definition?.skills,
    selectedTarget.workDir,
  )
  if (!resolvedSkills.ok) return unavailable()
  const skills =
    slackContext.kind === 'resolved'
      ? [...resolvedSkills.skills, inlineSlackCollaborationSkill(slackContext.value)]
      : resolvedSkills.skills

  if (operationId && operationKey && deps.followupOperationJournal) {
    try {
      const claim = await deps.followupOperationJournal.claim(operationKey, operationId)
      if (claim === 'submitted') return { accepted: true }
      if (claim === 'claimed') return unavailable()
    } catch (error) {
      log.error('followup operation journal claim failed', { exception: error, session: sessionTarget.kind })
      return unavailable()
    }
  }

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
    buildExecutionEnvelope(
      text,
      definition?.instructions,
      skills,
      slackContext.kind === 'resolved' ? slackContext.value : null,
    ),
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
    if (operationId && operationKey && deps.followupOperationJournal) {
      await deps.followupOperationJournal.release(operationKey, operationId).catch(() => undefined)
    }
    log.error('followup durable input enqueue failed', { exception: error, session: sessionTarget.kind })
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
  }
  const observerState = buildFollowupObserver(
    outbox,
    sessionTarget,
    selectedTarget,
    payload.operationId,
    payload.turnId,
  )
  try {
    const completion = callFollowup(handle, followupRequest, observerState.observer).then(
      async (result) => {
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
          )
          if (readErrorKind(result) === 'unavailable-runtime') {
            log.error('followup runtime unavailable', { reason: message, session: selectedTarget.runtimeSessionId })
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
          )
          return
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
        )
      },
      (error) => {
        log.error('followup runtime.followup rejected', { exception: error, session: selectedTarget.runtimeSessionId })
        recordFollowupActivity(
          outbox,
          sessionTarget,
          selectedTarget,
          payload.operationId,
          payload.turnId,
          'unknown',
          error,
        )
      },
    )
    if (operationId && operationKey && deps.followupOperationJournal) {
      try {
        await deps.followupOperationJournal.markSubmitted(operationKey, operationId)
      } catch (error) {
        log.error('followup operation journal submission mark failed', {
          exception: error,
          session: sessionTarget.kind,
        })
        return unavailable()
      }
    }
    void completion
  } catch (error) {
    log.error('followup runtime.followup threw', { exception: error, session: selectedTarget.runtimeSessionId })
    recordFollowupActivity(outbox, sessionTarget, selectedTarget, payload.operationId, payload.turnId, 'unknown', error)
    return unavailable()
  }
  return { accepted: true }
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
  return kind === 'unavailable-runtime' || kind === 'deadline-exceeded'
}

function buildFollowupObserver(
  outbox: AgentSessionRuntimeEventOutbox,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
  turnId: string | undefined,
): {
  observer: PiTurnObserver | RuntimeTurnObserver | null
  flush: () => Promise<unknown>
} {
  const pending: Promise<void>[] = []
  let observerError: unknown = null
  let openCodeEventOrdinal = 0
  if (!operationId) return { observer: null, flush: async () => null }
  const completedAt = new Date().toISOString()
  const observer: PiTurnObserver | RuntimeTurnObserver = {
    onEvent: (event: PiRuntimeEvent | RuntimeTurnEvent) => {
      const ordinal = 'id' in event ? 0 : ++openCodeEventOrdinal
      const record: RuntimeEventRecord = {
        id: `followup-event:${operationId}:${followupEventId(event, ordinal)}`,
        producerFamily: 'session-followup',
        target: sessionTargetToRuntimeTarget(sessionTarget),
        runtimeSessionId: target.runtimeSessionId,
        sessionTurnId: turnId,
        work: null,
        event: {
          type: event.type,
          payload: {
            ...event.payload,
            turnId,
            source: 'followup',
            operationId,
            runtimeSessionId: target.runtimeSessionId,
            completedAt,
          },
        },
        acknowledgementPolicy: 'successful-response',
      }
      const enqueue = outbox.enqueueProducedFact(record)
      pending.push(enqueue)
      enqueue.catch((outboxError) => {
        observerError ??= outboxError
        log.error('failed to persist followup runtime event', {
          session: target.runtimeSessionId,
          exception: outboxError,
        })
      })
    },
  }
  return {
    observer,
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

async function enqueueFollowupInput(
  outbox: AgentSessionRuntimeEventOutbox,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  payload: ReceiveFollowupPayload,
  text: string,
  randomId: () => string,
): Promise<void> {
  // Issue-522 T-001 D1: when the server mints a stable inputId and
  // turnId on the AgentSession grain before dispatching the follow-up,
  // use them as the durable record id and the canonical correlation
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
}

function recordFollowupActivity(
  outbox: AgentSessionRuntimeEventOutbox,
  sessionTarget: SessionTarget,
  target: FollowupTarget,
  operationId: string | undefined,
  turnId: string | undefined,
  activity: 'idle' | 'unknown',
  error?: unknown,
  output?: string | null,
): void {
  if (!operationId) return
  const completedAt = new Date().toISOString()
  const record: RuntimeEventRecord = {
    id: `followup-activity:${operationId}:${activity}:${completedAt}`,
    producerFamily: 'session-followup',
    target: sessionTargetToRuntimeTarget(sessionTarget),
    runtimeSessionId: target.runtimeSessionId,
    sessionTurnId: turnId,
    work: null,
    event: {
      type: 'session.activity',
      payload: {
        activity,
        status: activity === 'idle' ? 'completed' : 'failed',
        ...(error ? { failureReason: error instanceof Error ? error.message : errorMessage(error) } : {}),
        ...(output ? { message: output, output } : {}),
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
    log.error('failed to persist followup terminal', { session: target.runtimeSessionId, exception: outboxError })
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
