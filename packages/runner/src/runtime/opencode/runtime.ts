/**
 * `OpenCodeRuntime` — Runner-side deep module for OpenCode execution.
 *
 * Owns:
 *   - shared Server/Client lifecycle via `createOpencodeServer()` /
 *     `createOpencodeClient()` (no direct spawn, no `--pure`, no
 *     `.opencode` lockfile cleanup);
 *   - the single `client.global.event()` subscription;
 *   - the readiness check (health plus model catalog load via
 *     `client.v2.provider.list()` + `client.v2.model.list()`);
 *   - error normalization to a small Mohist result set;
 *   - permission authorization (no auto-approve, no Workflow Approval).
 *
 * Callers depend only on Mohist-owned request/result types from
 * `./types.js`. The generated SDK is an implementation detail
 * contained inside this module.
 *
 * T-002 stops short of executing turns or running session commands —
 * those land in T-003/T-004/T-005, which wire this runtime into the
 * host and the Action adapter. What lives here is the lifecycle and
 * readiness contract the rest of the system depends on.
 */

import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type {
  RuntimeDiagnostic,
  RuntimeModelCatalog,
  RuntimeReadyState,
  RuntimeResult,
  RuntimeSessionCreateRequest,
  RuntimeSessionCreateResult,
} from "./types.js"
import { errorKindFor, normalizeTurnFailed, normalizeUnavailableRuntime } from "./errors.js"
import type { OpencodeServerHandle } from "./server-process.js"
import type { CatalogClient } from "./catalog.js"
import type { RuntimeEventSubscription } from "./event-subscription.js"

export interface OpenCodeRuntimeDeps {
  readonly directory: string
  readonly serverFactory: (directory: string, signal: AbortSignal) => Promise<OpencodeServerHandle>
  readonly catalogFactory: (client: OpencodeClient) => CatalogClient
  readonly eventSubscriptionFactory: (client: OpencodeClient) => RuntimeEventSubscription
  readonly rebuildDelayMs?: number
}

interface InternalState {
  ready: boolean
  diagnostic: RuntimeDiagnostic | null
  catalog: RuntimeModelCatalog | null
  server: OpencodeServerHandle | null
  catalogClient: CatalogClient | null
  events: RuntimeEventSubscription | null
  exitWatcher: Promise<void> | null
  rebuildTriggered: boolean
}

export class OpenCodeRuntime {
  private readonly deps: OpenCodeRuntimeDeps
  private readonly state: InternalState
  private startInFlight: Promise<RuntimeResult<RuntimeReadyState>> | null = null

  constructor(deps: OpenCodeRuntimeDeps) {
    this.deps = deps
    this.state = {
      ready: false,
      diagnostic: null,
      catalog: null,
      server: null,
      catalogClient: null,
      events: null,
      exitWatcher: null,
      rebuildTriggered: false,
    }
  }

  /**
   * Idempotent start. Returns the readiness state. Re-running after a
   * failure or exit triggers a rebuild; concurrent callers share a
   * single in-flight attempt.
   */
  async start(signal: AbortSignal = new AbortController().signal): Promise<RuntimeResult<RuntimeReadyState>> {
    if (this.state.ready) {
      return { ok: true, value: this.readyState(), diagnostics: [] }
    }
    if (this.startInFlight) {
      return this.startInFlight
    }
    const attempt = this.attemptStart(signal)
    this.startInFlight = attempt
    try {
      return await attempt
    } finally {
      this.startInFlight = null
    }
  }

  ready(): boolean {
    return this.state.ready
  }

  diagnostic(): RuntimeDiagnostic | null {
    return this.state.diagnostic
  }

  catalog(): RuntimeModelCatalog | null {
    return this.state.catalog
  }

