// The runner routes Follow-up, Cancel, and
// the `SessionCommand` compact/reset handler by the `runtime` field
// carried on the command's persisted binding. OpenCode and Pi are
// intentionally parallel deep modules:
// their request/result types are not interchangeable, so a generic
// `AgentRuntime` interface is forbidden. The dispatch helper exposes
// the selector + the two parallel call surfaces the handlers use
// without leaking the deep-module boundary types into each other.

import type { ManagerExecutionBoundary } from '../runtime/manager-execution-boundary.js'
import type {
  OpenCodeRuntime,
  RuntimeFilePart,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeResult,
  RuntimeTurnObserver,
  RuntimeTurnEvent,
} from '../runtime/opencode/index.js'
import type {
  CodexRuntime,
  CodexRuntimeTurnEvent,
  CodexCancelResult,
  CodexCompactResult,
  CodexFollowupResult,
  CodexResetResult,
  CodexResult,
  CodexTurnEventObserver,
} from '../runtime/codex/index.js'
import type {
  PiCancelFacts,
  PiCancelRequest,
  PiCancelResult,
  PiCompactRequest,
  PiCompactResult,
  PiErrorKind,
  PiFollowupFacts,
  PiFollowupRequest,
  PiFollowupResult,
  PiResetFacts,
  PiResetRequest,
  PiResetResult,
  PiResult,
  PiRuntime,
  PiRuntimeEvent,
  PiTurnObserver,
} from '../runtime/pi/index.js'
import { parseModelIdentifier } from '../runtime/opencode/index.js'
import type { RuntimeSessionBinding } from './session-target.js'
import type { AgentSessionRuntimeEventQueue, RuntimeEventRecord } from './runtime-event-queue.js'
import type {
  SessionCommand,
  SessionCommandError,
  SessionCommandRequest,
  SessionCommandResult,
} from './session-command-handler.js'

/**
 * Late-binding accessor shape. The host supplies either the runtime
 * directly (when construction is synchronous) or a getter that
 * returns the current handle (when the runtime is rebuilt after a
 * server exit). The `T & object` constraint prevents the
 * `T | (() => T | null)` union from being mistakenly collapsed to
 * `T & Function` (which would lose the call signature) when `T` is
 * itself a class.
 */
export type RuntimeAccessor<T extends object> = T | (() => T | null) | null

export interface CommandRuntimeAccessors {
  openCode?: RuntimeAccessor<OpenCodeRuntime>
  pi?: RuntimeAccessor<PiRuntime>
  codex?: RuntimeAccessor<CodexRuntime>
}

/**
 * Discriminated handle the handlers receive after the binding has been
 * resolved. The handlers branch on `kind` to invoke the matching
 * backend; the alternative (a registry keyed by `runtime` string) was
 * rejected to keep the two deep modules' types from leaking into a
 * shared surface.
 */
export type CommandRuntimeHandle =
  | { readonly kind: 'opencode'; readonly runtime: OpenCodeRuntime }
  | { readonly kind: 'pi'; readonly runtime: PiRuntime }
  | { readonly kind: 'codex'; readonly runtime: CodexRuntime }

export function resolveAccessor<T extends object>(accessor: RuntimeAccessor<T> | undefined): T | null {
  if (accessor === undefined || accessor === null) return null
  return typeof accessor === 'function' ? accessor() : accessor
}

export function resolveCommandRuntime(
  binding: Pick<RuntimeSessionBinding, 'runtime'>,
  accessors: CommandRuntimeAccessors,
): CommandRuntimeHandle | null {
  const name = binding.runtime.toLowerCase()
  if (name === 'opencode') {
    const runtime = resolveAccessor(accessors.openCode)
    return runtime ? { kind: 'opencode', runtime } : null
  }
  if (name === 'pi') {
    const runtime = resolveAccessor(accessors.pi)
    return runtime ? { kind: 'pi', runtime } : null
  }
  if (name === 'codex') {
    const runtime = resolveAccessor(accessors.codex)
    return runtime ? { kind: 'codex', runtime } : null
  }
  return null
}

export async function ensureCommandRuntimeReady(handle: CommandRuntimeHandle): Promise<boolean> {
  if (handle.runtime.ready()) return true
  if (handle.kind !== 'opencode') return false
  if (typeof handle.runtime.start !== 'function') return false
  const started = await handle.runtime.start()
  return started.ok
}

