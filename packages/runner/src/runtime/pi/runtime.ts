import { createCredentialMaskerFromEnvironment, CredentialMasker } from '../task-log.js'
import { resolve } from 'node:path'
import { diagnostic, piError, resetDiagnostic } from './errors.js'
import { isProviderFailure, DEFAULT_PI_PROVIDER_ERROR_POLICY } from './policy.js'
import { createPiProjector } from './projector.js'
import { realPiSdkFactory, type PiSdkFactory, type PiSdkServices, type PiSdkSession } from './sdk.js'
import type {
  PiCancelFacts,
  PiCancelRequest,
  PiCancelResult,
  PiCompactFacts,
  PiCompactRequest,
  PiCompactResult,
  PiDiagnostic,
  PiFollowupRequest,
  PiFollowupResult,
  PiInspectTurnFacts,
  PiInspectTurnRequest,
  PiInspectTurnResult,
  PiProviderErrorPolicy,
  PiReadyState,
  PiCatalog,
  PiResetFacts,
  PiResetRequest,
  PiResetResult,
  PiResult,
  PiRuntimeEvent,
  PiSessionCreateRequest,
  PiSessionResolveRequest,
  PiSessionResolveResult,
  PiSessionResult,
  PiTurnObserver,
  PiTurnRequest,
  PiTurnResult,
} from './types.js'

export interface PiClock {
  readonly now: () => number
  readonly setTimeout: (callback: () => void, delayMs: number) => unknown
  readonly clearTimeout: (handle: unknown) => void
}

export interface PiRuntimeDeps {
  readonly agentDir: string
  readonly sdkFactory?: PiSdkFactory
  readonly providerErrorPolicy?: PiProviderErrorPolicy
  readonly clock?: PiClock
  readonly masker?: CredentialMasker
}

const defaultClock: PiClock = {
  now: () => Date.now(),
  setTimeout: (callback, delay) => setTimeout(callback, delay),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}
const CANCEL_CONFIRMATION_TIMEOUT_MS = 5_000

export class PiRuntime {
  private readonly deps: PiRuntimeDeps
  private readonly sessions = new Map<string, PiSdkSession>()
  private readonly sessionMutexes = new Map<string, Promise<unknown>>()
  private readonly state: {
    ready: boolean
    diagnostic: PiDiagnostic | null
    catalog: PiCatalog | null
    services: PiSdkServices | null
  } = { ready: false, diagnostic: null, catalog: null, services: null }
  private startInFlight: Promise<PiResult<PiReadyState>> | null = null

  constructor(deps: PiRuntimeDeps) {
    this.deps = deps
  }

  private withSessionLock<T>(path: string, operation: () => Promise<T>): Promise<T> {
    const previous = this.sessionMutexes.get(path)
    if (previous) {
      const settled = previous.catch(() => undefined)
      const current = settled.then(operation)
      this.sessionMutexes.set(
        path,
        current.catch(() => undefined),
      )
      return current
    }
    const current = operation()
    this.sessionMutexes.set(
      path,
      current.catch(() => undefined),
    )
    return current
  }

  async start(): Promise<PiResult<PiReadyState>> {
    if (this.state.ready) return { ok: true, value: this.readyState(), diagnostics: [] }
    if (this.startInFlight) return this.startInFlight
    this.startInFlight = this.attemptStart()
    try {
      return await this.startInFlight
    } finally {
      this.startInFlight = null
    }
  }

  ready(): boolean {
    return this.state.ready
  }
  diagnostic(): PiDiagnostic | null {
    return this.state.diagnostic
  }
  catalog(): PiCatalog | null {
    return this.state.catalog
  }

