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

import type { OpencodeClient } from '@opencode-ai/sdk/v2'
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
  RuntimeSessionTarget,
  RuntimeTurnObserver,
  RuntimeTurnOptions,
  RuntimeTurnRequest,
  RuntimeTurnResult,
  RuntimeOwnershipSnapshot,
} from './types.js'
import {
  errorKindFor,
  hasUnconfirmedCleanup,
  normalizeGenerationDrainTimeout,
  normalizeInvalidInput,
  normalizeResourceContainment,
  normalizeMissingSession,
  normalizeTurnFailed,
  normalizeUnavailableRuntime,
} from './errors.js'
import type { OpencodeServerFactory, OpencodeServerHandle } from './server-process.js'
import { DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS } from './server-process.js'
import { boundedTimeoutMs, boundedWait } from '../bounded-wait.js'
import { createTimeoutSignal } from '../../system/timeout-signal.js'
import type { RuntimeEventSubscription } from './event-subscription.js'
import type { WorkspaceRemovalFenceResult } from '../workspace-removal-fence.js'
import {
  abortAndConfirmSession,
  runTurn,
  reattachTurn as waitForReattachedTurn,
  bindTurnInFlightTracker,
  type TurnExecutionDeps,
} from './turn.js'
import {
  OpenCodeDirectoryInstances,
  type DirectoryReclaimResult,
  type DirectoryReleaseResult,
} from './directory-instance.js'
import {
  combineAbortSignals,
  errorMessage,
  newRuntimeGeneration,
  positiveDuration,
  toDiagnostic,
  toRawError,
  validateFollowupInput,
  type ActiveGenerationTurn,
  type RuntimeGeneration,
} from './runtime-helpers.js'

export interface OpenCodeRuntimeDeps {
  readonly directory: string
  readonly serverFactory?: OpencodeServerFactory
  readonly eventSubscriptionFactory?: (client: OpencodeClient) => RuntimeEventSubscription
  readonly rebuildDelayMs?: number
  readonly idleGraceMs?: number
  readonly quarantineDrainTimeoutMs?: number
  readonly runtimeShutdownTimeoutMs?: number
  readonly clock?: RuntimeClock
  /**
   * Optional override for the provider-error failure policy. Defaults
   * to the quota/credit/billing pattern set with a consecutive-retry
   * threshold of 5. See `errors.ts`.
   */
  readonly providerErrorPolicy?: RuntimeProviderErrorPolicy
}

export interface RuntimeClock {
  readonly now: () => number
  readonly setTimeout: (callback: () => void, delayMs: number) => unknown
  readonly clearTimeout: (handle: unknown) => void
}

const defaultRuntimeClock: RuntimeClock = {
  now: () => Date.now(),
  setTimeout: (callback, delayMs) => setTimeout(callback, delayMs),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}

const DEFAULT_IDLE_GRACE_MS = 5 * 60_000

interface InternalState {
  ready: boolean
  diagnostic: RuntimeDiagnostic | null
  server: OpencodeServerHandle | null
  events: RuntimeEventSubscription | null
  generation: RuntimeGeneration | null
  exitWatcher: Promise<void> | null
  rebuildTriggered: boolean
  ownerIds: Set<string>
  idleSince: number | null
  idleTimer: unknown
  activeOperations: number
}

export class OpenCodeRuntime {
  private readonly deps: OpenCodeRuntimeDeps
  private readonly state: InternalState
  private startInFlight: Promise<RuntimeResult<RuntimeReadyState>> | null = null
  private rebuildInFlight: Promise<RuntimeResult<RuntimeReadyState>> | null = null
  private inFlight: ReturnType<typeof bindTurnInFlightTracker> | null = null
  private readonly directoryInstances: OpenCodeDirectoryInstances
  private nextGenerationId = 1
  private readonly clock: RuntimeClock
  private readonly idleGraceMs: number
  private readonly quarantineDrainTimeoutMs: number
  private readonly runtimeShutdownTimeoutMs: number