export interface FollowupCallTarget {
  readonly runtime: string
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface FollowupCallRequest {
  readonly target: FollowupCallTarget
  readonly prompt: string
  readonly inputId?: string | null
  readonly managerExecution?: ManagerExecutionBoundary | null
  readonly fileParts?: readonly RuntimeFilePart[] | null
  readonly options?: {
    readonly model?: string | null
    readonly variant?: string | null
    readonly reasoningEffort?: string | null
    readonly skills?: readonly { readonly name: string; readonly instructions: string }[]
  }
}

export interface CancelCallTarget {
  readonly runtime: string
  readonly runtimeSessionId: string
  readonly workDir: string
}

export type FollowupCallResult =
  | RuntimeResult<RuntimeFollowupResult>
  | PiResult<PiFollowupFacts>
  | CodexResult<CodexFollowupResult>

export type CancelCallResult =
  | RuntimeResult<RuntimeCancelResult>
  | PiResult<PiCancelFacts>
  | CodexResult<CodexCancelResult>

export function callFollowup(
  handle: CommandRuntimeHandle,
  request: FollowupCallRequest,
  observer: PiTurnObserver | RuntimeTurnObserver | CodexTurnEventObserver | null,
  signal?: AbortSignal,
): Promise<FollowupCallResult> {
  if (handle.kind === 'opencode') {
    return callOpenCodeFollowup(handle.runtime, request, observer as RuntimeTurnObserver | null, signal)
  }
  if (handle.kind === 'codex') {
    return callCodexFollowup(handle.runtime, request, observer as CodexTurnEventObserver | null, signal)
  }
  return callPiFollowup(handle.runtime, request, observer as PiTurnObserver | null, signal)
}

export function callCancel(handle: CommandRuntimeHandle, target: CancelCallTarget): Promise<CancelCallResult> {
  if (handle.kind === 'opencode') {
    return callOpenCodeCancel(handle.runtime, target)
  }
  if (handle.kind === 'codex') return callCodexCancel(handle.runtime, target)
  return callPiCancel(handle.runtime, target)
}

/**
 * Uniform facts projection across OpenCode and Pi cancel results.
 * OpenCode's `RuntimeCancelResult` wraps `facts: RuntimeCancelFacts`
 * which carries `cancelled` and `stopConfirmed`; Pi's `PiCancelFacts`
 * is the flattened equivalent used for the interrupt-unconfirmed
 * honesty signal.
 */
export interface CancelCallFacts {
  readonly cancelled: boolean
  readonly stopConfirmed?: boolean
}

export function readCancelFacts(result: CancelCallResult): CancelCallFacts | null {
  if (!result.ok) return null
  const value = result.value as {
    readonly cancelled?: boolean
    readonly stopConfirmed?: boolean
    readonly facts?: { readonly cancelled?: boolean; readonly stopConfirmed?: boolean }
  }
  if (typeof value.cancelled === 'boolean') {
    return {
      cancelled: value.cancelled,
      ...(typeof value.stopConfirmed === 'boolean' ? { stopConfirmed: value.stopConfirmed } : {}),
    }
  }
  const facts = value.facts
  if (facts && typeof facts.cancelled === 'boolean') {
    return {
      cancelled: facts.cancelled,
      ...(typeof facts.stopConfirmed === 'boolean' ? { stopConfirmed: facts.stopConfirmed } : {}),
    }
  }
  return null
}

export interface SessionCommandDispatchRequest {
  readonly runtimeSessionId: string
  readonly workDir: string
}

export async function callSessionCommand(
  handle: CommandRuntimeHandle,
  command: SessionCommand,
  request: SessionCommandDispatchRequest,
  observer: PiTurnObserver | CodexTurnEventObserver | null,
): Promise<SessionCommandResult> {
  if (handle.kind === 'opencode') {
    if (command === 'compact') return { ok: false, error: 'unavailable' }
    const result = await handle.runtime.createSession({
      target: { runtime: 'opencode', runtimeSessionId: null, workDir: request.workDir },
    })
    if (result.ok) return { ok: true, runtimeSessionId: result.value.runtimeSessionId }
    return { ok: false, error: mapOpenCodeError(result.error.kind) }
  }
  if (handle.kind === 'codex') {
    if (command === 'compact') return dispatchCodexCompact(handle.runtime, request, observer)
    return dispatchCodexReset(handle.runtime, request)
  }
  if (command === 'compact') {
    return dispatchPiCompact(handle.runtime, request, observer as PiTurnObserver | null)
  }
  return dispatchPiReset(handle.runtime, request)
}

export function createSessionCommandRouter(
  accessors: CommandRuntimeAccessors,
  outbox: AgentSessionRuntimeEventQueue,
): (request: SessionCommandRequest) => Promise<SessionCommandResult> {
  return async (request) => {
    const handle = resolveCommandRuntime({ runtime: request.runtime }, accessors)
    if (!handle) return { ok: false, error: 'runtime-unavailable' }
    if (!(await ensureCommandRuntimeReady(handle))) return { ok: false, error: 'unavailable' }
    if (!request.workDir || (request.command === 'compact' && !request.runtimeSessionId)) {
      return { ok: false, error: 'unavailable' }
    }
    const enqueueEvent = (event: RuntimeTurnEvent | PiRuntimeEvent | CodexRuntimeTurnEvent): void => {
      const eventId = 'id' in event && typeof event.id === 'string' ? event.id : `ordinal-${Date.now()}`
      const record: RuntimeEventRecord = {
        id: `session-command-event:${request.operationId}:${eventId}`,
        producerFamily: 'generic-followup',
        target: { kind: 'generic', projectId: request.projectId!, sessionId: request.sessionId },
        runtimeSessionId: request.runtimeSessionId!,
        work: null,
        event: {
          type: event.type,
          payload: {
            ...(handle.kind === 'codex' ? omitCodexVolatileTurnId(event.payload) : event.payload),
            source: 'session-command',
            command: request.command,
            operationId: request.operationId,
            runtimeSessionId: request.runtimeSessionId,
          },
        },
        acknowledgementPolicy: 'successful-response',
      }
      void outbox.enqueueProducedFact(record)
    }
    const observer: PiTurnObserver | CodexTurnEventObserver | null =
      outbox.ready() && request.projectId && request.runtimeSessionId
        ? handle.kind === 'codex'
          ? { onEvent: (event: CodexRuntimeTurnEvent) => enqueueEvent(event) }
          : { onEvent: (event: PiRuntimeEvent) => enqueueEvent(event) }
        : null
    if ((handle.kind === 'pi' || handle.kind === 'codex') && request.command === 'compact' && !observer) {
      return { ok: false, error: 'unavailable' }
    }
    return await callSessionCommand(
      handle,
      request.command,
      { runtimeSessionId: request.runtimeSessionId ?? '', workDir: request.workDir },
      observer,
    )
  }
}

async function callOpenCodeFollowup(
  runtime: OpenCodeRuntime,
  request: FollowupCallRequest,
  observer: RuntimeTurnObserver | null,
  signal?: AbortSignal,
): Promise<RuntimeResult<RuntimeFollowupResult>> {
  const opencodeRequest: RuntimeFollowupRequest = {
    target: { runtime: 'opencode', runtimeSessionId: request.target.runtimeSessionId, workDir: request.target.workDir },
    prompt: request.prompt,
    ...(request.fileParts && request.fileParts.length > 0 ? { fileParts: request.fileParts } : {}),
    ...(request.options
      ? {
          options: {
            model: parseFollowupModel(request.options.model),
            variant: request.options.variant ?? null,
            reasoningEffort: request.options.reasoningEffort ?? null,
            ...(request.options.skills ? { skills: request.options.skills } : {}),
          },
        }
      : {}),
  }
  return signal === undefined
    ? await runtime.followup(opencodeRequest, observer ?? undefined)
    : await runtime.followup(opencodeRequest, observer ?? undefined, signal)
}

async function callCodexFollowup(
  runtime: CodexRuntime,
  request: FollowupCallRequest,
  observer: PiTurnObserver | RuntimeTurnObserver | CodexTurnEventObserver | null,
  signal?: AbortSignal,
): Promise<CodexResult<CodexFollowupResult>> {
  return await runtime.followup(
    {
      target: { runtime: 'codex', runtimeSessionId: request.target.runtimeSessionId, workDir: request.target.workDir },
      prompt: request.prompt,
      ...(request.inputId ? { clientUserMessageId: request.inputId } : {}),
      ...(request.fileParts && request.fileParts.length > 0 ? { fileParts: request.fileParts } : {}),
      ...(request.options
        ? {
            options: {
              model: request.options.model ?? null,
              variant: request.options.variant ?? null,
              reasoningEffort: request.options.reasoningEffort as never,
            },
          }
        : {}),
    },
    (observer as CodexTurnEventObserver | null) ?? undefined,
    signal,
  )
}

async function callPiFollowup(
  runtime: PiRuntime,
  request: FollowupCallRequest,
  observer: PiTurnObserver | null,
  signal?: AbortSignal,
): Promise<PiFollowupResult> {
  const piRequest: PiFollowupRequest = {
    target: { runtime: 'pi', runtimeSessionId: request.target.runtimeSessionId, workDir: request.target.workDir },
    prompt: request.prompt,
    ...(request.options
      ? {
          options: {
            model: request.options.model ?? null,
            variant: request.options.variant ?? null,
            reasoningEffort: request.options.reasoningEffort ?? null,
            ...(request.options.skills ? { skills: request.options.skills } : {}),
          },
        }
      : {}),
    managerExecution: request.managerExecution ?? null,
  }
  return signal === undefined
    ? await runtime.followup(piRequest, observer ?? undefined)
    : await runtime.followup(piRequest, observer ?? undefined, signal)
}

function omitCodexVolatileTurnId(payload: Record<string, unknown>): Record<string, unknown> {
  const { turnId: _volatileTurnId, ...durablePayload } = payload
  return durablePayload
}

function parseFollowupModel(value: string | null | undefined): { providerID: string; modelID: string } | null {
  if (!value) return null
  const parsed = parseModelIdentifier(value)
  return parsed.kind === 'ok' ? parsed.value : null
}

async function callOpenCodeCancel(
  runtime: OpenCodeRuntime,
  target: CancelCallTarget,
): Promise<RuntimeResult<RuntimeCancelResult>> {
  const opencodeRequest: RuntimeCancelRequest = {
    target: { runtime: 'opencode', runtimeSessionId: target.runtimeSessionId, workDir: target.workDir },
  }
  return await runtime.cancel(opencodeRequest)
}

async function callCodexCancel(
  runtime: CodexRuntime,
  target: CancelCallTarget,
): Promise<CodexResult<CodexCancelResult>> {
  return await runtime.cancel({
    target: { runtime: 'codex', runtimeSessionId: target.runtimeSessionId, workDir: target.workDir },
  })
}

async function callPiCancel(runtime: PiRuntime, target: CancelCallTarget): Promise<PiCancelResult> {
  const piRequest: PiCancelRequest = {
    target: { runtime: 'pi', runtimeSessionId: target.runtimeSessionId, workDir: target.workDir },
  }
  return await runtime.cancel(piRequest)
}

async function dispatchCodexCompact(
  runtime: CodexRuntime,
  request: SessionCommandDispatchRequest,
  observer: PiTurnObserver | CodexTurnEventObserver | null,
): Promise<SessionCommandResult> {
  const result: CodexResult<CodexCompactResult> = await runtime.compact(
    { target: { runtime: 'codex', runtimeSessionId: request.runtimeSessionId, workDir: request.workDir } },
    (observer as CodexTurnEventObserver | null) ?? undefined,
  )
  if (result.ok) return { ok: true }
  return { ok: false, error: mapCodexError(result.error.kind) }
}

async function dispatchCodexReset(
  runtime: CodexRuntime,
  request: SessionCommandDispatchRequest,
): Promise<SessionCommandResult> {
  const result: CodexResult<CodexResetResult> = await runtime.reset({
    target: { runtime: 'codex', runtimeSessionId: request.runtimeSessionId, workDir: request.workDir },
  })
  if (result.ok) return { ok: true, runtimeSessionId: result.value.facts.runtimeSessionId }
  return { ok: false, error: mapCodexError(result.error.kind) }
}

async function dispatchPiCompact(
  runtime: PiRuntime,
  request: SessionCommandDispatchRequest,
  observer: PiTurnObserver | null,
): Promise<SessionCommandResult> {
  const piRequest: PiCompactRequest = {
    target: { runtime: 'pi', runtimeSessionId: request.runtimeSessionId, workDir: request.workDir },
  }
  const result: PiCompactResult = await runtime.compact(piRequest, observer ?? undefined)
  if (result.ok) return { ok: true }
  return { ok: false, error: mapPiError(result.error.kind) }
}

async function dispatchPiReset(
  runtime: PiRuntime,
  request: SessionCommandDispatchRequest,
): Promise<SessionCommandResult> {
  const piRequest: PiResetRequest = {
    target: { runtime: 'pi', runtimeSessionId: request.runtimeSessionId, workDir: request.workDir },
  }
  const result: PiResetResult = await runtime.reset(piRequest)
  if (result.ok) return { ok: true, runtimeSessionId: result.value.runtimeSessionId }
  return { ok: false, error: mapPiError(result.error.kind) }
}

function mapOpenCodeError(kind: string): SessionCommandError {
  if (kind === 'missing-session') return 'missing'
  return 'unavailable'
}

function mapCodexError(kind: string): SessionCommandError {
  if (kind === 'missing-session') return 'missing'
  return 'unavailable'
}

function mapPiError(kind: PiErrorKind): SessionCommandError {
  switch (kind) {
    case 'missing-session':
      return 'missing'
    case 'conflict':
      return 'conflict'
    case 'unavailable-runtime':
    case 'turn-failed':
    case 'invalid-input':
    case 'incompatible-runtime':
    case 'deadline-exceeded':
    case 'interrupted':
      return 'unavailable'
  }
}