  /**
   * Resolve or create a physical Session via `client.session.create()`.
   * In T-002 this is the first boundary call that exercises the
   * runtime; the full turn execution lands in T-004. The result is
   * already a Mohist-owned shape (no SDK DTO leaks).
   */
  async createSession(
    request: RuntimeSessionCreateRequest,
  ): Promise<RuntimeResult<RuntimeSessionCreateResult>> {
    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const client = this.state.server.client
    try {
      const created = await client.session.create({
        directory: request.target.workDir,
        ...(request.model ? { model: { id: request.model.modelID, providerID: request.model.providerID } } : {}),
      })
      const data = created?.data as { id?: string } | undefined
      if (!data || typeof data.id !== "string") {
        const error = normalizeTurnFailed({ message: "session.create returned no id" })
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      const result: RuntimeSessionCreateResult = {
        runtimeSessionId: data.id,
        workDir: request.target.workDir,
      }
      return { ok: true, value: result, diagnostics: [] }
    } catch (cause) {
      const raw = toRawError(cause)
      const kind = errorKindFor(raw)
      const error =
        kind === "unavailable-runtime"
          ? normalizeUnavailableRuntime()
          : kind === "turn-failed"
            ? normalizeTurnFailed(raw)
            : normalizeTurnFailed(raw)
      return { ok: false, error, diagnostics: error.diagnostics }
    }
  }

  /**
   * Shut the runtime down. Cancels the in-flight subscription and
   * closes the shared server. The readiness diagnostic is preserved
   * unless `clearDiagnostic` is true (the public shutdown entry point
   * sets it; the rebuild path leaves the last-known diagnostic in
   * place so callers can still inspect it).
   */
  async shutdown(options: { clearDiagnostic?: boolean } = {}): Promise<void> {
    const { events, server } = this.state
    this.state.events = null
    this.state.server = null
    this.state.catalogClient = null
    this.state.catalog = null
    this.state.ready = false
    if (options.clearDiagnostic ?? true) {
      this.state.diagnostic = null
    }
    if (events) await events.close().catch(() => {})
    if (server) await server.close().catch(() => {})
  }

  private readyState(): RuntimeReadyState {
    return { ready: this.state.ready, diagnostic: this.state.diagnostic }
  }

  private async attemptStart(signal: AbortSignal): Promise<RuntimeResult<RuntimeReadyState>> {
    if (this.state.server !== null) {
      await this.shutdown().catch(() => {})
    }
    const diagnostics: RuntimeDiagnostic[] = []
    let server: OpencodeServerHandle
    try {
      server = await this.deps.serverFactory(this.deps.directory, signal)
    } catch (cause) {
      const diagnostic = toDiagnostic(cause, "server-spawn-failed", "Failed to start OpenCode server")
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }
    this.state.server = server

    try {
      const health = await server.client.global.health()
      if (!health?.data) {
        const diagnostic: RuntimeDiagnostic = {
          severity: "error",
          code: "health-failed",
          message: "OpenCode health check returned an empty body",
        }
        this.state.diagnostic = diagnostic
        diagnostics.push(diagnostic)
        const error = normalizeUnavailableRuntime(diagnostics)
        return { ok: false, error, diagnostics }
      }
    } catch (cause) {
      const diagnostic = toDiagnostic(cause, "health-failed", "OpenCode health check failed")
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      await server.close().catch(() => {})
      this.state.server = null
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }

    const catalogClient = this.deps.catalogFactory(server.client)
    this.state.catalogClient = catalogClient
    let catalog: RuntimeModelCatalog
    try {
      catalog = await catalogClient.list()
    } catch (cause) {
      const diagnostic = toDiagnostic(cause, "catalog-load-failed", "Failed to load OpenCode model catalog")
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      await server.close().catch(() => {})
      this.state.server = null
      this.state.catalogClient = null
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }
    this.state.catalog = catalog

    const events = this.deps.eventSubscriptionFactory(server.client)
    this.state.events = events
    this.watchExit(events, server)

    this.state.ready = true
    this.state.diagnostic = null
    return { ok: true, value: this.readyState(), diagnostics }
  }

  private watchExit(events: RuntimeEventSubscription, server: OpencodeServerHandle): void {
    const triggerRebuild = () => {
      if (this.state.rebuildTriggered) return
      if (!this.state.ready) return
      if (this.state.server !== server) return
      this.state.rebuildTriggered = true
      this.state.ready = false
      this.state.diagnostic = {
        severity: "error",
        code: "server-exit",
        message: "OpenCode server exited; rebuilding runtime",
      }
      this.scheduleRebuild()
    }
    const listener = (event: { type: string }) => {
      if (event.type === "server.disconnected" || event.type === "server.heartbeat-failed") {
        triggerRebuild()
      }
    }
    events.subscribe(listener)
    this.state.exitWatcher = new Promise<void>(() => {
      // The subscription closes when the server drops; the listener
      // path above triggers the rebuild. This promise is intentionally
      // long-lived so external code can await it on shutdown.
    })
  }

  private scheduleRebuild(): void {
    const delay = this.deps.rebuildDelayMs ?? 0
    const fire = async () => {
      if (delay > 0) {
        await new Promise<void>((resolve) => {
          const timer = setTimeout(resolve, delay)
          timer.unref?.()
        })
      }
      this.state.rebuildTriggered = false
      await this.shutdown({ clearDiagnostic: false }).catch(() => {})
      await this.start().catch(() => {})
    }
    void fire()
  }
}

function toDiagnostic(cause: unknown, code: string, fallback: string): RuntimeDiagnostic {
  if (cause instanceof Error) {
    return { severity: "error", code, message: cause.message || fallback }
  }
  return { severity: "error", code, message: fallback, details: { cause: String(cause) } }
}

function toRawError(cause: unknown): { message: string; status?: number; code?: string; service?: string } {
  if (cause instanceof Error) {
    const message = cause.message || "OpenCode error"
    const status = (cause as { status?: number }).status
    const code = (cause as { code?: string }).code
    const service = (cause as { service?: string }).service
    return { message, ...(typeof status === "number" ? { status } : {}), ...(typeof code === "string" ? { code } : {}), ...(typeof service === "string" ? { service } : {}) }
  }
  return { message: String(cause) }
}
