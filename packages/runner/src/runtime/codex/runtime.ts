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
import { redactCodexCredentialString } from './credential.js'
import {
  createCodexModelCatalogLoader,
  type CodexCatalogManager,
  validateCodexTurnConfiguration,
} from './model-catalog.js'
import { type CodexReadinessProbe, evaluateCodexReadiness } from './readiness.js'
import { codexInitializationTransportFromHandle, performCodexInitialization } from './initialization.js'

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
 * The full implementations of the turn and Session command entry points
 * land in later runtime tasks; this file wires the boundary types, readiness,
 * and catalog seam implemented here.
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
    unsubscribe: (() => void) | null
    closed: boolean
  }
  private generationCounter = 0
  private readonly readinessProbe: CodexReadinessProbe | null
  private catalogManager: CodexCatalogManager | null = null

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
    this.server = { factory: deps.serverFactory ?? null, handle: null, unsubscribe: null, closed: false }
    this.readinessProbe = deps.readinessProbe ?? null
  }

  async start(): Promise<CodexResult<CodexReadyState>> {
    if (this.state.ready) {
      const diagnostics = this.state.diagnostic ? [this.state.diagnostic] : []
      return { ok: true, value: this.readyState(), diagnostics }
    }
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

  generation(): number | null {
    return this.state.generation
  }

  diagnostic(): CodexDiagnostic | null {
    return this.state.diagnostic
  }

  catalog(): CodexCatalog | null {
    return this.state.catalog
  }

  /**
   * Refresh the catalog on the host's bounded maintenance cadence. A failed
   * refresh keeps the last complete snapshot, gates readiness, and exposes
   * the current diagnostic. A successful refresh clears the failure and only
   * changes registration when the snapshot content changed.
   */
  async refreshCatalog(): Promise<{ readonly changed: boolean; readonly catalog: CodexCatalog | null }> {
    if (!this.state.generation || !this.catalogManager) {
      const diagnostic: CodexDiagnostic = {
        severity: 'error',
        code: 'catalog-refresh-unavailable',
        message: 'Codex model catalog cannot refresh before app-server readiness',
      }
      this.state.ready = false
      this.state.diagnostic = diagnostic
      return { changed: false, catalog: this.state.catalog }
    }
    const result = await this.catalogManager.refreshCatalog()
    this.state.catalog = result.catalog
    this.state.diagnostic = result.diagnostics[0] ?? null
    this.state.ready = result.ok
    return { changed: result.changed, catalog: result.catalog }
  }

  /** Validate the frozen model and canonical effort immediately before turn/start. */
  validateTurnConfiguration(options: {
    readonly model?: unknown
    readonly reasoningEffort?: unknown
    readonly variant?: unknown
    readonly unknownKeys?: readonly string[]
  }) {
    return validateCodexTurnConfiguration(this.state.catalog, options)
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
    this.catalogManager = null
    if (options.clearDiagnostic) this.state.diagnostic = null
    const handle = this.server.handle
    this.server.unsubscribe?.()
    this.server.unsubscribe = null
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
    this.state.ready = false
    this.state.diagnostic = null
    this.state.catalog = null
    if (this.server.handle && !this.server.closed) {
      await this.tearDownHandle(this.server.handle)
    }
    const startupStartedAt = this.clock.now()
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
    let factoryOperation: Promise<CodexServerHandle> | null = null
    try {
      factoryOperation = factory({
        codexHome: this.deps.codexHome,
        cwd: this.deps.cwd,
        startupTimeoutMs: this.startupTimeoutMs,
        shutdownTimeoutMs: this.runtimeShutdownTimeoutMs,
        clock: this.clock,
      })
      handle = await this.withStartupTimeout(factoryOperation, this.startupTimeoutMs)
    } catch (cause) {
      const timedOut = cause instanceof Error && cause.message.includes('startup exceeded')
      if (timedOut && factoryOperation) {
        void factoryOperation.then(
          (lateHandle) => boundedWait(() => lateHandle.close(), this.runtimeShutdownTimeoutMs),
          () => undefined,
        )
      }
      const diagnostic: CodexDiagnostic = {
        severity: 'error',
        code: timedOut ? 'startup-timeout' : 'server-spawn-failed',
        message: redactCodexCredentialString(cause instanceof Error ? cause.message : 'Codex app-server spawn failed'),
      }
      return this.recordFailure(diagnostic)
    }
    this.server.handle = handle
    this.server.closed = false
    this.server.unsubscribe = handle.subscribe((message) => this.observeServerMessage(message))

    // Run the initialize → initialized handshake. The handshake
    // rejects non-managed codexHome, malformed initialize responses,
    // and any child exit before the response arrives.
    const transport = codexInitializationTransportFromHandle(handle)
    const initBudget = Math.max(0, this.startupTimeoutMs - (this.clock.now() - startupStartedAt))
    const initResult = await performCodexInitialization(transport, {
      managedCodexHome: this.deps.codexHome,
      startupTimeoutMs: initBudget,
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
    let readinessResult: Awaited<ReturnType<typeof evaluateCodexReadiness>>
    try {
      readinessResult = await evaluateCodexReadiness({
        managedCodexHome: this.deps.codexHome,
        startupTimeoutMs: this.startupTimeoutMs,
        probe: readinessProbe,
        appServer: {
          started: !handle.hasExited?.(),
          initialized: initResult.value.handshakeComplete,
          startupElapsedMs: this.clock.now() - startupStartedAt,
        },
        clock: this.clock,
      })
    } catch (cause) {
      const diagnostic: CodexDiagnostic = {
        severity: 'error',
        code: 'readiness-failed',
        message: redactCodexCredentialString(cause instanceof Error ? cause.message : 'Codex readiness probe failed'),
      }
      await this.tearDownHandle(handle)
      return this.recordFailure(diagnostic)
    }
    if (!readinessResult.ok) {
      await this.tearDownHandle(handle)
      this.state.diagnostic = readinessResult.error.diagnostics[0] ?? {
        severity: 'error',
        code: 'readiness-failed',
        message: 'Codex readiness probe failed',
      }
      return { ok: false, error: readinessResult.error, diagnostics: readinessResult.diagnostics }
    }
    const protocolFailureDiagnostic = this.state.diagnostic as CodexDiagnostic | null
    if (handle.hasExited?.() || protocolFailureDiagnostic?.code === 'protocol-failure') {
      const diagnostic = protocolFailureDiagnostic ?? {
        severity: 'error' as const,
        code: 'protocol-failure',
        message: 'Codex app-server failed during readiness; refusing to claim work',
      }
      await this.tearDownHandle(handle)
      return this.recordFailure(diagnostic)
    }
    this.state.ready = true
    this.state.diagnostic = readinessResult.diagnostics[0] ?? null
    this.state.catalog = readinessResult.value.catalog
    this.catalogManager = catalogLoader
    this.generationCounter += 1
    this.state.generation = this.generationCounter
    return { ok: true, value: this.readyState(), diagnostics: readinessResult.diagnostics }
  }

  private recordFailure(diagnostic: CodexDiagnostic): CodexResult<CodexReadyState> {
    this.state.diagnostic = diagnostic
    const error = normalizeUnavailableRuntimeCodex([diagnostic])
    return { ok: false, error, diagnostics: error.diagnostics }
  }

  private async tearDownHandle(handle: CodexServerHandle): Promise<void> {
    this.server.unsubscribe?.()
    this.server.unsubscribe = null
    try {
      await boundedWait(() => handle.close(), this.runtimeShutdownTimeoutMs)
    } catch {
      /* best effort */
    }
    this.server.closed = true
    this.server.handle = null
  }

  private observeServerMessage(message: unknown): void {
    if (!message || typeof message !== 'object') return
    const candidate = message as { method?: unknown; params?: unknown }
    if (candidate.method !== 'protocol-failure') return
    const params = candidate.params && typeof candidate.params === 'object' ? candidate.params : null
    const reason =
      params && typeof (params as { reason?: unknown }).reason === 'string'
        ? (params as { reason: string }).reason
        : 'protocol-failure'
    const detail =
      params && typeof (params as { message?: unknown }).message === 'string'
        ? (params as { message: string }).message
        : 'Codex app-server protocol failure'
    this.state.ready = false
    this.state.generation = null
    this.state.catalog = null
    this.state.diagnostic = {
      severity: 'error',
      code: 'protocol-failure',
      message: redactCodexCredentialString(`Codex app-server ${reason}: ${detail}`),
    }
  }

  private async withStartupTimeout<T>(operation: Promise<T>, timeoutMs: number): Promise<T> {
    let timer: unknown
    const timeout = new Promise<never>((_, reject) => {
      timer = this.clock.setTimeout(
        () => reject(new Error(`Codex app-server startup exceeded ${timeoutMs}ms`)),
        timeoutMs,
      )
    })
    try {
      return await Promise.race([operation, timeout])
    } finally {
      if (timer !== undefined) this.clock.clearTimeout(timer)
    }
  }
}

/** Build the retained-snapshot catalog manager over the live app-server handle. */
function codexCatalogLoaderFromHandle(handle: CodexServerHandle): CodexCatalogManager {
  return createCodexModelCatalogLoader({
    send: (request) => handle.send(request),
  })
}
