// Issue-451 T-004 / design D2: the runner routes Follow-up, Cancel, and
// the `SessionCommand` compact/reset handler by the `runtime` field
// carried on the command's persisted binding. OpenCode and Pi are
// intentionally parallel deep modules (per `design/runtimes/pi.md`):
// their request/result types are not interchangeable, so a generic
// `AgentRuntime` interface is forbidden. The dispatch helper exposes
// the selector + the two parallel call surfaces the handlers use
// without leaking the deep-module boundary types into each other.

import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeResult,
} from "../runtime/opencode/index.js"
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
  PiTurnObserver,
} from "../runtime/pi/index.js"
import type { RuntimeSessionBinding } from "./session-target.js"
import type { SessionCommand, SessionCommandError, SessionCommandResult } from "./session-command-handler.js"

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
}

/**
 * Discriminated handle the handlers receive after the binding has been
 * resolved. The handlers branch on `kind` to invoke the matching
 * backend; the alternative (a registry keyed by `runtime` string) was
 * rejected in design D2.
 */
export type CommandRuntimeHandle =
  | { readonly kind: "opencode"; readonly runtime: OpenCodeRuntime }
  | { readonly kind: "pi"; readonly runtime: PiRuntime }

export function resolveAccessor<T extends object>(accessor: RuntimeAccessor<T> | undefined): T | null {
  if (accessor === undefined || accessor === null) return null
  return typeof accessor === "function" ? accessor() : accessor
}

export function resolveCommandRuntime(
  binding: Pick<RuntimeSessionBinding, "runtime">,
  accessors: CommandRuntimeAccessors,
): CommandRuntimeHandle | null {
  const name = binding.runtime.toLowerCase()
  if (name === "opencode") {
    const runtime = resolveAccessor(accessors.openCode)
    return runtime ? { kind: "opencode", runtime } : null
  }
  if (name === "pi") {
    const runtime = resolveAccessor(accessors.pi)
    return runtime ? { kind: "pi", runtime } : null
  }
  return null
}

