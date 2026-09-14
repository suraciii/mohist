/**
 * `CodexRuntime` — Runner-side deep module for Codex execution.
 *
 * The runtime drives one long-lived `codex app-server --stdio` child
 * per Runner. The implementation lands across T-003 (process +
 * initialization + readiness), T-004 (model catalog + canonical effort
 * mapping), T-005 (Thread + Turn lifecycle, binding-before-effect,
 * no-replay), and T-006 (two-phase closeout + approval rejection).
 *
 * This file declares the public boundary surface: the constructor
 * shape, the entry points callers depend on, the readiness check,
 * and the initialization handshake. The internal state machine, the
 * JSON-RPC consumer integration, and the per-method server-call
 * details land in their respective files.
 *
 * Callers depend only on Mohist-owned request/result types from
 * `./types.js`. The app-server protocol is an implementation detail
 * contained inside this module.
 */

import { createHash } from 'node:crypto'
import { boundedTimeoutMs, boundedWait } from '../bounded-wait.js'
import { CODEX_DEFAULT_TIMEOUTS } from './types.js'
import type {
  CodexCatalog,
  CodexClock,
  CodexDiagnostic,
  CodexFollowupRequest,
  CodexFollowupResult,
  CodexCancelRequest,
  CodexCancelResult,
  CodexCompactRequest,
  CodexCompactResult,
  CodexModelDescriptor,
  CodexReadyState,
  CodexResetRequest,
  CodexResetResult,
  CodexResult,
  CodexTurnRequest,
  CodexTurnResult,
} from './types.js'
import { defaultCodexClock } from './runtime-clock.js'
import type { CodexServerFactory, CodexServerHandle } from './server-process.js'
import { normalizeUnavailableRuntimeCodex } from './errors.js'
import {
  type CodexAuthenticationProbe,
  type CodexCatalogLoader,
  type CodexCliProbe,
  type CodexReadinessProbe,
  evaluateCodexReadiness,
} from './readiness.js'
import { codexInitializationTransportFromHandle, performCodexInitialization } from './initialization.js'
import { isCodexModelListResult } from './protocol-types.js'

export interface CodexRuntimeDeps {
  readonly codexHome: string
  readonly cwd: string
  readonly serverFactory?: CodexServerFactory
  readonly startupTimeoutMs?: number
  readonly runtimeShutdownTimeoutMs?: number
  readonly clock?: CodexClock
  /**
   * Probe used by the readiness gate. The runtime never invokes a
   * CLI on its own; the probe wires the actual filesystem / process
   * access the gate needs. Tests inject a deterministic probe;
   * production wires the real one through the resource context.
   */
  readonly readinessProbe?: CodexReadinessProbe
}

/**
 * Lifecycle of the CodexRuntime.
 *
 * Public entry points declared now are the seam callers use to drive
 * Codex execution and Session commands:
 *
 *   - `start()`: idempotent readiness probe.
 *   - `runTurn()`: a Codex Turn for an AgentJob.
 *   - `followup()`: a Follow-up steered into the active Turn or queued
 *     as a new Turn on the same Thread.
 *   - `cancel()`: interrupt the exact active Turn with bounded
 *     confirmation.
 *   - `compact()`: idle-only context compaction via
 *     `thread/compact/start`.
 *   - `reset()`: empty Thread + atomic binding replacement.
 *   - `catalog()`: the published catalog snapshot.
 *   - `shutdown()`: bounded shutdown of the app-server child.
 *
 * The full implementations of each entry point land in T-004 to
 * T-008; this file wires the boundary types and the readiness seam
 * implemented in T-003.
 */