  constructor(deps: OpenCodeRuntimeDeps) {
    this.deps = deps
    this.clock = deps.clock ?? defaultRuntimeClock
    this.idleGraceMs = Math.max(0, Math.floor(deps.idleGraceMs ?? DEFAULT_IDLE_GRACE_MS))
    this.quarantineDrainTimeoutMs = boundedTimeoutMs(deps.quarantineDrainTimeoutMs, 60_000)
    this.runtimeShutdownTimeoutMs = boundedTimeoutMs(deps.runtimeShutdownTimeoutMs, DEFAULT_RUNTIME_SHUTDOWN_TIMEOUT_MS)
    this.state = {
      ready: false,
      diagnostic: null,
      server: null,
      events: null,
      generation: null,
      exitWatcher: null,
      rebuildTriggered: false,
      ownerIds: new Set(),
      idleSince: null,
      idleTimer: null,
      activeOperations: 0,
    }
    this.directoryInstances = new OpenCodeDirectoryInstances(() => this.state.server?.client ?? null)
  }

  async reclaimWhere(predicate: (directory: string) => boolean): Promise<DirectoryReclaimResult> {
    return await this.withRuntimeOperation(() => this.directoryInstances.reclaimWhere(predicate))
  }

  async release(directory: string): Promise<DirectoryReleaseResult> {
    return await this.withRuntimeOperation(() => this.directoryInstances.release(directory))
  }

  async withRemovalFence<T>(directory: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
    if (!this.state.ready || !this.state.server) return { kind: 'failed' }
    return await this.withRuntimeOperation(() => this.directoryInstances.withRemovalFence(directory, callback))
  }

  setWorkOwners(ownerIds: readonly string[]): void {
    this.state.ownerIds = new Set(ownerIds.filter((ownerId) => ownerId.trim().length > 0))
    this.reconcileIdleLifecycle()
  }

  ownership(): RuntimeOwnershipSnapshot {
    return {
      ownerIds: [...this.state.ownerIds].sort(),
      idleSince: this.state.idleSince,
      activeOperations: this.state.activeOperations,
      generation: this.state.generation?.id ?? null,
    }
  }

  canPollWhileCold(): boolean {
    return !this.state.server && !this.startInFlight && !this.rebuildInFlight && this.state.diagnostic === null
  }