export interface FollowupCallTarget {
  readonly runtime: string
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface FollowupCallRequest {
  readonly target: FollowupCallTarget
  readonly prompt: string
}

export interface CancelCallTarget {
  readonly runtime: string
  readonly runtimeSessionId: string
  readonly workDir: string
}

export type FollowupCallResult = RuntimeResult<RuntimeFollowupResult> | PiResult<PiFollowupFacts>

export type CancelCallResult = RuntimeResult<RuntimeCancelResult> | PiResult<PiCancelFacts>

export function callFollowup(
  handle: CommandRuntimeHandle,
  request: FollowupCallRequest,
  observer: PiTurnObserver | null,
): Promise<FollowupCallResult> {
  if (handle.kind === "opencode") {
    return callOpenCodeFollowup(handle.runtime, request)
  }
  return callPiFollowup(handle.runtime, request, observer)
}

export function callCancel(
  handle: CommandRuntimeHandle,
  target: CancelCallTarget,
): Promise<CancelCallResult> {
  if (handle.kind === "opencode") {
    return callOpenCodeCancel(handle.runtime, target)
  }
  return callPiCancel(handle.runtime, target)
}

/**
 * Uniform facts projection across OpenCode and Pi cancel results.
 * OpenCode's `RuntimeCancelResult` wraps `facts: RuntimeCancelFacts`
 * which carries `cancelled: true`; Pi's `PiCancelFacts` is the
 * flattened result and adds `stopConfirmed: boolean` for the
 * interrupt-unconfirmed honesty signal (design D6).
 */
export interface CancelCallFacts {
  readonly cancelled: boolean
  readonly stopConfirmed: boolean
}

export function readCancelFacts(result: CancelCallResult): CancelCallFacts | null {
  if (!result.ok) return null
  const value = result.value as { readonly cancelled?: boolean; readonly stopConfirmed?: boolean; readonly facts?: { readonly cancelled?: boolean; readonly stopConfirmed?: boolean } }
  if (typeof value.cancelled === "boolean") {
    return {
      cancelled: value.cancelled,
      stopConfirmed: typeof value.stopConfirmed === "boolean" ? value.stopConfirmed : true,
    }
  }
  const facts = value.facts
  if (facts && typeof facts.cancelled === "boolean") {
    return {
      cancelled: facts.cancelled,
      stopConfirmed: typeof facts.stopConfirmed === "boolean" ? facts.stopConfirmed : true,
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
  observer: PiTurnObserver | null,
): Promise<SessionCommandResult> {
  if (handle.kind === "opencode") {
    if (command === "compact") return { ok: false, error: "unavailable" }
    const result = await handle.runtime.createSession({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: request.workDir },
    })
    if (result.ok) return { ok: true, runtimeSessionId: result.value.runtimeSessionId }
    return { ok: false, error: mapOpenCodeError(result.error.kind) }
  }
  if (command === "compact") {
    return dispatchPiCompact(handle.runtime, request, observer)
  }
  return dispatchPiReset(handle.runtime, request)
}

async function callOpenCodeFollowup(
  runtime: OpenCodeRuntime,
  request: FollowupCallRequest,
): Promise<RuntimeResult<RuntimeFollowupResult>> {
  const opencodeRequest: RuntimeFollowupRequest = {
    target: { runtime: "opencode", runtimeSessionId: request.target.runtimeSessionId, workDir: request.target.workDir },
    prompt: request.prompt,
  }
  return await runtime.followup(opencodeRequest)
}

async function callPiFollowup(
  runtime: PiRuntime,
  request: FollowupCallRequest,
  observer: PiTurnObserver | null,
): Promise<PiFollowupResult> {
  const piRequest: PiFollowupRequest = {
    target: { runtime: "pi", runtimeSessionId: request.target.runtimeSessionId, workDir: request.target.workDir },
    prompt: request.prompt,
  }
  return await runtime.followup(piRequest, observer ?? undefined)
}

async function callOpenCodeCancel(
  runtime: OpenCodeRuntime,
  target: CancelCallTarget,
): Promise<RuntimeResult<RuntimeCancelResult>> {
  const opencodeRequest: RuntimeCancelRequest = {
    target: { runtime: "opencode", runtimeSessionId: target.runtimeSessionId, workDir: target.workDir },
  }
  return await runtime.cancel(opencodeRequest)
}

async function callPiCancel(
  runtime: PiRuntime,
  target: CancelCallTarget,
): Promise<PiCancelResult> {
  const piRequest: PiCancelRequest = {
    target: { runtime: "pi", runtimeSessionId: target.runtimeSessionId, workDir: target.workDir },
  }
  return await runtime.cancel(piRequest)
}

async function dispatchPiCompact(
  runtime: PiRuntime,
  request: SessionCommandDispatchRequest,
  observer: PiTurnObserver | null,
): Promise<SessionCommandResult> {
  const piRequest: PiCompactRequest = {
    target: { runtime: "pi", runtimeSessionId: request.runtimeSessionId, workDir: request.workDir },
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
    target: { runtime: "pi", runtimeSessionId: request.runtimeSessionId, workDir: request.workDir },
  }
  const result: PiResetResult = await runtime.reset(piRequest)
  if (result.ok) return { ok: true, runtimeSessionId: result.value.runtimeSessionId }
  return { ok: false, error: mapPiError(result.error.kind) }
}

function mapOpenCodeError(kind: string): SessionCommandError {
  if (kind === "missing-session") return "missing"
  return "unavailable"
}

function mapPiError(kind: PiErrorKind): SessionCommandError {
  switch (kind) {
    case "missing-session":
      return "missing"
    case "conflict":
      return "conflict"
    case "unavailable-runtime":
    case "turn-failed":
    case "invalid-input":
    case "incompatible-runtime":
    case "deadline-exceeded":
    case "interrupted":
      return "unavailable"
  }
}