export class CodexRuntime {
  private readonly deps: Required<Pick<CodexRuntimeDeps, 'codexHome' | 'cwd'>> & CodexRuntimeDeps
  private readonly clock: CodexClock
  private readonly startupTimeoutMs: number
  private readonly runtimeShutdownTimeoutMs: number
  private readonly state: {
    ready: boolean
    diagnostic: CodexDiagnostic | null
    catalog: CodexCatalog | null
    generation: number | null
    startInFlight: Promise<CodexResult<CodexReadyState>> | null
  }
  private readonly server: {
    factory: CodexServerFactory | null
    handle: CodexServerHandle | null
    closed: boolean
  }
  private readonly readinessProbe: CodexReadinessProbe | null

  constructor(deps: CodexRuntimeDeps) {
    this.deps = deps
    this.clock = deps.clock ?? defaultCodexClock
    this.startupTimeoutMs = boundedTimeoutMs(deps.startupTimeoutMs, CODEX_DEFAULT_TIMEOUTS.startupMs)
    this.runtimeShutdownTimeoutMs = boundedTimeoutMs(deps.runtimeShutdownTimeoutMs, CODEX_DEFAULT_TIMEOUTS.shutdownMs)
    this.state = {
      ready: false,
      diagnostic: null,
      catalog: null,
      generation: null,
      startInFlight: null,
    }
    this.server = { factory: deps.serverFactory ?? null, handle: null, closed: false }
    this.readinessProbe = deps.readinessProbe ?? null
  }

  async start(): Promise<CodexResult<CodexReadyState>> {
    if (this.state.ready) return { ok: true, value: this.readyState(), diagnostics: [] }
    if (this.state.startInFlight) return this.state.startInFlight
    const attempt = this.attemptStart()
    this.state.startInFlight = attempt
    try {
      return await attempt
    } finally {
      this.state.startInFlight = null
    }
  }

  ready(): boolean {
    return this.state.ready
  }

  diagnostic(): CodexDiagnostic | null {
    return this.state.diagnostic
  }

  catalog(): CodexCatalog | null {
    return this.state.catalog
  }

  /**
   * Run a Codex Turn to terminal completion. The full Turn lifecycle,
   * event routing, and completion authority land in T-005. This seam
   * declares the boundary shape; the implementation defers until the
   * consumer integration is in place.
   */
  async runTurn(
    _request: CodexTurnRequest,
    _signal: AbortSignal = new AbortController().signal,
  ): Promise<CodexResult<CodexTurnResult>> {
    if (!this.state.ready) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return {
      ok: false,
      error: {
        kind: 'unavailable-runtime',
        message: 'Codex turn execution is not yet wired (T-005 pending)',
        diagnostics: [
          {
            severity: 'error',
            code: 'not-implemented',
            message: 'Codex Turn execution lands in T-005',
          },
        ],
      },
      diagnostics: [],
    }
  }

  /**
   * Follow-up on an existing Codex Thread. Full implementation lands
   * in T-005 / T-008.
   */
  async followup(_request: CodexFollowupRequest): Promise<CodexResult<CodexFollowupResult>> {
    if (!this.state.ready) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return {
      ok: false,
      error: {
        kind: 'unavailable-runtime',
        message: 'Codex follow-up is not yet wired (T-005 / T-008 pending)',
        diagnostics: [
          {
            severity: 'error',
            code: 'not-implemented',
            message: 'Codex follow-up lands in T-005 / T-008',
          },
        ],
      },
      diagnostics: [],
    }
  }

  /**
   * Cancel an active Codex Turn. Full implementation lands in T-005 /
   * T-008.
   */
  async cancel(_request: CodexCancelRequest): Promise<CodexResult<CodexCancelResult>> {
    if (!this.state.ready) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return {
      ok: false,
      error: {
        kind: 'unavailable-runtime',
        message: 'Codex cancel is not yet wired (T-005 / T-008 pending)',
        diagnostics: [
          {
            severity: 'error',
            code: 'not-implemented',
            message: 'Codex cancel lands in T-005 / T-008',
          },
        ],
      },
      diagnostics: [],
    }
  }

