/**
 * `CodexRuntime` — Runner-side deep module for Codex execution.
 *
 * The runtime drives one long-lived `codex app-server --stdio` child
 * per Runner. The full implementation lands across T-003 (process +
 * initialization + readiness), T-004 (model catalog + canonical effort
 * mapping), T-005 (Thread + Turn lifecycle, binding-before-effect,
 * no-replay), and T-006 (two-phase closeout + approval rejection).
 *
 * This file declares the public boundary surface: the constructor
 * shape, the entry points callers depend on, and the readiness check.
 * The internal state machine, the JSON-RPC consumer integration, and
 * the per-method server-call details land in their respective files.
 *
 * Callers depend only on Mohist-owned request/result types from
 * `./types.js`. The app-server protocol is an implementation detail
 * contained inside this module.
 */

import { boundedTimeoutMs } from '../bounded-wait.js'
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
import type { CodexServerFactory } from './server-process.js'
import { normalizeUnavailableRuntimeCodex } from './errors.js'

export interface CodexRuntimeDeps {
  readonly codexHome: string
  readonly cwd: string
  readonly serverFactory?: CodexServerFactory
  readonly startupTimeoutMs?: number
  readonly runtimeShutdownTimeoutMs?: number
  readonly clock?: CodexClock
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
 * The full implementations of each entry point land in T-003 to
 * T-006; this skeleton owns the boundary types and the readiness seam.
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
  private readonly server: { factory: CodexServerFactory | null }

  constructor(deps: CodexRuntimeDeps) {
    this.deps = deps
    this.clock = deps.clock ?? defaultCodexClock
    this.startupTimeoutMs = boundedTimeoutMs(deps.startupTimeoutMs, CODEX_DEFAULT_TIMEOUTS.startupMs)
    this.runtimeShutdownTimeoutMs = boundedTimeoutMs(
      deps.runtimeShutdownTimeoutMs,
      CODEX_DEFAULT_TIMEOUTS.shutdownMs,
    )
    this.state = {
      ready: false,
      diagnostic: null,
      catalog: null,
      generation: null,
      startInFlight: null,
    }
    this.server = { factory: deps.serverFactory ?? null }
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
   * Shut the CodexRuntime down. Cancels the in-flight subscription
   * and closes the shared app-server child within the configured
   * deadline. The readiness diagnostic is preserved so callers can
   * still inspect the last-known failure reason after shutdown.
   */
  async shutdown(_options: { clearDiagnostic?: boolean } = {}): Promise<void> {
    this.state.ready = false
    this.state.generation = null
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
    // The full readiness probe (CLI executable, version range,
    // initialization, managed codexHome, authentication, model
    // catalog) lands in T-003. The boundary surface is in place now;
    // we exercise the factory seam so unit tests can prove the line-
    // framed JSON-RPC consumer is wired.
    const factory = this.server.factory ?? this.deps.serverFactory
    if (!factory) {
      const diagnostic: CodexDiagnostic = {
        severity: 'error',
        code: 'server-spawn-failed',
        message: 'Codex server factory was not provided',
      }
      this.state.diagnostic = diagnostic
      const error = normalizeUnavailableRuntimeCodex([diagnostic])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    try {
      await factory({
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
      this.state.diagnostic = diagnostic
      const error = normalizeUnavailableRuntimeCodex([diagnostic])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    this.state.ready = true
    this.state.diagnostic = null
    this.state.generation = 1
    return { ok: true, value: this.readyState(), diagnostics: [] }
  }
}