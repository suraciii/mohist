import type { ClientSideConnection } from "@agentclientprotocol/sdk"
import type { ActionContext } from "../../core/types.js"
import {
  appendOpencodeDiagnostic,
  findFailFastOpencodeProviderErrorDiagnostic,
  findOpencodeProviderErrorDiagnostic,
  type OpencodeProviderErrorDiagnostic,
} from "../../runtime/opencode-log-diagnostics.js"
import { buildUsageUpdatePayload, emitLivenessStatusEvent, emitSessionEvent, hasUsageUpdateContent } from "./session-events.js"
import type { AcpProcessHandle } from "./process.js"

interface LivenessProbeState {
  probeSentAt?: string
  probeDeadlineAt?: string
  probeVersion?: number
  lastDataAt: number
  lastActivityType?: string
  dataVersion: number
  postProbeActivity?: boolean
}

export interface SessionLivenessState {
  probeSentAt?: string
  probeDeadlineAt?: string
  probeVersion?: number
  lastDataAt: number
  lastActivityType?: string
  dataVersion: number
}

export type LivenessFailureReason = "probe_timeout" | "probe_send_failed" | "protocol_disconnect" | "process_exit" | "prompt_timeout"
export type PromptFailureReason = LivenessFailureReason | "provider_error"

export function createSessionLivenessState(): SessionLivenessState {
  return {
    lastDataAt: Date.now(),
    dataVersion: 0,
  }
}

export function recordSessionLivenessActivity(state: SessionLivenessState, activityType?: string) {
  state.lastDataAt = Date.now()
  state.dataVersion += 1
  if (activityType) state.lastActivityType = activityType
}

function beginLivenessProbe(state: SessionLivenessState, probeTimeoutMs: number) {
  const probeSentAt = new Date()
  const probeDeadlineAt = new Date(probeSentAt.getTime() + probeTimeoutMs)
  state.probeSentAt = probeSentAt.toISOString()
  state.probeDeadlineAt = probeDeadlineAt.toISOString()
  state.probeVersion = state.dataVersion
  return { probeSentAt: state.probeSentAt, probeDeadlineAt: state.probeDeadlineAt, probeVersion: state.probeVersion }
}

function clearLivenessProbe(state: SessionLivenessState) {
  state.probeSentAt = undefined
  state.probeDeadlineAt = undefined
  state.probeVersion = undefined
}

export function hasPostProbeActivity(state: SessionLivenessState) {
  return state.probeVersion !== undefined && state.dataVersion > state.probeVersion
}

function probeWasSatisfied(state: SessionLivenessState) {
  if (state.probeVersion === undefined || !state.probeDeadlineAt) return false
  return hasPostProbeActivity(state) && state.lastDataAt <= Date.parse(state.probeDeadlineAt)
}

const CANCEL_TIMEOUT_MS = 5_000
const PROBE_PROMPT = "If this session is still alive, briefly report the current step and continue from existing context. Do not restart completed work."

const PROVIDER_ERROR_CHECK_INTERVAL_MS = 1_000
let cancelTimeoutMsForTest: number | null = null

export function setAcpCancelTimeoutMsForTest(timeoutMs: number | null) {
  cancelTimeoutMsForTest = timeoutMs
}