  /**
   * Idle-only context compaction via `thread/compact/start`. Full
   * implementation lands in T-008.
   */
  async compact(_request: CodexCompactRequest): Promise<CodexResult<CodexCompactResult>> {
    if (!this.state.ready) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return {
      ok: false,
      error: {
        kind: 'unavailable-runtime',
        message: 'Codex compact is not yet wired (T-008 pending)',
        diagnostics: [
          {
            severity: 'error',
            code: 'not-implemented',
            message: 'Codex compact lands in T-008',
          },
        ],
      },
      diagnostics: [],
    }
  }

  /**
   * Reset creates an empty Thread + atomic binding CAS. Full
   * implementation lands in T-008.
   */
  async reset(_request: CodexResetRequest): Promise<CodexResult<CodexResetResult>> {
    if (!this.state.ready) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return {
      ok: false,
      error: {
        kind: 'unavailable-runtime',
        message: 'Codex reset is not yet wired (T-008 pending)',
        diagnostics: [
          {
            severity: 'error',
            code: 'not-implemented',
            message: 'Codex reset lands in T-008',
          },
        ],
      },
      diagnostics: [],
    }
  }

  /**
   * Shut the CodexRuntime down. Closes the shared app-server child
   * within the configured deadline. The readiness diagnostic is
   * preserved so callers can still inspect the last-known failure
   * reason after shutdown unless `clearDiagnostic` is set.
   */
  async shutdown(options: { clearDiagnostic?: boolean } = {}): Promise<void> {
    this.state.ready = false
    this.state.generation = null
    if (options.clearDiagnostic) this.state.diagnostic = null
    const handle = this.server.handle
    if (handle && !this.server.closed) {
      this.server.closed = true
      await boundedWait(() => handle.close(), this.runtimeShutdownTimeoutMs)
    }
    this.server.handle = null
  }

  private readyState(): CodexReadyState {
    return {
      ready: this.state.ready,
      diagnostic: this.state.diagnostic,
      catalog: this.state.catalog,
      generation: this.state.generation,
    }
  }

  private async attemptStart(): Promise<CodexResult<CodexReadyState>> {
    const factory = this.server.factory ?? this.deps.serverFactory
    if (!factory) {
      const diagnostic: CodexDiagnostic = {
        severity: 'error',
        code: 'server-spawn-failed',
        message: 'Codex server factory was not provided',
      }
      return this.recordFailure(diagnostic)
    }
    let handle: CodexServerHandle
    try {
      handle = await factory({
        codexHome: this.deps.codexHome,
        cwd: this.deps.cwd,
        startupTimeoutMs: this.startupTimeoutMs,
        shutdownTimeoutMs: this.runtimeShutdownTimeoutMs,
        clock: this.clock,
      })
    } catch (cause) {
      const diagnostic: CodexDiagnostic = {
        severity: 'error',
        code: 'server-spawn-failed',
        message: cause instanceof Error ? cause.message : 'Codex app-server spawn failed',
      }
      return this.recordFailure(diagnostic)
    }
    this.server.handle = handle

    // Run the initialize → initialized handshake. The handshake
    // rejects non-managed codexHome, malformed initialize responses,
    // and any child exit before the response arrives.
    const transport = codexInitializationTransportFromHandle(handle)
    const initResult = await performCodexInitialization(transport, {
      managedCodexHome: this.deps.codexHome,
      startupTimeoutMs: this.startupTimeoutMs,
      clock: this.clock,
    })
    if (!initResult.ok) {
      await this.tearDownHandle(handle)
      this.state.diagnostic = initResult.error.diagnostics[0] ?? {
        severity: 'error',
        code: 'initialize-failed',
        message: 'Codex initialize handshake failed',
      }
      return { ok: false, error: initResult.error, diagnostics: initResult.diagnostics }
    }

    // Evaluate the readiness gate. The probe needs the spawned handle
    // so the catalog loader can drive model/list on the just-initialized
    // child.
    if (!this.readinessProbe) {
      await this.tearDownHandle(handle)
      const diagnostic: CodexDiagnostic = {
        severity: 'error',
        code: 'readiness-probe-missing',
        message: 'Codex readiness probe was not provided',
      }
      return this.recordFailure(diagnostic)
    }
    const catalogLoader = codexCatalogLoaderFromHandle(handle)
    const readinessProbe: CodexReadinessProbe = {
      cli: this.readinessProbe.cli,
      authentication: this.readinessProbe.authentication,
      catalog: catalogLoader,
    }
    const readinessResult = await evaluateCodexReadiness({
      managedCodexHome: this.deps.codexHome,
      startupTimeoutMs: this.startupTimeoutMs,
      probe: readinessProbe,
      clock: this.clock,
    })
    if (!readinessResult.ok) {
      await this.tearDownHandle(handle)
      this.state.diagnostic = readinessResult.error.diagnostics[0] ?? {
        severity: 'error',
        code: 'readiness-failed',
        message: 'Codex readiness probe failed',
      }
      return { ok: false, error: readinessResult.error, diagnostics: readinessResult.diagnostics }
    }
    this.state.ready = true
    this.state.diagnostic = null
    this.state.catalog = readinessResult.value.catalog
    this.state.generation = 1
    return { ok: true, value: this.readyState(), diagnostics: [] }
  }

