// The Session Activity probe handler answers a Server-owned observation about a
// logical Session by reading the Runtime Session the Server captured. It is a
// read: the only Runtime call it makes is `resolveSession`, the same read path
// Cancel already uses to reconcile a started stop. No probe starts, creates,
// replaces, replays, or cancels anything, and a probe is never permission to
// rerun effects.
//
// Two failure classes must stay apart, because the Server settles them
// differently:
//   - A Runtime this Runner does not enable, or one whose provider confirms the
//     bound Session is gone, cannot account for the binding. That is
//     `unknown-to-runner` evidence.
//   - An enabled Runtime that was never constructed, is not ready, rejects the
//     read, or answers about a different Session proves nothing. Those probes
//     throw, the transport replies Internal error, and the Server's Activity
//     stays `unknown`.

import type { AgentRuntime } from '../core/types.js'
import { runnerLogger } from '../system/logger.js'
import { resolveCommandRuntime, type CommandRuntimeAccessors, type CommandRuntimeHandle } from './command-runtime.js'

const log = runnerLogger.child('session')

/** Runtimes a probe may target; any other name is an invalid request. */
export type RunnerSessionActivityProbeRuntime = AgentRuntime

/**
 * The three observations the Runner can report. There is deliberately no
 * `unknown`: an unanswered, failed, or malformed probe produces no result and
 * leaves the Server's Activity `unknown`.
 */
export type RunnerSessionActivityProbeObservation = 'executing' | 'idle' | 'unknown-to-runner'

/** Server-owned probe identity: the complete target the Server examined. */
export interface RunnerSessionActivityProbeRequest {
  sessionId: string
  observationId: string
  runnerId: string
  runtime: RunnerSessionActivityProbeRuntime
  runtimeSessionId: string
  workDir: string
  bindingEpoch: number
  contextGeneration: number
}

/**
 * The Runner's answer. `probe` echoes the complete target actually examined, so
 * the Server can reject an answer that does not match what it captured.
 */
export interface RunnerSessionActivityProbeResult {
  probe: RunnerSessionActivityProbeRequest
  observation: RunnerSessionActivityProbeObservation
}

export interface SessionProbeHandlerDeps extends CommandRuntimeAccessors {
  /** This Runner's identity; a probe naming another Runner is never answered. */
  readonly runnerId: string
  /** Runtimes this Runner is configured to run. */
  readonly enabledRuntimes: ReadonlySet<AgentRuntime>
}

export type SessionActivityProbeHandler = (
  request: RunnerSessionActivityProbeRequest | null | undefined,
) => Promise<RunnerSessionActivityProbeResult>

/**
 * The one authoritative validator for a probe. The control dispatcher rejects a
 * malformed probe as Invalid Params before the handler runs, and the handler
 * re-checks so a direct caller can never be answered about a partial target.
 * Every identity member is required: an incomplete binding is a request error,
 * never absence evidence.
 */
export function isRunnerSessionActivityProbeRequest(value: unknown): value is RunnerSessionActivityProbeRequest {
  if (!value || typeof value !== 'object') return false
  const probe = value as Partial<RunnerSessionActivityProbeRequest>
  return (
    nonempty(probe.sessionId) &&
    nonempty(probe.observationId) &&
    nonempty(probe.runnerId) &&
    (probe.runtime === 'pi' || probe.runtime === 'opencode' || probe.runtime === 'codex') &&
    nonempty(probe.runtimeSessionId) &&
    nonempty(probe.workDir) &&
    Number.isSafeInteger(probe.bindingEpoch) &&
    (probe.bindingEpoch as number) >= 0 &&
    Number.isSafeInteger(probe.contextGeneration) &&
    (probe.contextGeneration as number) >= 1
  )
}

export function createSessionProbeHandler(deps: SessionProbeHandlerDeps): SessionActivityProbeHandler {
  return async (request) => {
    if (!isRunnerSessionActivityProbeRequest(request)) {
      throw new Error('session.probe params are not a complete probe target')
    }
    // The echo is rebuilt from the examined members only: no unknown request
    // property reaches the Server's comparison of the captured target.
    const probe = examinedProbe(request)
    if (probe.runnerId !== deps.runnerId) {
      throw new Error('session.probe names a different Runner')
    }
    if (!deps.enabledRuntimes.has(probe.runtime)) return unknownToRunner(probe)
    const handle = resolveCommandRuntime({ runtime: probe.runtime }, deps)
    if (!handle) throw new Error(`the enabled ${probe.runtime} Runtime is not available`)
    if (!handle.runtime.ready()) throw new Error(`the enabled ${probe.runtime} Runtime is not ready`)
    const resolved = await resolveProbedSession(handle, probe)
    if (!resolved.ok) {
      // Only a provider-confirmed absent Session is absence evidence; every
      // other rejection means this probe proved nothing.
      if (resolved.kind === 'missing-session') return unknownToRunner(probe)
      throw new Error(`the bound ${probe.runtime} Session could not be read (${resolved.kind})`)
    }
    if (resolved.runtimeSessionId !== probe.runtimeSessionId || resolved.workDir !== probe.workDir) {
      log.error('session probe examined a different Runtime Session than the binding', {
        observation: probe.observationId,
        session: probe.sessionId,
      })
      throw new Error('the probed Runtime Session does not match the captured binding')
    }
    return { probe, observation: resolved.activeTurn ? 'executing' : 'idle' }
  }
}

function examinedProbe(request: RunnerSessionActivityProbeRequest): RunnerSessionActivityProbeRequest {
  return {
    sessionId: request.sessionId,
    observationId: request.observationId,
    runnerId: request.runnerId,
    runtime: request.runtime,
    runtimeSessionId: request.runtimeSessionId,
    workDir: request.workDir,
    bindingEpoch: request.bindingEpoch,
    contextGeneration: request.contextGeneration,
  }
}

function unknownToRunner(probe: RunnerSessionActivityProbeRequest): RunnerSessionActivityProbeResult {
  return { probe, observation: 'unknown-to-runner' }
}

type ProbedSession =
  | { readonly ok: true; readonly runtimeSessionId: string; readonly workDir: string; readonly activeTurn: boolean }
  | { readonly ok: false; readonly kind: string }

async function resolveProbedSession(
  handle: CommandRuntimeHandle,
  probe: RunnerSessionActivityProbeRequest,
): Promise<ProbedSession> {
  try {
    if (handle.kind === 'opencode') {
      const result = await handle.runtime.resolveSession({
        target: {
          runtime: 'opencode',
          runtimeSessionId: probe.runtimeSessionId,
          workDir: probe.workDir,
        },
      })
      return result.ok ? { ok: true, ...result.value } : { ok: false, kind: result.error.kind }
    }
    if (handle.kind === 'codex') {
      const result = await handle.runtime.resolveSession({
        target: { runtimeSessionId: probe.runtimeSessionId, workDir: probe.workDir },
      })
      return result.ok ? { ok: true, ...result.value } : { ok: false, kind: result.error.kind }
    }
    const result = await handle.runtime.resolveSession({
      target: { runtime: 'pi', runtimeSessionId: probe.runtimeSessionId, workDir: probe.workDir },
    })
    return result.ok ? { ok: true, ...result.value } : { ok: false, kind: result.error.kind }
  } catch (error) {
    log.error('session probe resolve threw', { exception: error, observation: probe.observationId })
    throw new Error(
      `the bound ${probe.runtime} Session could not be read (${error instanceof Error ? error.message : String(error)})`,
    )
  }
}

function nonempty(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}