  async resolveSession(request: PiSessionResolveRequest): Promise<PiResult<PiSessionResolveResult>> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId) return this.failure('missing-session', 'Pi Session binding is missing')
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure('incompatible-runtime', 'Pi runtimeSessionId must be an absolute session-file path')
    try {
      const session = this.sessions.get(path) ?? (await this.state.services.openSession(path, request.target.workDir))
      this.sessions.set(path, session)
      return {
        ok: true,
        value: { runtimeSessionId: path, workDir: request.target.workDir, activeTurn: session.isStreaming },
        diagnostics: [],
      }
    } catch (cause) {
      return isMissingSessionFile(cause)
        ? this.failure('missing-session', 'The bound Pi Session is missing', [
            diagnostic('session-open-failed', this.mask(message(cause))),
          ])
        : this.failure('turn-failed', 'The bound Pi Session could not be opened', [
            diagnostic('session-open-failed', this.mask(message(cause))),
          ])
    }
  }

  async createSession(request: PiSessionCreateRequest): Promise<PiResult<PiSessionResult>> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    try {
      const session = await this.state.services.createSession(request.target.workDir)
      const path = normalizedPath(session.sessionFile)
      if (!path) return this.failure('incompatible-runtime', 'Pi did not return an absolute session-file path')
      this.sessions.set(path, session)
      return { ok: true, value: { runtimeSessionId: path, workDir: request.target.workDir }, diagnostics: [] }
    } catch (cause) {
      return this.failure('turn-failed', 'Pi Session creation failed', [
        diagnostic('session-create-failed', this.mask(message(cause))),
      ])
    }
  }

  async runTurn(
    request: PiTurnRequest,
    signal: AbortSignal,
    observer?: PiTurnObserver,
  ): Promise<PiResult<PiTurnResult>> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    if (!request.prompt || request.prompt.trim().length === 0)
      return this.failure('invalid-input', 'Pi prompt must be non-empty')
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId)
      return this.failure('missing-session', 'Pi turn requires a bound Session', [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure('incompatible-runtime', 'Pi runtimeSessionId must be an absolute session-file path')
    let session: PiSdkSession
    try {
      session = this.sessions.get(path) ?? (await this.state.services.openSession(path, request.target.workDir))
      this.sessions.set(path, session)
    } catch (cause) {
      return this.failure('missing-session', 'The bound Pi Session is missing or corrupt', [
        resetDiagnostic(),
        diagnostic('session-open-failed', this.mask(message(cause))),
      ])
    }

    const diagnostics: PiDiagnostic[] = []
    if (request.options?.unknownKeys && request.options.unknownKeys.length > 0) {
      diagnostics.push(
        diagnostic(
          'options-unknown-keys',
          `Ignored unknown option keys: ${request.options.unknownKeys.join(', ')}`,
          'info',
        ),
      )
    }
    const projector = createPiProjector(
      path,
      request.target.workDir,
      this.deps.masker ?? createCredentialMaskerFromEnvironment(),
    )
    const report = (events: readonly PiRuntimeEvent[]) => events.forEach((event) => observer?.onEvent?.(event))
    let fixed: PiResult<PiTurnResult> | null = null
    let resolveFixed!: () => void
    const fixedSignal = new Promise<void>((resolve) => {
      resolveFixed = resolve
    })
    const fixAndAbort = (result: PiResult<PiTurnResult>) => {
      if (fixed) return
      fixed = result
      void abortAndDiagnose(session, diagnostics, this.mask.bind(this)).finally(resolveFixed)
    }
    const unsubscribe = session.subscribe((event) => {
      const facts = projector.project(event)
      report(facts)
      if (isRetryFailure(event, this.policy()) && !fixed) {
        fixAndAbort(this.finishFailure('turn-failed', 'Pi provider retries exhausted', diagnostics))
      }
    })
    const model = request.options?.model
    if (model) {
      const parsed = splitModel(model)
      if (!parsed) {
        unsubscribe()
        return this.failure('invalid-input', 'options.model must use provider/model syntax')
      }
      try {
        await session.setModel(this.state.services.model(parsed.provider, parsed.id))
      } catch (cause) {
        unsubscribe()
        return this.failure('turn-failed', 'Pi rejected the selected model', [
          diagnostic('model-rejected', this.mask(message(cause))),
        ])
      }
    }
    if (request.options?.reasoningEffort) session.setThinkingLevel(request.options.reasoningEffort)
    if (request.options?.variant) session.setThinkingLevel(request.options.variant)
    const clock = this.deps.clock ?? defaultClock
    const duration = request.durationMs ?? null
    const deadline =
      duration !== null && duration >= 0
        ? clock.setTimeout(() => {
            fixAndAbort(this.finishFailure('deadline-exceeded', 'Pi turn deadline exceeded', diagnostics))
          }, duration)
        : null
    const warningDelay = duration !== null && duration >= 0 ? Math.max(0, duration - 5 * 60_000) : null
    const warning =
      warningDelay === null
        ? null
        : clock.setTimeout(() => {
            if (!fixed)
              void session.steer(
                'This turn is nearing its execution deadline; wrap up the current work and return the final answer.',
              )
          }, warningDelay)
    const cancel = () => {
      if (!fixed) fixAndAbort(this.finishFailure('interrupted', 'Pi turn was interrupted', diagnostics))
    }
    signal.addEventListener('abort', cancel, { once: true })
    try {
      const promptOperation = this.withSessionLock(path, () =>
        session.prompt(request.prompt, { expandPromptTemplates: false }).then(() => 'completed' as const),
      )
      const promptOutcome = await Promise.race([promptOperation, fixedSignal.then(() => 'fixed' as const)])
      if (fixed) return fixed
      if (lastMessageFailed(session.messages))
        return this.failure('turn-failed', 'Pi turn failed', [
          diagnostic('turn-failed', this.mask(lastMessageError(session.messages) ?? 'Pi reported an error')),
        ])
      report(projector.reconcile(session.messages))
      diagnostics.push(...projector.diagnostics().map((item) => diagnostic(item.code, this.mask(item.message), 'info')))
      return {
        ok: true,
        value: {
          facts: {
            finalAssistantText: finalText(session.messages),
            runtimeSessionId: path,
            workDir: request.target.workDir,
          },
          diagnostics,
        },
        diagnostics,
      }
    } catch (cause) {
      if (fixed) return fixed
      return this.failure('turn-failed', 'Pi turn failed', [diagnostic('turn-failed', this.mask(message(cause)))])
    } finally {
      signal.removeEventListener('abort', cancel)
      if (deadline !== null) clock.clearTimeout(deadline)
      if (warning !== null) clock.clearTimeout(warning)
      unsubscribe()
    }
  }

  /**
   * Reads the recorded turn state of the bound Pi Session without
   * starting one. Recovery reconciliation adopts a terminal turn
   * (`failed`/`finalAssistantText`) as the authoritative outcome; an
   * active turn or a missing session proves the execution context is
   * not adoptable by this process. Never mutates the session.
   */
  async inspectTurn(request: PiInspectTurnRequest): Promise<PiInspectTurnResult> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId)
      return this.failure('missing-session', 'Pi turn inspection requires a bound Session', [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure('incompatible-runtime', 'Pi runtimeSessionId must be an absolute session-file path')
    try {
      const session = this.sessions.get(path) ?? (await this.state.services.openSession(path, request.target.workDir))
      this.sessions.set(path, session)
      const facts: PiInspectTurnFacts = {
        runtimeSessionId: path,
        workDir: request.target.workDir,
        activeTurn: session.isStreaming,
        finalAssistantText: finalText(session.messages),
        failed: lastMessageFailed(session.messages),
        errorMessage: lastMessageError(session.messages) ?? null,
      }
      return { ok: true, value: facts, diagnostics: [] }
    } catch (cause) {
      return isMissingSessionFile(cause)
        ? this.failure('missing-session', 'The bound Pi Session is missing', [
            diagnostic('session-open-failed', this.mask(message(cause))),
          ])
        : this.failure('turn-failed', 'The bound Pi Session could not be opened', [
            diagnostic('session-open-failed', this.mask(message(cause))),
          ])
    }
  }

  /**
   * Follow-up against a bound Pi Session.
   *
   * Branches on the physical Pi session's `isStreaming`:
   *  - Busy → `await session.steer(text)`. The running turn's
   *    projection is owned by the active `runTurn` subscription; this
   *    method does not start a new turn. Resolves accepted. `steer`
   *    does not acquire the per-session prompt mutex (it injects into
   *    a running turn).
   *  - Idle → acquire the per-session prompt mutex (D10), set up a
   *    `createPiProjector` subscription, and call
   *    `session.prompt(text, { expandPromptTemplates: false, preflight })`.
   *    The Follow-up resolves as accepted when `preflight(true)` fires
   *    (Pi confirmed reception), and as a failure when `preflight(false)`
   *    fires (Pi rejected reception — missing model or credentials) or
   *    when `prompt()` throws. A background continuation holds the
   *    mutex and the subscription until `prompt()` resolves, then
   *    tears both down. No automatic retry — a preflight-rejected
   *    Follow-up stays rejected.
   *
   * Both branches keep the physical Pi Session binding unchanged
   * (`runtimeSessionId` is returned as the persisted path). A missing
   * bound session file surfaces as `missing-session` with a Reset hint
   * (no silent new session).
   */
  async followup(request: PiFollowupRequest, observer?: PiTurnObserver): Promise<PiFollowupResult> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    if (!request.prompt || request.prompt.trim().length === 0)
      return this.failure('invalid-input', 'Pi follow-up prompt must be non-empty')
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId)
      return this.failure('missing-session', 'Pi follow-up requires a bound Session', [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure('incompatible-runtime', 'Pi runtimeSessionId must be an absolute session-file path')
    const session = await this.resolveFollowupSession(path, request.target.workDir)
    if (!session.ok) return session.failure
    const configured = await this.applyFollowupOptions(session.value, request.options)
    if (configured) return configured

    if (session.value.isStreaming) {
      try {
        await session.value.steer(request.prompt)
        return {
          ok: true,
          value: { runtimeSessionId: path, workDir: request.target.workDir },
          diagnostics: [],
        }
      } catch (cause) {
        return this.failure('turn-failed', 'Pi steer failed', [diagnostic('steer-failed', this.mask(message(cause)))])
      }
    }

    const projector = createPiProjector(
      path,
      request.target.workDir,
      this.deps.masker ?? createCredentialMaskerFromEnvironment(),
    )
    const report = (events: readonly PiRuntimeEvent[]) => events.forEach((event) => observer?.onEvent?.(event))
    return new Promise<PiFollowupResult>((resolve) => {
      let settled = false
      const settle = (result: PiFollowupResult) => {
        if (settled) return
        settled = true
        resolve(result)
      }
      void this.withSessionLock(path, async () => {
        const unsubscribe = session.value.subscribe((event) => report(projector.project(event)))
        try {
          await session.value.prompt(request.prompt, {
            expandPromptTemplates: false,
            preflight: (success) => {
              if (success) {
                settle({
                  ok: true,
                  value: { runtimeSessionId: path, workDir: request.target.workDir },
                  diagnostics: [],
                })
              } else {
                settle(
                  this.failure('turn-failed', 'Pi rejected follow-up reception (preflight rejected the prompt)', [
                    diagnostic(
                      'preflight-rejected',
                      'Pi preflight rejected the follow-up prompt — model or credentials missing',
                    ),
                  ]),
                )
              }
            },
          })
          report(projector.reconcile(session.value.messages))
        } catch (cause) {
          settle(
            this.failure('turn-failed', 'Pi follow-up prompt failed', [
              diagnostic('prompt-failed', this.mask(message(cause))),
            ]),
          )
        } finally {
          unsubscribe()
        }
      })
    })
  }

  private async applyFollowupOptions(
    session: PiSdkSession,
    options: PiFollowupRequest['options'],
  ): Promise<PiFollowupResult | null> {
    const model = options?.model
    if (model) {
      const parsed = splitModel(model)
      if (!parsed) return this.failure('invalid-input', 'options.model must use provider/model syntax')
      try {
        await session.setModel(this.state.services!.model(parsed.provider, parsed.id))
      } catch (cause) {
        return this.failure('turn-failed', 'Pi rejected the selected model', [
          diagnostic('model-rejected', this.mask(message(cause))),
        ])
      }
    }
    if (options?.reasoningEffort) session.setThinkingLevel(options.reasoningEffort)
    if (options?.variant) session.setThinkingLevel(options.variant)
    return null
  }

  /**
   * Cancel against an active Pi Session turn.
   *
   * Resolves/opens the session (missing file → `missing-session` with a
   * Reset hint). Reuses the existing `abortAndDiagnose` pattern:
   * `await session.abort()` then read `session.isStreaming` to confirm
   * stop. The result facts carry an explicit `stopConfirmed` flag
   * (`true` when `isStreaming` cleared after abort; `false` otherwise).
   *
   * Both stop-confirmed and stop-unconfirmed return `cancelled: true`
   * (the abort was attempted); `stopConfirmed: false` is a first-class
   * field so the upper layers can surface `interruptUnconfirmed` to the
   * API/user instead of reporting a still-running turn as safely
   * stopped. `cancel` does not acquire the per-session prompt mutex —
   * it must be able to interrupt an in-flight prompt.
   */
  async cancel(request: PiCancelRequest): Promise<PiCancelResult> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId)
      return this.failure('missing-session', 'Pi cancel requires a bound Session', [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure('incompatible-runtime', 'Pi runtimeSessionId must be an absolute session-file path')
    const session = await this.resolveFollowupSession(path, request.target.workDir)
    if (!session.ok) return session.failure

    const diagnostics: PiDiagnostic[] = []
    const wasStreaming = session.value.isStreaming
    let stopConfirmed = !wasStreaming
    const confirmation = wasStreaming ? watchPiStop(session.value, this.deps.clock ?? defaultClock) : null
    try {
      await session.value.abort()
      if (confirmation) stopConfirmed = await confirmation.wait
      if (!stopConfirmed) {
        stopConfirmed = false
        diagnostics.push(
          diagnostic(
            'abort-unconfirmed',
            this.mask('Pi did not confirm that the turn stopped through its event sequence'),
          ),
        )
      }
    } catch (cause) {
      stopConfirmed = false
      diagnostics.push(diagnostic('abort-unconfirmed', this.mask(message(cause))))
    } finally {
      confirmation?.dispose()
    }
    const facts: PiCancelFacts = {
      runtimeSessionId: path,
      workDir: request.target.workDir,
      cancelled: true,
      stopConfirmed,
    }
    return { ok: true, value: facts, diagnostics }
  }

  /**
   * Compact against the bound Pi Session.
   *
   * Acquires the per-session prompt mutex (so a concurrent `prompt()`
   * from a Workflow turn, an idle Follow-up, or another compact cannot
   * race on the same physical session), then guards the physical
   * session's `isStreaming` flag — a streaming session is reported as
   * `conflict` (the AgentSession grain already enforces logical
   * idleness; this is the physical-session backstop). On the success
   * path the runtime subscribes a `createPiProjector` so the
   * `compaction_start` / `compaction_end` events are projected through
   * the existing session event channel, then calls Pi's native
   * `session.compact()`. The result carries the unchanged
   * `runtimeSessionId` so the handler can translate it into the
   * SessionCommand contract (which omits `runtimeSessionId` for
   * compact, since the identity is preserved).
   *
   * The optional `observer` is the same shape as the Follow-up
   * observer — when present, the projector delivers the projected
   * events through it so the handler can mirror them into the
   * AgentSession event channel. The runtime itself does not retain
   * a long-lived subscription; the subscription is torn down in
   * `finally` after the compact call resolves.
   *
   * On any compact failure the runtime returns a `turn-failed`
   * carrying the underlying error — it MUST NOT synthesize a summary
   * or fabricate a compaction record.
   * A missing bound file surfaces as `missing-session` with a Reset
   * hint (no silent new session).
   */
  async compact(request: PiCompactRequest, observer?: PiTurnObserver): Promise<PiCompactResult> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId)
      return this.failure('missing-session', 'Pi compact requires a bound Session', [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure('incompatible-runtime', 'Pi runtimeSessionId must be an absolute session-file path')
    const session = await this.resolveFollowupSession(path, request.target.workDir)
    if (!session.ok) return session.failure

    const projector = createPiProjector(
      path,
      request.target.workDir,
      this.deps.masker ?? createCredentialMaskerFromEnvironment(),
    )
    return this.withSessionLock(path, async () => {
      if (session.value.isStreaming) {
        return this.failure('conflict', 'Pi compact refused: the physical session is still streaming', [
          diagnostic(
            'session-streaming',
            'Cannot compact while the Pi session is streaming; wait for the turn to finish',
          ),
        ])
      }
      const diagnostics: PiDiagnostic[] = []
      diagnostics.push(...projector.diagnostics().map((item) => diagnostic(item.code, this.mask(item.message), 'info')))
      const pendingReports: Promise<void>[] = []
      const report = (events: readonly PiRuntimeEvent[]) =>
        events.forEach((event) => {
          const result = observer?.onEvent?.(event)
          if (result) pendingReports.push(Promise.resolve(result))
        })
      let unsubscribe: () => void = () => {}
      try {
        unsubscribe = session.value.subscribe((event) => report(projector.project(event)))
        await session.value.compact()
        report(projector.reconcile(session.value.messages))
        await Promise.all(pendingReports)
        const facts: PiCompactFacts = { runtimeSessionId: path, workDir: request.target.workDir }
        return { ok: true, value: facts, diagnostics }
      } catch (cause) {
        return this.failure('turn-failed', 'Pi compact failed', [
          diagnostic('compact-failed', this.mask(message(cause))),
        ])
      } finally {
        unsubscribe()
      }
    })
  }

  /**
   * Reset against a bound Pi Session.
   *
   * Best-effort opens the bound session to read the current model and
   * thinking level; if the bound file is missing, Reset still proceeds
   * and skips the carry-over (it is the recovery operation).
   * `services.createSession(workDir)` produces a fresh empty Pi
   * session in the same work directory; the carried model and thinking
   * level, when available, are applied onto the new session via
   * `setModel` / `setThinkingLevel`. The new session is cached under
   * the new path; the prior session file is left on disk for audit.
   *
   * The returned `runtimeSessionId` is the new session file path
   * (necessarily different from the request id, which the
   * `SessionCommand` reset rule validates). The Server-side grain
   * performs the binding replacement and lineage append using the
   * returned id — `PiRuntime.reset` does not touch lineage itself.
   */
  async reset(request: PiResetRequest): Promise<PiResetResult> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    const workDir = request.target.workDir
    const priorPath = request.target.runtimeSessionId ? normalizedPath(request.target.runtimeSessionId) : null
    const cachedPrior: PiSdkSession | null = priorPath ? (this.sessions.get(priorPath) ?? null) : null
    let openedPrior: PiSdkSession | null = null
    const carry = await this.readCarryOver(priorPath, workDir, cachedPrior, (session) => {
      openedPrior = session
    })

    let nextSession: PiSdkSession
    try {
      nextSession = await this.state.services.createSession(workDir)
    } catch (cause) {
      if (openedPrior && priorPath) this.sessions.set(priorPath, openedPrior)
      return this.failure('turn-failed', 'Pi reset failed: could not create a new Pi session', [
        diagnostic('reset-create-failed', this.mask(message(cause))),
      ])
    }
    const newPath = normalizedPath(nextSession.sessionFile)
    if (!newPath) {
      nextSession.dispose()
      if (openedPrior && priorPath) this.sessions.set(priorPath, openedPrior)
      return this.failure('incompatible-runtime', 'Pi did not return an absolute session-file path for the new session')
    }

    const diagnostics: PiDiagnostic[] = []
    if (carry?.model !== undefined) {
      try {
        await nextSession.setModel(carry.model)
      } catch (cause) {
        diagnostics.push(diagnostic('reset-model-carry-failed', this.mask(message(cause))))
      }
    }
    if (carry?.thinkingLevel) {
      try {
        nextSession.setThinkingLevel(carry.thinkingLevel)
      } catch (cause) {
        diagnostics.push(diagnostic('reset-thinking-carry-failed', this.mask(message(cause))))
      }
    }

    const priorToDispose: PiSdkSession | null = openedPrior ?? cachedPrior
    if (priorToDispose && priorPath) {
      try {
        priorToDispose.dispose()
      } catch {
        /* best-effort cleanup */
      }
      if (this.sessions.get(priorPath) === priorToDispose) this.sessions.delete(priorPath)
    }
    this.sessions.set(newPath, nextSession)

    const facts: PiResetFacts = { runtimeSessionId: newPath, workDir }
    return { ok: true, value: facts, diagnostics }
  }

  private async readCarryOver(
    priorPath: string | null,
    workDir: string,
    cached: PiSdkSession | null,
    capture: (session: PiSdkSession) => void,
  ): Promise<{ model: unknown; thinkingLevel: string } | null> {
    const services = this.state.services
    if (!services || !priorPath) return null
    if (cached) {
      capture(cached)
      return { model: cached.getModel(), thinkingLevel: cached.getThinkingLevel() }
    }
    try {
      const session = await services.openSession(priorPath, workDir)
      capture(session)
      return { model: session.getModel(), thinkingLevel: session.getThinkingLevel() }
    } catch {
      return null
    }
  }

  private async resolveFollowupSession(
    path: string,
    workDir: string,
  ): Promise<{ ok: true; value: PiSdkSession } | { ok: false; failure: PiResult<never> }> {
    if (!this.state.services) return { ok: false, failure: this.unavailable() }
    let session: PiSdkSession
    try {
      const cached = this.sessions.get(path)
      if (cached) {
        if (this.state.services.validateSessionFile) {
          await this.state.services.validateSessionFile(path, cached.sessionId)
        }
        return { ok: true, value: cached }
      }
      const opened = await this.state.services.openSession(path, workDir)
      session = opened
      this.sessions.set(path, session)
      return { ok: true, value: session }
    } catch (cause) {
      return {
        ok: false,
        failure: this.failure(
          'missing-session',
          'The bound Pi Session is missing or corrupt — issue a Reset to establish a fresh Pi Session, then retry',
          [resetDiagnostic(), diagnostic('session-open-failed', this.mask(message(cause)))],
        ),
      }
    }
  }

  async shutdown(): Promise<void> {
    for (const session of this.sessions.values()) session.dispose()
    this.sessions.clear()
    this.sessionMutexes.clear()
    await this.state.services?.close()
    this.state.services = null
    this.state.ready = false
  }

  private async attemptStart(): Promise<PiResult<PiReadyState>> {
    this.state.ready = false
    this.state.catalog = null
    this.state.diagnostic = null
    try {
      const services = await (this.deps.sdkFactory ?? realPiSdkFactory).create({
        cwd: process.cwd(),
        agentDir: this.deps.agentDir,
      })
      this.state.services = services
      try {
        const models = await services.catalog()
        this.state.catalog = {
          models: models.map((model) => ({
            provider: model.provider,
            id: model.id,
            thinkingLevels: [...(model.thinkingLevels ?? ['off'])],
          })),
        }
      } catch (cause) {
        this.state.diagnostic = diagnostic(
          'pi-catalog-failed',
          `Pi model catalog unavailable: ${this.mask(message(cause))}`,
        )
        this.state.services = null
        await services.close().catch(() => undefined)
        return this.unavailable()
      }
      this.state.ready = true
      return { ok: true, value: this.readyState(), diagnostics: this.state.diagnostic ? [this.state.diagnostic] : [] }
    } catch (cause) {
      this.state.services = null
      this.state.diagnostic = diagnostic('pi-start-failed', this.mask(message(cause)))
      return this.unavailable()
    }
  }

  private policy(): PiProviderErrorPolicy {
    return this.deps.providerErrorPolicy ?? DEFAULT_PI_PROVIDER_ERROR_POLICY
  }
  private mask(value: string): string {
    return (this.deps.masker ?? createCredentialMaskerFromEnvironment()).mask(value)
  }
  private readyState(): PiReadyState {
    return { ready: this.state.ready, diagnostic: this.state.diagnostic, catalog: this.state.catalog }
  }
  private unavailable(): PiResult<never> {
    return {
      ok: false,
      error: piError(
        'unavailable-runtime',
        'Pi runtime is not ready',
        this.state.diagnostic ? [this.state.diagnostic] : [],
      ),
      diagnostics: this.state.diagnostic ? [this.state.diagnostic] : [],
    }
  }
  private failure(
    kind: 'invalid-input' | 'missing-session' | 'incompatible-runtime' | 'turn-failed' | 'conflict',
    messageText: string,
    diagnostics: readonly PiDiagnostic[] = [],
  ): PiResult<never> {
    return { ok: false, error: piError(kind, messageText, diagnostics), diagnostics }
  }
  private finishFailure(
    kind: 'deadline-exceeded' | 'interrupted' | 'turn-failed',
    messageText: string,
    diagnostics: PiDiagnostic[] = [],
  ): PiResult<PiTurnResult> {
    return { ok: false, error: piError(kind, messageText, diagnostics), diagnostics }
  }
}