  private recordFailure(diagnostic: CodexDiagnostic): CodexResult<CodexReadyState> {
    this.state.diagnostic = diagnostic
    const error = normalizeUnavailableRuntimeCodex([diagnostic])
    return { ok: false, error, diagnostics: error.diagnostics }
  }

  private async tearDownHandle(handle: CodexServerHandle): Promise<void> {
    try {
      await boundedWait(() => handle.close(), this.runtimeShutdownTimeoutMs)
    } catch {
      /* best effort */
    }
    this.server.closed = true
    this.server.handle = null
  }
}

/**
 * Build a catalog loader that drives `model/list` on the supplied
 * spawned handle. T-003 only requires that the catalog is non-empty;
 * the canonical reasoning-effort mapping, the page-through, and the
 * change-detection heartbeats land in T-004.
 */
function codexCatalogLoaderFromHandle(handle: CodexServerHandle): CodexCatalogLoader {
  return {
    async loadCatalog() {
      let envelope: unknown
      try {
        envelope = await handle.send<{ readonly cursor?: string | null; readonly pageSize?: number }, unknown>({
          id: 2,
          method: 'model/list',
          params: { pageSize: 256 },
        })
      } catch {
        return null
      }
      if (!isCodexModelListResult(envelope)) return null
      const native = envelope.result
      if (!Array.isArray(native.models) || native.models.length === 0) return null
      const models: CodexModelDescriptor[] = []
      for (const candidate of native.models) {
        if (!candidate || typeof candidate !== 'object') continue
        const entry = candidate as { id?: unknown; displayName?: unknown }
        if (typeof entry.id !== 'string') continue
        models.push({
          id: entry.id,
          displayName: typeof entry.displayName === 'string' ? entry.displayName : null,
          reasoningEfforts: [],
          defaultReasoningEffort: null,
          supportsReasoningEffort: true,
        })
      }
      if (models.length === 0) return null
      return {
        models,
        complete: native.complete === true,
        capabilityRevision: capabilityRevisionForCodexCatalog(models),
      }
    },
  }
}

/**
 * Hash the catalog content (model ids + display names) so the Runner
 * registration witness can detect changes between reloads. The
 * canonical reasoning-effort mapping lands in T-004; this minimal
 * revision is sufficient for T-003.
 */
function capabilityRevisionForCodexCatalog(models: readonly CodexModelDescriptor[]): string {
  const canonical = JSON.stringify(
    models.map((model) => ({
      id: model.id,
      displayName: model.displayName,
    })),
  )
  return createHash('sha256').update(canonical).digest('hex')
}
