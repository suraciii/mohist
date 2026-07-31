/**
 * `OpenCodeRuntime` — Runner-side deep module for OpenCode execution.
 *
 * Owns:
 *   - shared Server/Client lifecycle via `createOpencodeServer()` /
 *     `createOpencodeClient()` (no direct spawn, no `--pure`, no
 *     `.opencode` lockfile cleanup);
 *   - the single `client.global.event()` subscription;
 *   - the readiness check (server health plus global event subscription);
 *   - error normalization to a small Mohist result set;
 *   - permission authorization (no auto-approve, no Workflow Approval);
 *   - Workflow Inline Agent turn execution over the native
 *     `client.session.*` surface.
 *
 * Callers depend only on Mohist-owned request/result types from
 * `./types.js`. The generated SDK is an implementation detail
 * contained inside this module.
 */

import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type {
  RuntimeCancelFacts,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeDiagnostic,
  RuntimeFollowupFacts,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeProviderErrorPolicy,
  RuntimeReadyState,
  RuntimeResult,
  RuntimeSessionCreateRequest,
  RuntimeSessionCreateResult,
  RuntimeSessionResolveRequest,
  RuntimeSessionResolveResult,
  RuntimeTurnObserver,
  RuntimeTurnOptions,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from "./types.js"
import { errorKindFor, normalizeInvalidInput, normalizeMissingSession, normalizeTurnFailed, normalizeUnavailableRuntime } from "./errors.js"
import type { OpencodeServerHandle } from "./server-process.js"
import type { RuntimeEventSubscription } from "./event-subscription.js"
import type { WorkspaceRemovalFenceResult } from "../workspace-removal-fence.js"
import { runTurn, bindTurnInFlightTracker, type TurnExecutionDeps } from "./turn.js"
import {
  OpenCodeDirectoryInstances,
  type DirectoryReclaimResult,
  type DirectoryReleaseResult,
} from "./directory-instance.js"

export interface OpenCodeRuntimeDeps {
  readonly directory: string
  readonly serverFactory?: (directory: string, signal: AbortSignal) => Promise<OpencodeServerHandle>
  readonly eventSubscriptionFactory?: (client: OpencodeClient) => RuntimeEventSubscription
  readonly rebuildDelayMs?: number
  /**
   * Optional override for the provider-error failure policy. Defaults
   * to the quota/credit/billing pattern set with a consecutive-retry
   * threshold of 5. See `errors.ts`.
   */
  readonly providerErrorPolicy?: RuntimeProviderErrorPolicy
}

interface InternalState {
  ready: boolean
  diagnostic: RuntimeDiagnostic | null
  server: OpencodeServerHandle | null
  events: RuntimeEventSubscription | null
  exitWatcher: Promise<void> | null
  rebuildTriggered: boolean
}

export class OpenCodeRuntime {
  private readonly deps: OpenCodeRuntimeDeps
  private readonly state: InternalState
  private startInFlight: Promise<RuntimeResult<RuntimeReadyState>> | null = null
  private inFlight: ReturnType<typeof bindTurnInFlightTracker> | null = null
  private readonly directoryInstances: OpenCodeDirectoryInstances

  constructor(deps: OpenCodeRuntimeDeps) {
    this.deps = deps
    this.state = {
      ready: false,
      diagnostic: null,
      server: null,
      events: null,
      exitWatcher: null,
      rebuildTriggered: false,
    }
    this.directoryInstances = new OpenCodeDirectoryInstances(() => this.state.server?.client ?? null)
  }

  async reclaimWhere(predicate: (directory: string) => boolean): Promise<DirectoryReclaimResult> {
    return await this.directoryInstances.reclaimWhere(predicate)
  }

  async release(directory: string): Promise<DirectoryReleaseResult> {
    return await this.directoryInstances.release(directory)
  }

  async withRemovalFence<T>(directory: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
    if (!this.state.ready || !this.state.server) return { kind: "failed" }
    return await this.directoryInstances.withRemovalFence(directory, callback)
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

  /**
   * Resolve or create a physical Session via `client.session.create()`.
   * The result is
   * already a Mohist-owned shape (no SDK DTO leaks).
   */
  async resolveSession(
    request: RuntimeSessionResolveRequest,
  ): Promise<RuntimeResult<RuntimeSessionResolveResult>> {
    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (!request.target.runtimeSessionId) {
      const error = normalizeMissingSession()
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const runtimeSessionId = request.target.runtimeSessionId
    return await this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
      const server = this.state.server
      if (!server || !this.state.ready) {
        const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      lease.markUsed()
      let sessionData: { id: string }
      try {
        const resolved = await server.client.session.get({
          sessionID: runtimeSessionId,
          directory: request.target.workDir,
        }, { throwOnError: true })
        const data = resolved.data
        if (!data || typeof data !== "object" || data.id !== request.target.runtimeSessionId) {
          const error = normalizeTurnFailed({ message: "OpenCode session.get returned a malformed or mismatched Session" })
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        sessionData = data
      } catch (cause) {
        if ((cause as { status?: number } | undefined)?.status === 404) {
          const error = normalizeMissingSession()
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        const error = normalizeTurnFailed({ message: errorMessage(cause, "Failed to resolve persisted Runtime Session") })
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      try {
        const statusResponse = await server.client.session.status(
          { directory: request.target.workDir },
          { throwOnError: true },
        )
        const statuses = statusResponse.data
        if (!statuses || typeof statuses !== "object") {
          const error = normalizeTurnFailed({ message: "session.status returned no status map" })
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        const status = statuses[runtimeSessionId]
        return {
          ok: true,
          value: {
            runtimeSessionId: sessionData.id,
            workDir: request.target.workDir,
            activeTurn: status !== undefined && status.type !== "idle",
          },
          diagnostics: [],
        }
      } catch (cause) {
        const error = normalizeTurnFailed({ message: errorMessage(cause, "Failed to read Runtime Session active-turn status") })
        return { ok: false, error, diagnostics: error.diagnostics }
      }
    })
  }

  async createSession(
    request: RuntimeSessionCreateRequest,
  ): Promise<RuntimeResult<RuntimeSessionCreateResult>> {
    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return await this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
      const server = this.state.server
      if (!server || !this.state.ready) {
        const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      lease.markUsed()
      try {
        const created = await server.client.session.create({
          directory: request.target.workDir,
          ...(request.model ? { model: { providerID: request.model.providerID, id: request.model.modelID } } : {}),
        }, { throwOnError: true })
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
    })
  }

  /**
   * Run a Workflow Inline Agent turn over the OpenCode runtime. The
   * runtime:
   *
   *   - resolves or creates the physical Session via
   *     `client.session.create()` (no rotation on model/variant
   *     change; rotation is governed by the binding + directory);
   *   - constructs the SDK model DTO from the parsed provider/model
   *     and applies model/variant on the prompt body
   *     (`client.session.prompt()`);
   *   - awaits the prompt response as the sole completion authority
   *     (no `client.v2.session.wait()`);
   *   - watches `session.status` retry events for provider-error
   *     recoverability (per the design call table), aborting and
   *     returning `turn-failed` only when a non-recoverable pattern
   *     matches or `attempt` reaches the consecutive-retry
   *     threshold;
   *   - uses the caller's abort signal as the deadline, calling
   *     `client.session.abort()` on deadline and returning
   *     `interrupted`;
   *   - does not auto-replay an uncertain prompt submission;
   *   - never exposes SDK DTOs across the boundary.
   *
   * The returned `RuntimeTurnResult.facts.finalAssistantText` is the
   * private turn fact the Workflow task executor evaluates `path:
   * _output` against; the Action does not synthesize `{ promise }`.
   */
  async runTurn(
    request: RuntimeTurnRequest,
    signal: AbortSignal,
    observer?: RuntimeTurnObserver,
  ): Promise<RuntimeResult<RuntimeTurnResult>> {
    if (!this.state.server || !this.state.ready || !this.state.events) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const inFlight = this.ensureInFlightTracker()
    const sessionKey = request.target.runtimeSessionId ?? `${request.target.workDir}::pending`
    if (!inFlight.start(sessionKey)) {
      const error = normalizeUnavailableRuntime([{
        severity: "error",
        code: "in-flight",
        message: "Another work prompt is already running for this AgentSession",
      }])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    try {
      return await this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
        const server = this.state.server
        const events = this.state.events
        if (!server || !this.state.ready || !events) {
          const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        const deps: TurnExecutionDeps = {
          client: server.client,
          events,
          ...(this.deps.providerErrorPolicy ? { policy: this.deps.providerErrorPolicy } : {}),
          markDirectoryUsed: lease.markUsed,
          trackPendingOperation: lease.trackPending,
        }
        const result = await runTurn(request, deps, signal, observer)
        if (!result.ok && result.error.diagnostics.some((diagnostic) => diagnostic.code === "opencode-transport-failed")) {
          this.triggerRebuild(server)
        }
        return result
      })
    } finally {
      inFlight.end(sessionKey)
    }
  }

  /**
   * Dispatch a Follow-up prompt to an existing Runtime Session.
   *
   * Wraps `client.session.promptAsync`.
   * The runtime verifies the persisted binding still resolves to a
   * live physical Session before dispatching; a stale binding surfaces
   * as `missing-session` with the existing Reset hint. The dispatch
   * is fire-and-forget at the SDK layer — the prompt returns when
   * the message is on the wire, not when the agent finishes. The
   * runner-side handler treats this as `accepted: true` regardless of
   * the eventual turn outcome (turn completion is observed through
   * the existing global event subscription + AgentSession event
   * channel, the same way the Workflow source observes it).
   *
   * `options.model` / `options.variant` apply to the prompt body only
   * — the physical Session is never rotated on a Follow-up.
   */
  async followup(request: RuntimeFollowupRequest): Promise<RuntimeResult<RuntimeFollowupResult>> {
    const diagnostics: RuntimeDiagnostic[] = []

    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }

    const validation = validateFollowupInput(request, diagnostics)
    if (validation.kind === "failure") {
      return { ok: false, error: validation.error, diagnostics: [...diagnostics, ...validation.error.diagnostics] }
    }
    const { model, variant } = validation.value

    if (!request.target.runtimeSessionId) {
      const error = normalizeInvalidInput(
        "Follow-up requires a current OpenCode Runtime Session binding; pass the persisted runtimeSessionId from the AgentSession binding",
        [{
          severity: "error",
          code: "missing-binding",
          message: "Reset the session to establish a fresh Runtime Session, then retry",
        }],
      )
      return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
    }
    const runtimeSessionId = request.target.runtimeSessionId

    return await this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
      const server = this.state.server
      if (!server || !this.state.ready) {
        const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      lease.markUsed()
      const client = server.client
      try {
        const resolved = await client.session.get({
          sessionID: runtimeSessionId,
          directory: request.target.workDir,
        }, { throwOnError: true })
        const resolvedData = resolved.data
        if (!resolvedData || resolvedData.id !== runtimeSessionId) {
          const error = normalizeMissingSession()
          return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
        }
      } catch (cause) {
        const status = (cause as { status?: number } | undefined)?.status
        if (status === 404) {
          const error = normalizeMissingSession()
          return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
        }
        const error = normalizeTurnFailed({ message: errorMessage(cause, "Failed to resolve persisted Runtime Session") })
        return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
      }

      try {
        await client.session.promptAsync({
          sessionID: runtimeSessionId,
          directory: request.target.workDir,
          parts: [
            { type: "text", text: request.prompt },
            ...(request.fileParts ?? []).map((part) => ({ type: "file" as const, ...part })),
          ],
          ...(model ? { model: { providerID: model.providerID, modelID: model.modelID } } : {}),
          ...(variant ? { variant } : {}),
        }, { throwOnError: true })
        const facts: RuntimeFollowupFacts = {
          runtimeSessionId,
          workDir: request.target.workDir,
        }
        return { ok: true, value: { facts, diagnostics }, diagnostics }
      } catch (cause) {
        const status = (cause as { status?: number } | undefined)?.status
        if (status === 404) {
          const error = normalizeMissingSession()
          return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
        }
        const error = normalizeTurnFailed({ message: errorMessage(cause, "OpenCode follow-up prompt failed") })
        return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
      }
    })
  }

  /**
   * Cancel an active Runtime Session turn.
   *
   * Wraps `client.session.abort`. The
   * runtime resolves the binding first; a stale binding surfaces as
   * `missing-session` with the existing Reset hint. `cancelled: true`
   * is the authoritative reply — whether the agent honours the
   * cancellation is the agent's decision; the runtime reports the
   * attempt honestly (matches the `not-cancellable` vs `cancelled`
   * taxonomy the cancel handler already speaks).
   */
  async cancel(request: RuntimeCancelRequest): Promise<RuntimeResult<RuntimeCancelResult>> {
    const diagnostics: RuntimeDiagnostic[] = []

    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }

    if (!request.target.runtimeSessionId) {
      const error = normalizeInvalidInput(
        "Cancel requires a current OpenCode Runtime Session binding; pass the persisted runtimeSessionId from the AgentSession binding",
        [{
          severity: "error",
          code: "missing-binding",
          message: "Reset the session to establish a fresh Runtime Session, then retry",
        }],
      )
      return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
    }
    const runtimeSessionId = request.target.runtimeSessionId

    return await this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
      const server = this.state.server
      if (!server || !this.state.ready) {
        const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      lease.markUsed()
      try {
        await server.client.session.abort({
          sessionID: runtimeSessionId,
          directory: request.target.workDir,
        }, { throwOnError: true })
        const facts: RuntimeCancelFacts = {
          runtimeSessionId,
          workDir: request.target.workDir,
          cancelled: true,
        }
        return { ok: true, value: { facts, diagnostics }, diagnostics }
      } catch (cause) {
        const status = (cause as { status?: number } | undefined)?.status
        if (status === 404) {
          const error = normalizeMissingSession()
          return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
        }
        const error = normalizeTurnFailed({ message: errorMessage(cause, "OpenCode cancel failed") })
        return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
      }
    })
  }

  private ensureInFlightTracker() {
    if (!this.inFlight) this.inFlight = bindTurnInFlightTracker()
    return this.inFlight
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
    this.directoryInstances.resetGeneration()
    this.state.events = null
    this.state.server = null
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
    const serverFactory = this.deps.serverFactory
    if (!serverFactory) {
      const diagnostic: RuntimeDiagnostic = {
        severity: "error",
        code: "server-spawn-failed",
        message: "OpenCode server factory was not provided",
      }
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }
    const eventSubscriptionFactory = this.deps.eventSubscriptionFactory
    if (!eventSubscriptionFactory) {
      const diagnostic: RuntimeDiagnostic = {
        severity: "error",
        code: "server-spawn-failed",
        message: "OpenCode event subscription factory was not provided",
      }
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }
    let server: OpencodeServerHandle
    try {
      server = await serverFactory(this.deps.directory, signal)
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

    const events = eventSubscriptionFactory(server.client)
    this.state.events = events
    this.watchExit(events, server)

    this.state.ready = true
    this.state.diagnostic = null
    return { ok: true, value: this.readyState(), diagnostics }
  }

  private watchExit(events: RuntimeEventSubscription, server: OpencodeServerHandle): void {
    const listener = (event: { type: string }) => {
      if (event.type === "server.disconnected" || event.type === "server.heartbeat-failed") {
        this.triggerRebuild(server)
      }
    }
    events.subscribe(listener)
    this.state.exitWatcher = new Promise<void>(() => {
      // The subscription closes when the server drops; the listener
      // path above triggers the rebuild. This promise is intentionally
      // long-lived so external code can await it on shutdown.
    })
  }

  private triggerRebuild(server: OpencodeServerHandle): void {
    if (this.state.rebuildTriggered) return
    if (!this.state.ready) return
    if (this.state.server !== server) return
    this.state.rebuildTriggered = true
    this.directoryInstances.resetGeneration()
    this.state.ready = false
    this.state.diagnostic = {
      severity: "error",
      code: "server-exit",
      message: "OpenCode server exited; rebuilding runtime",
    }
    this.scheduleRebuild()
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

function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof Error) return cause.message || fallback
  return String(cause) || fallback
}

type FollowupValidationOk = {
  kind: "ok"
  value: { model: { providerID: string; modelID: string } | null; variant: string | null }
}
type FollowupValidationFailure = {
  kind: "failure"
  error: ReturnType<typeof normalizeInvalidInput>
}
type FollowupValidationResult = FollowupValidationOk | FollowupValidationFailure

function validateFollowupInput(
  request: RuntimeFollowupRequest,
  diagnostics: RuntimeDiagnostic[],
): FollowupValidationResult {
  const options: RuntimeTurnOptions | undefined | null = request.options ?? undefined
  if (options?.unknownKeys && options.unknownKeys.length > 0) {
    diagnostics.push({
      severity: "info",
      code: "options-unknown-keys",
      message: `Ignored unknown option keys: ${options.unknownKeys.join(", ")}`,
      details: { keys: options.unknownKeys },
    })
  }
  let model: { providerID: string; modelID: string } | null = null
  if (options?.model !== undefined && options.model !== null) {
    if (typeof options.model !== "object") {
      return { kind: "failure", error: normalizeInvalidInput("options.model must be an object with providerID and modelID when present") }
    }
    model = options.model
  }
  let variant: string | null = null
  if (options?.variant !== undefined && options.variant !== null) {
    if (typeof options.variant !== "string") {
      return { kind: "failure", error: normalizeInvalidInput("options.variant must be a string when present") }
    }
    variant = options.variant
  }
  if (!request.prompt || typeof request.prompt !== "string" || request.prompt.trim().length === 0) {
    return { kind: "failure", error: normalizeInvalidInput("Follow-up prompt must be a non-empty string") }
  }
  return { kind: "ok", value: { model, variant } }
}