function isMissingSessionFile(cause: unknown): boolean {
  if (!cause || typeof cause !== 'object') return false
  const value = cause as { code?: unknown; cause?: unknown }
  return value.code === 'ENOENT' || isMissingSessionFile(value.cause)
}

function normalizedPath(value: string | undefined): string | null {
  if (!value) return null
  const path = value.replaceAll('\\', '/')
  return path.startsWith('/') ? resolve(path) : null
}
function splitModel(value: string): { provider: string; id: string } | null {
  const index = value.indexOf('/')
  return index > 0 && index < value.length - 1 ? { provider: value.slice(0, index), id: value.slice(index + 1) } : null
}
function message(cause: unknown): string {
  return cause instanceof Error ? cause.message || 'Pi operation failed' : String(cause)
}
function finalText(messages: readonly { role?: string; content?: unknown }[]): string | null {
  const assistant = [...messages].reverse().find((item) => item.role === 'assistant')
  return contentText(assistant?.content)
}
function contentText(content: unknown): string | null {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return null
  const text = content
    .map((part) =>
      typeof part === 'string'
        ? part
        : part && typeof part === 'object' && 'text' in part && typeof part.text === 'string'
          ? part.text
          : '',
    )
    .join('')
  return text || null
}
function lastMessageFailed(messages: readonly { role?: string; stopReason?: string }[]): boolean {
  const item = [...messages].reverse().find((entry) => entry.role === 'assistant')
  return item?.stopReason === 'error'
}
function lastMessageError(messages: readonly { role?: string; errorMessage?: string }[]): string | undefined {
  return [...messages].reverse().find((entry) => entry.role === 'assistant')?.errorMessage
}
function isRetryFailure(event: unknown, policy: PiProviderErrorPolicy): boolean {
  if (!event || typeof event !== 'object' || (event as { type?: unknown }).type !== 'auto_retry_start') return false
  const value = event as { errorMessage?: unknown; attempt?: unknown }
  const text = typeof value.errorMessage === 'string' ? value.errorMessage : ''
  return (
    isProviderFailure(text, policy) ||
    (typeof value.attempt === 'number' && value.attempt >= policy.consecutiveRetryThreshold)
  )
}
function isPiStopEvent(event: unknown): boolean {
  return Boolean(event && typeof event === 'object' && (event as { type?: unknown }).type === 'agent_settled')
}
function watchPiStop(
  session: PiSdkSession,
  clock: PiClock,
): { readonly wait: Promise<boolean>; readonly dispose: () => void } {
  let resolveWait: (confirmed: boolean) => void = () => {}
  const wait = new Promise<boolean>((resolve) => {
    resolveWait = resolve
  })
  let settled = false
  let stopEventObserved = false
  let timeout: unknown | null = null
  let unsubscribe: (() => void) | null = null
  const complete = (confirmed: boolean) => {
    if (settled) return
    settled = true
    if (timeout !== null) clock.clearTimeout(timeout)
    unsubscribe?.()
    resolveWait(confirmed)
  }
  const removeListener = session.subscribe((event) => {
    if (isPiStopEvent(event)) {
      stopEventObserved = true
      if (!session.isStreaming) complete(true)
    }
  })
  unsubscribe = removeListener
  if (settled) {
    removeListener()
    return { wait, dispose: () => complete(false) }
  }
  timeout = clock.setTimeout(() => complete(stopEventObserved && !session.isStreaming), CANCEL_CONFIRMATION_TIMEOUT_MS)
  return { wait, dispose: () => complete(false) }
}
async function abortAndDiagnose(
  session: PiSdkSession,
  diagnostics: PiDiagnostic[],
  mask: (text: string) => string,
): Promise<void> {
  try {
    await session.abort()
    await Promise.resolve()
    if (session.isStreaming)
      diagnostics.push(diagnostic('abort-unconfirmed', mask('Pi did not confirm that the turn stopped')))
  } catch (cause) {
    diagnostics.push(diagnostic('abort-unconfirmed', mask(message(cause))))
  }
}