  /**
   * Idempotent start. Returns the readiness state. Re-running after a
   * failure or exit triggers a rebuild; concurrent callers share a
   * single in-flight attempt.
   */
  async start(signal: AbortSignal = new AbortController().signal): Promise<RuntimeResult<RuntimeReadyState>> {
    if (this.state.ready) {
      this.reconcileIdleLifecycle()
      return { ok: true, value: this.readyState(), diagnostics: [] }
    }
    if (this.rebuildInFlight) return this.rebuildInFlight
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
  async resolveSession(request: RuntimeSessionResolveRequest): Promise<RuntimeResult<RuntimeSessionResolveResult>> {
    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (!request.target.runtimeSessionId) {
      const error = normalizeMissingSession()
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const runtimeSessionId = request.target.runtimeSessionId
    return await this.withRuntimeOperation(() =>
      this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
        const server = this.state.server
        if (!server || !this.state.ready) {
          const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        lease.markUsed()
        let sessionData: { id: string }
        try {
          const resolved = await server.client.session.get(
            {
              sessionID: runtimeSessionId,
              directory: request.target.workDir,
            },
            { throwOnError: true },
          )
          const data = resolved.data
          if (!data || typeof data !== 'object' || data.id !== request.target.runtimeSessionId) {
            const error = normalizeTurnFailed({
              message: 'OpenCode session.get returned a malformed or mismatched Session',
            })
            return { ok: false, error, diagnostics: error.diagnostics }
          }
          sessionData = data
        } catch (cause) {
          if ((cause as { status?: number } | undefined)?.status === 404) {
            const error = normalizeMissingSession()
            return { ok: false, error, diagnostics: error.diagnostics }
          }
          const error = normalizeTurnFailed({
            message: errorMessage(cause, 'Failed to resolve persisted Runtime Session'),
          })
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        try {
          const statusResponse = await server.client.session.status(
            { directory: request.target.workDir },
            { throwOnError: true },
          )
          const statuses = statusResponse.data
          if (!statuses || typeof statuses !== 'object') {
            const error = normalizeTurnFailed({ message: 'session.status returned no status map' })
            return { ok: false, error, diagnostics: error.diagnostics }
          }
          const status = statuses[runtimeSessionId]
          return {
            ok: true,
            value: {
              runtimeSessionId: sessionData.id,
              workDir: request.target.workDir,
              activeTurn: status !== undefined && status.type !== 'idle',
            },
            diagnostics: [],
          }
        } catch (cause) {
          const error = normalizeTurnFailed({
            message: errorMessage(cause, 'Failed to read Runtime Session active-turn status'),
          })
          return { ok: false, error, diagnostics: error.diagnostics }
        }
      }),
    )
  }

  async createSession(request: RuntimeSessionCreateRequest): Promise<RuntimeResult<RuntimeSessionCreateResult>> {
    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    return await this.withRuntimeOperation(() =>
      this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
        const server = this.state.server
        if (!server || !this.state.ready) {
          const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        lease.markUsed()
        try {
          const created = await server.client.session.create(
            {
              directory: request.target.workDir,
              ...(request.model ? { model: { providerID: request.model.providerID, id: request.model.modelID } } : {}),
            },
            { throwOnError: true },
          )
          const data = created?.data as { id?: string } | undefined
          if (!data || typeof data.id !== 'string') {
            const error = normalizeTurnFailed({ message: 'session.create returned no id' })
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
            kind === 'unavailable-runtime'
              ? normalizeUnavailableRuntime()
              : kind === 'turn-failed'
                ? normalizeTurnFailed(raw)
                : normalizeTurnFailed(raw)
          return { ok: false, error, diagnostics: error.diagnostics }
        }
      }),
    )
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
    return await this.withRuntimeOperation(() => this.runTurnCore(request, signal, observer))
  }

  /**
   * Adopt an already-running physical turn after the host process restarted.
   * This operation shares generation drain tracking with ordinary turns but
   * deliberately does not submit another prompt.
   */
  async reattachTurn(
    request: { readonly target: RuntimeSessionTarget },
    signal: AbortSignal,
    observer?: RuntimeTurnObserver,
  ): Promise<RuntimeResult<RuntimeTurnResult>> {
    return await this.withRuntimeOperation(() => this.reattachTurnCore(request, signal, observer))
  }

  private async reattachTurnCore(
    request: { readonly target: RuntimeSessionTarget },
    signal: AbortSignal,
    observer?: RuntimeTurnObserver,
  ): Promise<RuntimeResult<RuntimeTurnResult>> {
    const generation = this.acquireReadyGeneration()
    if (!generation) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const inFlight = this.ensureInFlightTracker()
    const sessionKey = request.target.runtimeSessionId ?? `${request.target.workDir}::pending`
    if (!inFlight.start(sessionKey)) {
      this.releaseGeneration(generation)
      const error = normalizeUnavailableRuntime([
        {
          severity: 'error',
          code: 'in-flight',
          message: 'Another work prompt is already running for this AgentSession',
        },
      ])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    let resolveForced!: () => void
    const forced = new Promise<void>((resolve) => {
      resolveForced = resolve
    })
    const activeTurn: ActiveGenerationTurn = {
      abortController: new AbortController(),
      forced,
      resolveForced,
      forcedFailure: false,
    }
    generation.activeTurns.add(activeTurn)
    const combined = combineAbortSignals(signal, activeTurn.abortController.signal)
    try {
      return await this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
        const deps: TurnExecutionDeps = {
          client: generation.server.client,
          events: generation.events,
          ...(this.deps.providerErrorPolicy ? { policy: this.deps.providerErrorPolicy } : {}),
          markDirectoryUsed: lease.markUsed,
          trackPendingOperation: lease.trackPending,
        }
        const adopted = await Promise.race([
          waitForReattachedTurn(
            { target: request.target, prompt: '', options: null },
            deps,
            combined.signal,
            observer,
          ).then((result) => ({ kind: 'result' as const, result })),
          activeTurn.forced.then(() => ({ kind: 'forced' as const })),
        ])
        if (adopted.kind === 'forced') {
          const error = normalizeGenerationDrainTimeout(this.quarantineDrainTimeoutMs)
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        if (activeTurn.forcedFailure) {
          const error = normalizeGenerationDrainTimeout(this.quarantineDrainTimeoutMs)
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        return adopted.result
      })
    } finally {
      combined.dispose()
      generation.activeTurns.delete(activeTurn)
      inFlight.end(sessionKey)
      this.releaseGeneration(generation)
    }
  }

  private async runTurnCore(
    request: RuntimeTurnRequest,
    signal: AbortSignal,
    observer?: RuntimeTurnObserver,
  ): Promise<RuntimeResult<RuntimeTurnResult>> {
    const generation = this.acquireReadyGeneration()
    if (!generation) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const inFlight = this.ensureInFlightTracker()
    const sessionKey = request.target.runtimeSessionId ?? `${request.target.workDir}::pending`
    if (!inFlight.start(sessionKey)) {
      this.releaseGeneration(generation)
      const error = normalizeUnavailableRuntime([
        {
          severity: 'error',
          code: 'in-flight',
          message: 'Another work prompt is already running for this AgentSession',
        },
      ])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    let resolveForced!: () => void
    const forced = new Promise<void>((resolve) => {
      resolveForced = resolve
    })
    const activeTurn: ActiveGenerationTurn = {
      abortController: new AbortController(),
      forced,
      resolveForced,
      forcedFailure: false,
    }
    generation.activeTurns.add(activeTurn)
    const budgetMs = positiveDuration(request.resourceBudgetMs)
    const budget = budgetMs === undefined ? null : createTimeoutSignal(signal, budgetMs)
    const combined = combineAbortSignals(budget?.signal ?? signal, activeTurn.abortController.signal)
    try {
      return await this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
        const server = generation.server
        const events = generation.events
        const deps: TurnExecutionDeps = {
          client: server.client,
          events,
          ...(this.deps.providerErrorPolicy ? { policy: this.deps.providerErrorPolicy } : {}),
          markDirectoryUsed: lease.markUsed,
          trackPendingOperation: lease.trackPending,
        }
        const turnOutcome = await Promise.race([
          runTurn(request, deps, combined.signal, observer).then(
            (result) => ({ kind: 'result' as const, result }),
            (cause) => ({ kind: 'error' as const, cause }),
          ),
          activeTurn.forced.then(() => ({ kind: 'forced' as const })),
        ])
        if (turnOutcome.kind === 'forced') {
          const error = normalizeGenerationDrainTimeout(this.quarantineDrainTimeoutMs)
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        if (turnOutcome.kind === 'error') throw turnOutcome.cause
        const result = turnOutcome.result
        if (budget?.timedOut()) {
          this.triggerRebuild(server, {
            severity: 'error',
            code: 'resource-containment',
            message: 'OpenCode turn exceeded its per-work resource budget; quarantining the runtime generation',
          })
          const error = normalizeResourceContainment(budgetMs!)
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        if (activeTurn.forcedFailure) {
          const error = normalizeGenerationDrainTimeout(
            this.quarantineDrainTimeoutMs,
            result.ok ? [] : result.diagnostics,
          )
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        if (!result.ok && hasUnconfirmedCleanup(result.error.diagnostics)) {
          this.triggerRebuild(server, {
            severity: 'error',
            code: 'cleanup-unconfirmed',
            message:
              'OpenCode turn cleanup did not reach a confirmed terminal state; invalidating the runtime generation before reuse',
          })
        } else if (
          !result.ok &&
          result.error.diagnostics.some((diagnostic) => diagnostic.code === 'opencode-transport-failed')
        ) {
          this.triggerRebuild(server)
        }
        return result
      })
    } finally {
      combined.dispose()
      budget?.dispose()
      generation.activeTurns.delete(activeTurn)
      inFlight.end(sessionKey)
      this.releaseGeneration(generation)
    }
  }

  /**
   * Run a Follow-up prompt to completion on an existing Runtime Session.
   * The SignalR handler still acknowledges the command immediately;
   * this runtime owns completion and event projection.
   */
  async followup(
    request: RuntimeFollowupRequest,
    observer?: RuntimeTurnObserver,
  ): Promise<RuntimeResult<RuntimeFollowupResult>> {
    const diagnostics: RuntimeDiagnostic[] = []

    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }

    const validation = validateFollowupInput(request, diagnostics)
    if (validation.kind === 'failure') {
      return { ok: false, error: validation.error, diagnostics: [...diagnostics, ...validation.error.diagnostics] }
    }
    const { model, variant } = validation.value

    if (!request.target.runtimeSessionId) {
      const error = normalizeInvalidInput(
        'Follow-up requires a current OpenCode Runtime Session binding; pass the persisted runtimeSessionId from the AgentSession binding',
        [
          {
            severity: 'error',
            code: 'missing-binding',
            message: 'Reset the session to establish a fresh Runtime Session, then retry',
          },
        ],
      )
      return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
    }
    const result = await this.runTurn(
      {
        target: request.target,
        prompt: request.prompt,
        ...(request.fileParts ? { fileParts: request.fileParts } : {}),
        options: {
          ...(request.options ?? {}),
          model,
          variant,
        },
      },
      new AbortController().signal,
      observer,
    )
    if (!result.ok) return result

    const facts: RuntimeFollowupFacts = {
      runtimeSessionId: result.value.facts.runtimeSessionId,
      workDir: result.value.facts.workDir,
      finalAssistantText: result.value.facts.finalAssistantText,
    }
    return {
      ok: true,
      value: { facts, diagnostics: [...diagnostics, ...result.value.diagnostics] },
      diagnostics: [...diagnostics, ...result.diagnostics],
    }
  }

  /**
   * Cancel an active Runtime Session turn.
   *
   * Resolves the binding first, then uses the same abort-and-status
   * confirmation as turn cleanup. An accepted abort is not itself proof
   * that the turn stopped; that fact crosses the runtime boundary as
   * `stopConfirmed`.
   */
  async cancel(request: RuntimeCancelRequest): Promise<RuntimeResult<RuntimeCancelResult>> {
    const diagnostics: RuntimeDiagnostic[] = []

    if (!this.state.server || !this.state.ready) {
      const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }

    if (!request.target.runtimeSessionId) {
      const error = normalizeInvalidInput(
        'Cancel requires a current OpenCode Runtime Session binding; pass the persisted runtimeSessionId from the AgentSession binding',
        [
          {
            severity: 'error',
            code: 'missing-binding',
            message: 'Reset the session to establish a fresh Runtime Session, then retry',
          },
        ],
      )
      return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
    }
    const runtimeSessionId = request.target.runtimeSessionId

    return await this.withRuntimeOperation(() =>
      this.directoryInstances.withOperation(request.target.workDir, async (lease) => {
        const server = this.state.server
        if (!server || !this.state.ready) {
          const error = normalizeUnavailableRuntime(this.state.diagnostic ? [this.state.diagnostic] : [])
          return { ok: false, error, diagnostics: error.diagnostics }
        }
        lease.markUsed()
        const confirmation = await abortAndConfirmSession(server.client, runtimeSessionId, request.target.workDir)
        if (!confirmation.ok && confirmation.missingSession) {
          const error = normalizeMissingSession()
          return { ok: false, error, diagnostics: [...diagnostics, ...error.diagnostics] }
        }
        const confirmationDiagnostics = confirmation.ok
          ? []
          : [{ severity: 'error' as const, code: confirmation.code, message: confirmation.message }]
        const facts: RuntimeCancelFacts = {
          runtimeSessionId,
          workDir: request.target.workDir,
          cancelled: true,
          stopConfirmed: confirmation.ok,
        }
        return {
          ok: true,
          value: { facts, diagnostics: [...diagnostics, ...confirmationDiagnostics] },
          diagnostics: [...diagnostics, ...confirmationDiagnostics],
        }
      }),
    )
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
    const { events, server, generation } = this.state
    this.clearIdleTimer()
    this.directoryInstances.resetGeneration()
    this.state.events = null
    this.state.server = null
    this.state.generation = null
    this.state.ready = false
    this.state.idleSince = null
    if (generation) {
      generation.closed = true
      this.resolveGenerationDrain(generation)
    }
    if (options.clearDiagnostic ?? true) {
      this.state.diagnostic = null
    }
    await boundedWait(
      () =>
        Promise.allSettled([
          ...(server ? [server.terminateTree?.() ?? server.close()] : []),
          ...(events ? [events.close()] : []),
        ]),
      this.runtimeShutdownTimeoutMs,
    )
  }

  private readyState(): RuntimeReadyState {
    return { ready: this.state.ready, diagnostic: this.state.diagnostic, ownership: this.ownership() }
  }

  private async attemptStart(signal: AbortSignal): Promise<RuntimeResult<RuntimeReadyState>> {
    if (this.state.server !== null) {
      await this.shutdown().catch(() => {})
    }
    const diagnostics: RuntimeDiagnostic[] = []
    const serverFactory = this.deps.serverFactory
    if (!serverFactory) {
      const diagnostic: RuntimeDiagnostic = {
        severity: 'error',
        code: 'server-spawn-failed',
        message: 'OpenCode server factory was not provided',
      }
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }
    const eventSubscriptionFactory = this.deps.eventSubscriptionFactory
    if (!eventSubscriptionFactory) {
      const diagnostic: RuntimeDiagnostic = {
        severity: 'error',
        code: 'server-spawn-failed',
        message: 'OpenCode event subscription factory was not provided',
      }
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }
    let server: OpencodeServerHandle
    try {
      server = await serverFactory(this.deps.directory, signal, {
        shutdownTimeoutMs: this.runtimeShutdownTimeoutMs,
      })
    } catch (cause) {
      const diagnostic = toDiagnostic(cause, 'server-spawn-failed', 'Failed to start OpenCode server')
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
          severity: 'error',
          code: 'health-failed',
          message: 'OpenCode health check returned an empty body',
        }
        this.state.diagnostic = diagnostic
        diagnostics.push(diagnostic)
        await boundedWait(() => server.terminateTree?.() ?? server.close(), this.runtimeShutdownTimeoutMs)
        this.state.server = null
        const error = normalizeUnavailableRuntime(diagnostics)
        return { ok: false, error, diagnostics }
      }
    } catch (cause) {
      const diagnostic = toDiagnostic(cause, 'health-failed', 'OpenCode health check failed')
      this.state.diagnostic = diagnostic
      diagnostics.push(diagnostic)
      await boundedWait(() => server.terminateTree?.() ?? server.close(), this.runtimeShutdownTimeoutMs)
      this.state.server = null
      const error = normalizeUnavailableRuntime(diagnostics)
      return { ok: false, error, diagnostics }
    }

    const events = eventSubscriptionFactory(server.client)
    const generation = newRuntimeGeneration(this.nextGenerationId++, server, events)
    this.state.generation = generation
    this.state.events = events
    this.watchExit(events, server)

    this.state.ready = true
    this.state.diagnostic = null
    this.reconcileIdleLifecycle()
    return { ok: true, value: this.readyState(), diagnostics }
  }

  private watchExit(events: RuntimeEventSubscription, server: OpencodeServerHandle): void {
    const listener = (event: { type: string }) => {
      if (event.type === 'server.disconnected' || event.type === 'server.heartbeat-failed') {
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

  private triggerRebuild(server: OpencodeServerHandle, diagnostic?: RuntimeDiagnostic): void {
    if (this.state.rebuildTriggered) return
    if (!this.state.ready) return
    if (this.state.server !== server) return
    const generation = this.state.generation
    if (!generation || generation.server !== server || generation.quarantined) return
    this.state.rebuildTriggered = true
    generation.quarantined = true
    this.state.ready = false
    this.state.diagnostic = diagnostic ?? {
      severity: 'error',
      code: 'server-exit',
      message: 'OpenCode server exited; rebuilding runtime',
    }
    if (generation.activeTurns.size === 0) {
      this.resolveGenerationDrain(generation)
    }
    const rebuild = this.scheduleRebuild(generation)
    this.rebuildInFlight = rebuild
    void rebuild.then(
      () => {
        if (this.rebuildInFlight === rebuild) this.rebuildInFlight = null
      },
      () => {
        if (this.rebuildInFlight === rebuild) this.rebuildInFlight = null
      },
    )
  }

  private scheduleRebuild(generation: RuntimeGeneration): Promise<RuntimeResult<RuntimeReadyState>> {
    const delay = this.deps.rebuildDelayMs ?? 0
    return (async () => {
      const drained = await this.waitForGenerationDrain(generation)
      if (!drained) this.forceReleaseGeneration(generation)
      if (delay > 0) {
        await new Promise<void>((resolve) => {
          const timer = this.clock.setTimeout(resolve, delay)
          const unref = (timer as { unref?: () => void }).unref
          unref?.call(timer)
        })
      }
      if (this.state.generation === generation) {
        await this.shutdown({ clearDiagnostic: false }).catch(() => {})
      }
      this.state.rebuildTriggered = false
      this.rebuildInFlight = null
      return await this.start()
    })()
  }

  private acquireReadyGeneration(): RuntimeGeneration | null {
    const generation = this.state.generation
    if (
      !this.state.ready ||
      !generation ||
      generation.quarantined ||
      this.state.server !== generation.server ||
      this.state.events !== generation.events
    ) {
      return null
    }
    return generation
  }

  private releaseGeneration(generation: RuntimeGeneration): void {
    if (generation.activeTurns.size !== 0 || !generation.quarantined || generation.closed) return
    if (this.state.generation === generation) this.directoryInstances.resetGeneration()
    this.resolveGenerationDrain(generation)
  }

  private async waitForGenerationDrain(generation: RuntimeGeneration): Promise<boolean> {
    if (generation.drainResolved) return true
    let timer: unknown
    const timeout = new Promise<boolean>((resolve) => {
      timer = this.clock.setTimeout(() => resolve(false), this.quarantineDrainTimeoutMs)
    })
    try {
      return await Promise.race([generation.drained.then(() => true), timeout])
    } finally {
      if (timer !== undefined) this.clock.clearTimeout(timer)
    }
  }

  private forceReleaseGeneration(generation: RuntimeGeneration): void {
    if (generation.drainResolved) return
    generation.closed = true
    for (const activeTurn of generation.activeTurns) {
      activeTurn.forcedFailure = true
      activeTurn.resolveForced()
      try {
        activeTurn.abortController.abort(new Error('generation-drain-timeout'))
      } catch {
        /* best effort */
      }
    }
    this.resolveGenerationDrain(generation)
  }

  private resolveGenerationDrain(generation: RuntimeGeneration): void {
    if (generation.drainResolved) return
    generation.drainResolved = true
    generation.resolveDrained()
  }

  private async withRuntimeOperation<T>(operation: () => Promise<T>): Promise<T> {
    this.state.activeOperations += 1
    this.clearIdleTimer()
    try {
      return await operation()
    } finally {
      this.state.activeOperations -= 1
      this.reconcileIdleLifecycle()
    }
  }

  private reconcileIdleLifecycle(): void {
    if (!this.state.ready || !this.state.server || this.state.ownerIds.size > 0 || this.state.activeOperations > 0) {
      this.clearIdleTimer()
      if (this.state.ownerIds.size > 0) this.state.idleSince = null
      return
    }
    if (this.state.idleSince === null) this.state.idleSince = this.clock.now()
    if (this.state.idleTimer !== null) return
    const remaining = Math.max(0, this.idleGraceMs - (this.clock.now() - this.state.idleSince))
    this.state.idleTimer = this.clock.setTimeout(() => {
      this.state.idleTimer = null
      void this.reclaimIfIdle()
    }, remaining)
  }

  private clearIdleTimer(): void {
    if (this.state.idleTimer !== null) {
      this.clock.clearTimeout(this.state.idleTimer)
      this.state.idleTimer = null
    }
    if (this.state.ownerIds.size > 0 || this.state.activeOperations > 0) this.state.idleSince = null
  }

  private async reclaimIfIdle(): Promise<void> {
    if (
      !this.state.ready ||
      !this.state.server ||
      this.state.ownerIds.size > 0 ||
      this.state.activeOperations > 0 ||
      this.startInFlight ||
      this.rebuildInFlight
    ) {
      this.reconcileIdleLifecycle()
      return
    }
    await this.shutdown({ clearDiagnostic: false })
  }
}