export async function monitorPrompt(context: ActionContext, connection: ClientSideConnection, sessionId: string, prompt: string, options: { timeoutMs: number; livenessQuietThresholdMs: number; probeTimeoutMs: number; livenessState: SessionLivenessState; waitForData(version: number): Promise<"data">; exitFailure?: Promise<never>; acpProcess?: AcpProcessHandle; providerErrorCheckIntervalMs?: number }): Promise<"completed" | { error: string; providerError?: OpencodeProviderErrorDiagnostic; failureReason?: PromptFailureReason }> {
  const startedAt = Date.now()
  const promptPromise = connection.prompt({ sessionId, prompt: [{ type: "text", text: prompt }] })
  let promptUsage: unknown
  promptPromise.then(
    (response) => { promptUsage = response.usage },
    () => {},
  )
  const promptOutcome = promptPromise.then(() => "completed" as const, (error: unknown) => toError(error))
  const exitFailure = options.exitFailure ?? new Promise<never>(() => {})
  const exitOutcome = exitFailure.catch((error) => error)
  const abortWatch = watchAbort(context.signal)
  const providerErrorCheckIntervalMs = Math.max(1, options.providerErrorCheckIntervalMs ?? PROVIDER_ERROR_CHECK_INTERVAL_MS)

  const emitPromptUsageIfAppropriate = async () => {
    if (!promptUsage || typeof promptUsage !== "object") return
    const payload = buildUsageUpdatePayload(context, sessionId, "prompt_response", promptUsage)
    if (!hasUsageUpdateContent(payload)) return
    await emitSessionEvent(context, "usage.updated", payload)
  }

  try {
    monitorLoop:
    while (true) {
      const now = Date.now()
      const timeoutRemaining = startedAt + options.timeoutMs - now
      if (timeoutRemaining <= 0) {
        const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
        await emitLivenessStatusEvent(context, options.livenessState, "failed", {
          runtimeSessionId: sessionId,
          failureReason: "prompt_timeout",
          providerError: diagnostic,
          postProbeActivity: hasPostProbeActivity(options.livenessState),
        })
        await cancelAndReturn(options.acpProcess, connection, sessionId, `Timed out after ${options.timeoutMs / 1000}s`)
        return {
          error: appendOpencodeDiagnostic(`Timed out after ${options.timeoutMs / 1000}s`, diagnostic),
          providerError: diagnostic,
          failureReason: "prompt_timeout",
        }
      }
      const quietRemaining = Math.max(0, options.livenessState.lastDataAt + options.livenessQuietThresholdMs - now)
      const waitMs = quietRemaining
      const result = await Promise.race([
        promptOutcome,
        timeout(Math.min(timeoutRemaining, Math.max(waitMs, 1), providerErrorCheckIntervalMs)),
        abortWatch.promise,
        exitOutcome,
      ])
      if (result === "completed") {
        await emitPromptUsageIfAppropriate()
        return "completed"
      }
      if (result === "aborted") return await cancelAndReturn(options.acpProcess, connection, sessionId, "Agent stopped by user")
      if (result instanceof Error) {
        const failureReason: LivenessFailureReason = result.message.includes("[PROCESS_EXIT]") ? "process_exit" : "protocol_disconnect"
        const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
        await emitLivenessStatusEvent(context, options.livenessState, "failed", { runtimeSessionId: sessionId, failureReason, providerError: diagnostic, postProbeActivity: hasPostProbeActivity(options.livenessState) })
        return { error: appendOpencodeDiagnostic(result.message, diagnostic), providerError: diagnostic, failureReason }
      }
      const failFastProviderError = await findFailFastOpencodeProviderErrorDiagnostic(sessionId, startedAt)
      if (failFastProviderError) {
        await emitLivenessStatusEvent(context, options.livenessState, "failed", {
          runtimeSessionId: sessionId,
          failureReason: "provider_error",
          providerError: failFastProviderError,
          postProbeActivity: hasPostProbeActivity(options.livenessState),
        })
        await cancelAndReturn(options.acpProcess, connection, sessionId, failFastProviderError.summary)
        return { error: failFastProviderError.summary, providerError: failFastProviderError, failureReason: "provider_error" }
      }
      if (Date.now() - options.livenessState.lastDataAt < options.livenessQuietThresholdMs) continue

      const activeProbe = beginLivenessProbe(options.livenessState, options.probeTimeoutMs)
      await emitLivenessStatusEvent(context, options.livenessState, "probing", { runtimeSessionId: sessionId, activeProbeVersion: activeProbe.probeVersion })
      try {
        await ensurePromptAcceptedOrPending(connection.prompt({ sessionId, prompt: [{ type: "text", text: PROBE_PROMPT }] }))
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error)
        const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
        await emitLivenessStatusEvent(context, options.livenessState, "failed", { runtimeSessionId: sessionId, failureReason: "probe_send_failed", providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: hasPostProbeActivity(options.livenessState) })
        return { error: appendOpencodeDiagnostic(`Failed to send liveness probe: ${message}`, diagnostic), providerError: diagnostic, failureReason: "probe_send_failed" }
      }
      const probeData = options.waitForData(activeProbe.probeVersion)
      while (true) {
        const probeDeadlineMs = activeProbe.probeDeadlineAt ? Date.parse(activeProbe.probeDeadlineAt) : Date.now()
        const probeRemaining = Math.max(0, probeDeadlineMs - Date.now())
        if (probeRemaining <= 0) break

        const probeResult = await Promise.race([
          promptOutcome,
          probeData,
          timeout(Math.min(probeRemaining, providerErrorCheckIntervalMs)),
          abortWatch.promise,
          exitOutcome,
        ])
        if (probeResult === "completed" && hasPostProbeActivity(options.livenessState)) {
          await emitPromptUsageIfAppropriate()
          return "completed"
        }
        if (probeResult === "completed") {
          const probeState = buildProbeState(options.livenessState)
          const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
          await emitLivenessStatusEvent(context, options.livenessState, "failed", { runtimeSessionId: sessionId, failureReason: "probe_timeout", providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: probeState.postProbeActivity })
          return { error: appendOpencodeDiagnostic(`Session liveness probe timed out ${JSON.stringify(probeState)}`, diagnostic), providerError: diagnostic, failureReason: "probe_timeout" }
        }
        if (probeResult === "data" && probeWasSatisfied(options.livenessState)) {
          await emitLivenessStatusEvent(context, options.livenessState, "running", { runtimeSessionId: sessionId, satisfiedProbeVersion: activeProbe.probeVersion })
          clearLivenessProbe(options.livenessState)
          continue monitorLoop
        }
        if (probeResult === "data") break
        if (probeResult === "aborted") return await cancelAndReturn(options.acpProcess, connection, sessionId, "Agent stopped by user")
        if (probeResult instanceof Error) {
          const failureReason: LivenessFailureReason = probeResult.message.includes("[PROCESS_EXIT]") ? "process_exit" : "protocol_disconnect"
          const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
          await emitLivenessStatusEvent(context, options.livenessState, "failed", { runtimeSessionId: sessionId, failureReason, providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: hasPostProbeActivity(options.livenessState) })
          return { error: appendOpencodeDiagnostic(probeResult.message, diagnostic), providerError: diagnostic, failureReason }
        }
        const failFastProviderError = await findFailFastOpencodeProviderErrorDiagnostic(sessionId, startedAt)
        if (failFastProviderError) {
          await emitLivenessStatusEvent(context, options.livenessState, "failed", {
            runtimeSessionId: sessionId,
            failureReason: "provider_error",
            providerError: failFastProviderError,
            activeProbeVersion: activeProbe.probeVersion,
            postProbeActivity: hasPostProbeActivity(options.livenessState),
          })
          await cancelAndReturn(options.acpProcess, connection, sessionId, failFastProviderError.summary)
          return { error: failFastProviderError.summary, providerError: failFastProviderError, failureReason: "provider_error" }
        }
      }
      const probeState = buildProbeState(options.livenessState)
      const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { runtimeSessionId: sessionId, failureReason: "probe_timeout", providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: probeState.postProbeActivity })
      return { error: appendOpencodeDiagnostic(`Session liveness probe timed out ${JSON.stringify(probeState)}`, diagnostic), providerError: diagnostic, failureReason: "probe_timeout" }
    }
  } finally {
    abortWatch.dispose()
  }
}

