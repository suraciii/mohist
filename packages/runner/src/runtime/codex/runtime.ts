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
import {
  normalizeInvalidInputCodex,
  normalizeTurnFailedCodex,
  normalizeUnavailableRuntimeCodex,
  normalizeUnknownCodex,
} from './errors.js'
import { redactCodexCredentialString } from './credential.js'
import {
  createCodexModelCatalogLoader,
  type CodexCatalogManager,
  validateCodexTurnConfiguration,
} from './model-catalog.js'
import { type CodexReadinessProbe, evaluateCodexReadiness } from './readiness.js'
import { codexInitializationTransportFromHandle, performCodexInitialization } from './initialization.js'
import { resumeThread, startThread, type CodexThreadTransport } from './thread.js'
import { driveTurnToCompletion, submitTurnStart, type CodexTurnEventObserver, type CodexTurnTransport } from './turn.js'
import { normalizeCodexNotification } from './turn-events.js'
import { isCodexTurnCompletedEvent } from './protocol-types.js'
import { assignCodexRequestId, nextCodexRequestId } from './server-process.js'

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
 * The full implementations of the turn and Session command entry
 * points live in their respective modules; this file wires the
 * boundary types, readiness, catalog seam, and command entry points.
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
  private readonly knownThreads = new Set<string>()
  private readonly activeTurns = new Map<string, string>()
  private nextRequestId = 1

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
    request: CodexTurnRequest,
    signal: AbortSignal = new AbortController().signal,
    observer?: CodexTurnEventObserver,
  ): Promise<CodexResult<CodexTurnResult>> {
    if (!this.state.ready || !this.server.handle || this.server.closed) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (!request.clientUserMessageId || request.clientUserMessageId.trim().length === 0) {
      const error = normalizeInvalidInputCodex('Codex turn requires the Mohist SessionInput ID as clientUserMessageId')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (signal.aborted) {
      const error = normalizeTurnFailedCodex('Codex turn was aborted before submission')
      return { ok: false, error, diagnostics: error.diagnostics }
    }

    const configuration = this.validateTurnConfiguration({
      model: request.options?.model ?? null,
      reasoningEffort: request.options?.reasoningEffort ?? null,
      variant: request.options?.variant ?? null,
      unknownKeys: request.options?.unknownKeys,
    })
    if (!configuration.ok) return configuration

    const handle = this.server.handle
    const threadTransport: CodexThreadTransport = handle
    const turnTransport: CodexTurnTransport = handle
    let threadId = request.target.runtimeSessionId
    let workDir = request.target.workDir

    if (threadId === null) {
      const created = await startThread(
        threadTransport,
        {
          workDir,
          model: configuration.value.model,
          reasoningEffort: configuration.value.reasoningEffort,
        },
        this.takeRequestId(),
      )
      if (!created.ok) return created as CodexResult<CodexTurnResult>
      threadId = created.value.threadId
      workDir = created.value.workDir
      this.knownThreads.add(threadId)
      try {
        await observer?.onSessionReady?.({ runtimeSessionId: threadId, workDir })
      } catch (cause) {
        const error = normalizeTurnFailedCodex(
          `Codex Session binding could not be persisted before turn/start: ${cause instanceof Error ? cause.message : String(cause)}`,
        )
        return { ok: false, error, diagnostics: error.diagnostics }
      }
    } else {
      if (!this.knownThreads.has(threadId)) {
        const resumed = await resumeThread(threadTransport, threadId, workDir, this.takeRequestId())
        if (!resumed.ok) return resumed as CodexResult<CodexTurnResult>
        workDir = resumed.value.workDir
        this.knownThreads.add(threadId)
      }
      try {
        await observer?.onSessionReady?.({ runtimeSessionId: threadId, workDir })
      } catch (cause) {
        const error = normalizeTurnFailedCodex(
          `Codex Session binding could not be confirmed before turn/start: ${cause instanceof Error ? cause.message : String(cause)}`,
        )
        return { ok: false, error, diagnostics: error.diagnostics }
      }
    }

    const submission = await submitTurnStart(
      turnTransport,
      {
        threadId,
        workDir,
        prompt: request.prompt,
        fileParts: request.fileParts ?? null,
        clientUserMessageId: request.clientUserMessageId,
        resolved: configuration.value,
      },
      this.takeRequestId(),
    )
    if (!submission.ok) return submission as CodexResult<CodexTurnResult>

    this.activeTurns.set(threadId, submission.value.turnId)
    try {
      const completion = await driveTurnToCompletion({
        transport: turnTransport,
        runtimeSessionId: threadId,
        workDir,
        threadId,
        turnId: submission.value.turnId,
        deadlineMs: request.deadlineMs ?? null,
        signal,
        observer,
        nextRequestId: () => this.takeRequestId(),
      })
      if (signal.aborted && completion.ok) {
        const error = normalizeTurnFailedCodex('Codex turn completed after its owning execution was aborted')
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      return completion
    } finally {
      if (this.activeTurns.get(threadId) === submission.value.turnId) this.activeTurns.delete(threadId)
    }
  }

  /**
   * Follow-up on an existing Codex Thread. A steerable active Turn
   * uses `turn/steer` with the frozen `expectedTurnId`; otherwise the
   * accepted input queues a new `turn/start` on the same Thread. The
   * caller input id is required and is carried only for correlation.
   */
  async followup(
    request: CodexFollowupRequest,
    observer?: CodexTurnEventObserver,
    signal?: AbortSignal,
  ): Promise<CodexResult<CodexFollowupResult>> {
    if (!this.state.ready || !this.server.handle) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (!request.prompt || request.prompt.trim().length === 0) {
      const error = normalizeInvalidInputCodex('Codex follow-up prompt must be non-empty')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (!request.target.runtimeSessionId) {
      const error = normalizeInvalidInputCodex('Codex follow-up requires a bound Session')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (!request.clientUserMessageId) {
      const error = normalizeInvalidInputCodex('Codex follow-up requires the Mohist SessionInput ID')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (signal?.aborted) {
      const error = normalizeTurnFailedCodex('Codex follow-up was interrupted before admission')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const configuration = this.validateTurnConfiguration({
      model: request.options?.model ?? null,
      reasoningEffort: request.options?.reasoningEffort ?? null,
      variant: request.options?.variant ?? null,
    })
    if (!configuration.ok) return configuration as CodexResult<CodexFollowupResult>

    const threadId = request.target.runtimeSessionId
    const activeTurnId = this.activeTurns.get(threadId)
    if (activeTurnId?.startsWith('__compaction_pending_')) {
      const error = normalizeTurnFailedCodex(
        'Codex compact is still running; follow-up must wait for the compaction Turn to finish',
      )
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (activeTurnId) {
      try {
        const response = await this.server.handle.send({
          id: this.takeRequestId(),
          method: 'turn/steer',
          params: {
            threadId,
            input: [
              { type: 'text', text: request.prompt, text_elements: [] },
              ...(request.fileParts ?? []).map((part) => ({
                type: 'image' as const,
                mime: part.mime,
                url: part.url,
                filename: part.filename,
              })),
            ],
            expectedTurnId: activeTurnId,
            clientUserMessageId: request.clientUserMessageId,
          },
        })
        if (!response) throw new Error('Codex turn/steer returned an empty response')
        return {
          ok: true,
          value: {
            facts: { runtimeSessionId: threadId, workDir: request.target.workDir, finalAssistantText: null },
            diagnostics: [],
          },
          diagnostics: [],
        }
      } catch (cause) {
        const error = normalizeUnknownCodex(
          `Codex turn/steer response was not observed; the follow-up outcome is unknown: ${cause instanceof Error ? cause.message : String(cause)}`,
        )
        return { ok: false, error, diagnostics: error.diagnostics }
      }
    }

    const result = await this.runTurn(
      {
        target: request.target,
        prompt: request.prompt,
        clientUserMessageId: request.clientUserMessageId,
        fileParts: request.fileParts ?? null,
        options: request.options ?? null,
      },
      signal,
      observer,
    )
    if (!result.ok) return result as CodexResult<CodexFollowupResult>
    return {
      ok: true,
      value: {
        facts: { ...result.value.facts, finalAssistantText: result.value.facts.finalAssistantText },
        diagnostics: result.value.diagnostics,
      },
      diagnostics: result.diagnostics,
    }
  }

  /**
   * Cancel the exact active Codex Turn. The `turn/interrupt` RPC
   * response only means the request was accepted; `stopConfirmed` is
   * true only after the matching `turn/completed` for the frozen
   * Thread + Turn is observed within the bounded confirmation budget.
   * An unconfirmed interrupt stays `stopConfirmed: false` so callers
   * never report a still-running turn as safely stopped.
   */
  async cancel(request: CodexCancelRequest): Promise<CodexResult<CodexCancelResult>> {
    if (!this.state.ready || !this.server.handle) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const threadId = request.target.runtimeSessionId
    if (!threadId) {
      const error = normalizeInvalidInputCodex('Codex cancel requires a bound Session')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const turnId = this.activeTurns.get(threadId)
    if (!turnId || turnId.startsWith('__compaction_pending_')) {
      const diagnostics: CodexDiagnostic[] = [
        { severity: 'info', code: 'cancel-idle', message: 'Codex cancel found no active Turn to interrupt' },
      ]
      return {
        ok: true,
        value: {
          facts: { runtimeSessionId: threadId, workDir: request.target.workDir, cancelled: true, stopConfirmed: false },
          diagnostics,
        },
        diagnostics,
      }
    }
    const confirmation = this.awaitTurnTerminal(threadId, turnId)
    let diagnostics: CodexDiagnostic[] = []
    try {
      await this.server.handle.send({
        id: this.takeRequestId(),
        method: 'turn/interrupt',
        params: { threadId, turnId },
      })
    } catch (cause) {
      diagnostics = [
        {
          severity: 'warning',
          code: 'interrupt-unconfirmed',
          message: redactCodexCredentialString(cause instanceof Error ? cause.message : String(cause)),
        },
      ]
    }
    let stopConfirmed: boolean
    try {
      // The RPC accepted the interrupt, but acceptance is not the
      // confirmation. Wait for the exact matching terminal event or
      // the bounded budget before reporting the outcome.
      stopConfirmed = await confirmation.confirmed
    } finally {
      confirmation.dispose()
    }
    return {
      ok: true,
      value: {
        facts: { runtimeSessionId: threadId, workDir: request.target.workDir, cancelled: true, stopConfirmed },
        diagnostics,
      },
      diagnostics,
    }
  }

  /**
   * Idle-only context compaction via `thread/compact/start`. Compact
   * is admitted only while the bound Thread has no active Turn; success
   * requires the matching compaction Turn to reach `turn/completed`
   * and emit a `contextCompaction` item. The deprecated
   * `thread/compacted` notification is not completion authority.
   */
  async compact(
    request: CodexCompactRequest,
    observer?: CodexTurnEventObserver,
  ): Promise<CodexResult<CodexCompactResult>> {
    if (!this.state.ready || !this.server.handle) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const threadId = request.target.runtimeSessionId
    if (!threadId) {
      const error = normalizeInvalidInputCodex('Codex compact requires a bound Session')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (this.activeTurns.has(threadId)) {
      const error = normalizeTurnFailedCodex('Codex compact is only available while the Thread is idle')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    let response: unknown
    try {
      response = await this.server.handle.send({
        id: this.takeRequestId(),
        method: 'thread/compact/start',
        params: { threadId },
      })
    } catch (cause) {
      const error = normalizeUnknownCodex(
        `Codex compact start response was not observed; the compaction outcome is unknown: ${cause instanceof Error ? cause.message : String(cause)}`,
      )
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const compact = readCompactStartResult(response, threadId)
    if (!compact || compact.threadId !== threadId) {
      const error = normalizeUnknownCodex(
        'Codex compact start outcome is unknown because its response shape could not be verified',
      )
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    let sawCompaction = false
    const compactObserver: CodexTurnEventObserver = {
      onSessionReady: observer?.onSessionReady,
      onDiagnostic: observer?.onDiagnostic,
      onEvent: (event) => {
        sawCompaction ||= event.type === 'compaction'
        observer?.onEvent?.(event)
      },
    }
    const activeTurnKey = compact.turnId ?? `__compaction_pending_${this.takeRequestId()}`
    this.activeTurns.set(threadId, activeTurnKey)
    try {
      const completion = await driveTurnToCompletion({
        transport: this.server.handle,
        runtimeSessionId: threadId,
        workDir: request.target.workDir,
        threadId,
        turnId: compact.turnId,
        deadlineMs: null,
        observer: compactObserver,
        nextRequestId: () => this.takeRequestId(),
        onTurnIdDiscovered: (turnId) => this.activeTurns.set(threadId, turnId),
      })
      if (!completion.ok) return completion as CodexResult<CodexCompactResult>
      if (!sawCompaction) {
        const error = normalizeTurnFailedCodex('Codex compact completed without a contextCompaction item')
        return { ok: false, error, diagnostics: error.diagnostics }
      }
      return {
        ok: true,
        value: {
          facts: { runtimeSessionId: threadId, workDir: request.target.workDir },
          diagnostics: completion.value.diagnostics,
        },
        diagnostics: completion.diagnostics,
      }
    } finally {
      if (
        this.activeTurns.get(threadId) === activeTurnKey ||
        compact.turnId === null ||
        this.activeTurns.get(threadId) === compact.turnId
      ) {
        this.activeTurns.delete(threadId)
      }
    }
  }

  /**
   * Reset creates an empty Thread in the same working directory and
   * returns its id so the caller can compare-and-swap the full
   * binding while preserving AgentSession identity. The previous
   * transcript is never replayed into the new Thread.
   */
  async reset(request: CodexResetRequest): Promise<CodexResult<CodexResetResult>> {
    if (!this.state.ready || !this.server.handle) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    if (request.target.runtimeSessionId && this.activeTurns.has(request.target.runtimeSessionId)) {
      const error = normalizeTurnFailedCodex('Codex reset is only available while the Thread is idle')
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const created = await startThread(
      this.server.handle,
      { workDir: request.target.workDir, model: null, reasoningEffort: null },
      this.takeRequestId(),
    )
    if (!created.ok) return created as CodexResult<CodexResetResult>
    this.knownThreads.add(created.value.threadId)
    return {
      ok: true,
      value: {
        facts: { runtimeSessionId: created.value.threadId, workDir: created.value.workDir },
        diagnostics: created.value.diagnostics,
      },
      diagnostics: created.diagnostics,
    }
  }

  async resolveSession(request: {
    readonly target: { readonly runtimeSessionId: string; readonly workDir: string }
  }): Promise<
    CodexResult<{ readonly runtimeSessionId: string; readonly workDir: string; readonly activeTurn: boolean }>
  > {
    if (!this.state.ready || !this.server.handle) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const resumed = await resumeThread(
      this.server.handle,
      request.target.runtimeSessionId,
      request.target.workDir,
      this.takeRequestId(),
    )
    if (!resumed.ok)
      return resumed as CodexResult<{
        readonly runtimeSessionId: string
        readonly workDir: string
        readonly activeTurn: boolean
      }>
    this.knownThreads.add(resumed.value.threadId)
    return {
      ok: true,
      value: {
        runtimeSessionId: resumed.value.threadId,
        workDir: resumed.value.workDir,
        activeTurn: this.activeTurns.has(resumed.value.threadId),
      },
      diagnostics: resumed.diagnostics,
    }
  }

  async createSession(request: {
    readonly target: { readonly runtimeSessionId: null; readonly workDir: string }
    readonly model?: string | null
    readonly reasoningEffort?: string | null
  }): Promise<CodexResult<{ readonly runtimeSessionId: string; readonly workDir: string }>> {
    if (!this.state.ready || !this.server.handle) {
      const error = normalizeUnavailableRuntimeCodex(this.state.diagnostic ? [this.state.diagnostic] : [])
      return { ok: false, error, diagnostics: error.diagnostics }
    }
    const created = await startThread(
      this.server.handle,
      {
        workDir: request.target.workDir,
        model: request.model ?? null,
        reasoningEffort: (request.reasoningEffort ?? null) as never,
      },
      this.takeRequestId(),
    )
    if (!created.ok) return created as CodexResult<{ readonly runtimeSessionId: string; readonly workDir: string }>
    this.knownThreads.add(created.value.threadId)
    return {
      ok: true,
      value: { runtimeSessionId: created.value.threadId, workDir: created.value.workDir },
      diagnostics: created.diagnostics,
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
    this.knownThreads.clear()
    this.activeTurns.clear()
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
    this.knownThreads.clear()
    this.activeTurns.clear()
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
      requestId: this.takeRequestId(),
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
    const catalogLoader = codexCatalogLoaderFromHandle(handle, () => this.takeRequestId())
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
    this.knownThreads.clear()
    this.activeTurns.clear()
    this.state.diagnostic = {
      severity: 'error',
      code: 'protocol-failure',
      message: redactCodexCredentialString(`Codex app-server ${reason}: ${detail}`),
    }
  }

  /**
   * Bounded confirmation of a `turn/interrupt`. Resolves `true` when
   * the matching `turn/completed` for the exact frozen Thread + Turn
   * is observed, `false` when the bounded confirmation budget elapses
   * first. The caller owns disposal so a subscriber cannot leak.
   */
  private awaitTurnTerminal(
    threadId: string,
    turnId: string,
  ): { readonly confirmed: Promise<boolean>; dispose(): void } {
    const handle = this.server.handle
    if (!handle) return { confirmed: Promise.resolve(false), dispose: () => undefined }
    const clock = this.clock
    let settled = false
    let resolveFn: (value: boolean) => void = () => undefined
    const confirmed = new Promise<boolean>((resolve) => {
      resolveFn = resolve
    })
    const settle = (value: boolean): void => {
      if (settled) return
      settled = true
      resolveFn(value)
    }
    const unsubscribe = handle.subscribe((message) => {
      const normalized = normalizeCodexNotification(message)
      const event = normalized ?? message
      if (isCodexTurnCompletedEvent(event) && event.threadId === threadId && event.turnId === turnId) {
        settle(true)
      }
    })
    const timer = clock.setTimeout(() => settle(false), CODEX_DEFAULT_TIMEOUTS.cancelConfirmationMs)
    return {
      confirmed,
      dispose() {
        clock.clearTimeout(timer)
        unsubscribe()
      },
    }
  }

  private takeRequestId(): number {
    const id = this.nextRequestId > 0 ? this.nextRequestId : nextCodexRequestId()
    this.nextRequestId = assignCodexRequestId(id)
    return id
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

function readCompactStartResult(
  response: unknown,
  expectedThreadId: string,
): { readonly threadId: string; readonly turnId: string | null } | null {
  const candidate = response && typeof response === 'object' ? (response as { result?: unknown }) : null
  const result = candidate && 'result' in candidate ? candidate.result : response
  if (!result || typeof result !== 'object') return null
  const view = result as { threadId?: unknown; turnId?: unknown; turn?: unknown }
  const nestedTurn = view.turn && typeof view.turn === 'object' ? (view.turn as { id?: unknown }) : null
  const threadId = typeof view.threadId === 'string' ? view.threadId : expectedThreadId
  const turnId =
    typeof view.turnId === 'string' ? view.turnId : typeof nestedTurn?.id === 'string' ? nestedTurn.id : null
  return { threadId, turnId }
}

/** Build the retained-snapshot catalog manager over the live app-server handle. */
function codexCatalogLoaderFromHandle(handle: CodexServerHandle, nextRequestId: () => number): CodexCatalogManager {
  return createCodexModelCatalogLoader(
    {
      send: (request) => handle.send(request),
    },
    { nextRequestId },
  )
}