function buildProbeState(state: SessionLivenessState): LivenessProbeState {
  return {
    probeSentAt: state.probeSentAt,
    probeDeadlineAt: state.probeDeadlineAt,
    probeVersion: state.probeVersion,
    lastDataAt: state.lastDataAt,
    ...(state.lastActivityType ? { lastActivityType: state.lastActivityType } : {}),
    dataVersion: state.dataVersion,
    postProbeActivity: hasPostProbeActivity(state),
  }
}

function toError(error: unknown) {
  return error instanceof Error ? error : new Error(String(error))
}

async function ensurePromptAcceptedOrPending(promptPromise: Promise<unknown>) {
  let settled = false
  let rejected: unknown
  void promptPromise.then(
    () => { settled = true },
    (error) => {
      settled = true
      rejected = error
    },
  )
  await new Promise<void>((resolve) => queueMicrotask(resolve))
  if (settled && rejected !== undefined) throw rejected
}

export async function cancelAndReturn(acpProcess: AcpProcessHandle | undefined, connection: ClientSideConnection, sessionId: string, error: string) {
  let cancelled = false
  try {
    await Promise.race([
      connection.cancel({ sessionId }).then(() => { cancelled = true }),
      timeout(cancelTimeoutMsForTest ?? CANCEL_TIMEOUT_MS),
    ])
  } catch {}
  if (!cancelled && acpProcess) {
    await acpProcess.cleanup()
  }
  return { error }
}

export function waitForData(waiters: Set<() => void>, done: () => boolean): Promise<"data"> {
  if (done()) return Promise.resolve("data")
  return new Promise((resolve) => waiters.add(() => resolve("data")))
}

export function timeout(ms: number): Promise<"timeout"> {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve("timeout"), ms)
    if (ms > 10_000) timer.unref?.()
  })
}

function watchAbort(signal: AbortSignal): { promise: Promise<"aborted">; dispose(): void } {
  let dispose = () => {}
  const promise = new Promise<"aborted">((resolve) => {
    if (signal.aborted) {
      resolve("aborted")
      return
    }
    const onAbort = () => resolve("aborted")
    signal.addEventListener("abort", onAbort, { once: true })
    dispose = () => signal.removeEventListener("abort", onAbort)
  })
  return { promise, dispose }
}
