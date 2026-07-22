import { createCredentialMaskerFromEnvironment, CredentialMasker } from "../task-log.js"
import { resolve } from "node:path"
import { diagnostic, piError, resetDiagnostic } from "./errors.js"
import { isProviderFailure, DEFAULT_PI_PROVIDER_ERROR_POLICY } from "./policy.js"
import { createPiProjector } from "./projector.js"
import { realPiSdkFactory, type PiSdkFactory, type PiSdkServices, type PiSdkSession } from "./sdk.js"
import type {
  PiCancelFacts,
  PiCancelRequest,
  PiCancelResult,
  PiDiagnostic,
  PiFollowupRequest,
  PiFollowupResult,
  PiProviderErrorPolicy,
  PiReadyState,
  PiCatalog,
  PiResult,
  PiRuntimeEvent,
  PiSessionCreateRequest,
  PiSessionResult,
  PiTurnObserver,
  PiTurnRequest,
  PiTurnResult,
} from "./types.js"

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

const defaultClock: PiClock = { now: () => Date.now(), setTimeout: (callback, delay) => setTimeout(callback, delay), clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>) }

export class PiRuntime {
  private readonly deps: PiRuntimeDeps
  private readonly sessions = new Map<string, PiSdkSession>()
  private readonly sessionMutexes = new Map<string, Promise<unknown>>()
  private readonly state: { ready: boolean; diagnostic: PiDiagnostic | null; catalog: PiCatalog | null; services: PiSdkServices | null } = { ready: false, diagnostic: null, catalog: null, services: null }
  private startInFlight: Promise<PiResult<PiReadyState>> | null = null

  constructor(deps: PiRuntimeDeps) { this.deps = deps }

  private withSessionLock<T>(path: string, operation: () => Promise<T>): Promise<T> {
    const previous = this.sessionMutexes.get(path)
    if (previous) {
      const settled = previous.catch(() => undefined)
      const current = settled.then(operation)
      this.sessionMutexes.set(path, current.catch(() => undefined))
      return current
    }
    const current = operation()
    this.sessionMutexes.set(path, current.catch(() => undefined))
    return current
  }

  async start(): Promise<PiResult<PiReadyState>> {
    if (this.state.ready) return { ok: true, value: this.readyState(), diagnostics: [] }
    if (this.startInFlight) return this.startInFlight
    this.startInFlight = this.attemptStart()
    try { return await this.startInFlight } finally { this.startInFlight = null }
  }

  ready(): boolean { return this.state.ready }
  diagnostic(): PiDiagnostic | null { return this.state.diagnostic }
  catalog(): PiCatalog | null { return this.state.catalog }

  async createSession(request: PiSessionCreateRequest): Promise<PiResult<PiSessionResult>> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    try {
      const session = await this.state.services.createSession(request.target.workDir)
      const path = normalizedPath(session.sessionFile)
      if (!path) return this.failure("incompatible-runtime", "Pi did not return an absolute session-file path")
      this.sessions.set(path, session)
      return { ok: true, value: { runtimeSessionId: path, workDir: request.target.workDir }, diagnostics: [] }
    } catch (cause) { return this.failure("turn-failed", "Pi Session creation failed", [diagnostic("session-create-failed", this.mask(message(cause)))]) }
  }

  async runTurn(request: PiTurnRequest, signal: AbortSignal, observer?: PiTurnObserver): Promise<PiResult<PiTurnResult>> {
    if (!this.state.ready || !this.state.services) return this.unavailable()
    if (!request.prompt || request.prompt.trim().length === 0) return this.failure("invalid-input", "Pi prompt must be non-empty")
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId) return this.failure("missing-session", "Pi turn requires a bound Session", [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure("incompatible-runtime", "Pi runtimeSessionId must be an absolute session-file path")
    let session: PiSdkSession
    try {
      session = this.sessions.get(path) ?? await this.state.services.openSession(path, request.target.workDir)
      this.sessions.set(path, session)
    } catch (cause) { return this.failure("missing-session", "The bound Pi Session is missing or corrupt", [resetDiagnostic(), diagnostic("session-open-failed", this.mask(message(cause)))]) }

    const diagnostics: PiDiagnostic[] = []
    if (request.options?.unknownKeys && request.options.unknownKeys.length > 0) {
      diagnostics.push(diagnostic("options-unknown-keys", `Ignored unknown option keys: ${request.options.unknownKeys.join(", ")}`, "info"))
    }
    const projector = createPiProjector(path, request.target.workDir, this.deps.masker ?? createCredentialMaskerFromEnvironment())
    const report = (events: readonly PiRuntimeEvent[]) => events.forEach((event) => observer?.onEvent?.(event))
    let fixed: PiResult<PiTurnResult> | null = null
    let resolveFixed!: () => void
    const fixedSignal = new Promise<void>((resolve) => { resolveFixed = resolve })
    const fixAndAbort = (result: PiResult<PiTurnResult>) => {
      if (fixed) return
      fixed = result
      void abortAndDiagnose(session, diagnostics, this.mask.bind(this)).finally(resolveFixed)
    }
    const unsubscribe = session.subscribe((event) => {
      const facts = projector.project(event)
      report(facts)
      if (isRetryFailure(event, this.policy()) && !fixed) {
        fixAndAbort(this.finishFailure("turn-failed", "Pi provider retries exhausted", diagnostics))
      }
    })
    const model = request.options?.model
    if (model) {
      const parsed = splitModel(model)
      if (!parsed) { unsubscribe(); return this.failure("invalid-input", "options.model must use provider/model syntax") }
      try { await session.setModel(this.state.services.model(parsed.provider, parsed.id)) } catch (cause) { unsubscribe(); return this.failure("turn-failed", "Pi rejected the selected model", [diagnostic("model-rejected", this.mask(message(cause)))]) }
    }
    if (request.options?.variant) session.setThinkingLevel(request.options.variant)
    const clock = this.deps.clock ?? defaultClock
    const duration = request.durationMs ?? null
    const deadline = duration !== null && duration >= 0 ? clock.setTimeout(() => { fixAndAbort(this.finishFailure("deadline-exceeded", "Pi turn deadline exceeded", diagnostics)) }, duration) : null
    const warningDelay = duration !== null && duration >= 0 ? Math.max(0, duration - 5 * 60_000) : null
    const warning = warningDelay === null ? null : clock.setTimeout(() => { if (!fixed) void session.steer("This turn is nearing its execution deadline; wrap up the current work and return the final answer.") }, warningDelay)
    const cancel = () => { if (!fixed) fixAndAbort(this.finishFailure("interrupted", "Pi turn was interrupted", diagnostics)) }
    signal.addEventListener("abort", cancel, { once: true })
    try {
      const promptOutcome = await this.withSessionLock(path, () => Promise.race([
        session.prompt(request.prompt, { expandPromptTemplates: false }).then(() => "completed" as const),
        fixedSignal.then(() => "fixed" as const),
      ]))
      if (fixed) return fixed
      if (lastMessageFailed(session.messages)) return this.failure("turn-failed", "Pi turn failed", [diagnostic("turn-failed", this.mask(lastMessageError(session.messages) ?? "Pi reported an error"))])
      report(projector.reconcile(session.messages))
      diagnostics.push(...projector.diagnostics().map((item) => diagnostic(item.code, this.mask(item.message), "info")))
      return { ok: true, value: { facts: { finalAssistantText: finalText(session.messages), runtimeSessionId: path, workDir: request.target.workDir }, diagnostics }, diagnostics }
    } catch (cause) {
      if (fixed) return fixed
      return this.failure("turn-failed", "Pi turn failed", [diagnostic("turn-failed", this.mask(message(cause)))])
    } finally {
      signal.removeEventListener("abort", cancel)
      if (deadline !== null) clock.clearTimeout(deadline)
      if (warning !== null) clock.clearTimeout(warning)
      unsubscribe()
    }
  }

  /**
   * Follow-up against a bound Pi Session (issue #451 / design D5).
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
    if (!request.prompt || request.prompt.trim().length === 0) return this.failure("invalid-input", "Pi follow-up prompt must be non-empty")
    const runtimeSessionId = request.target.runtimeSessionId
    if (!runtimeSessionId) return this.failure("missing-session", "Pi follow-up requires a bound Session", [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure("incompatible-runtime", "Pi runtimeSessionId must be an absolute session-file path")
    const session = await this.resolveFollowupSession(path, request.target.workDir)
    if (!session.ok) return session.failure

    if (session.value.isStreaming) {
      try {
        await session.value.steer(request.prompt)
        return {
          ok: true,
          value: { runtimeSessionId: path, workDir: request.target.workDir },
          diagnostics: [],
        }
      } catch (cause) {
        return this.failure("turn-failed", "Pi steer failed", [diagnostic("steer-failed", this.mask(message(cause)))])
      }
    }

    const projector = createPiProjector(path, request.target.workDir, this.deps.masker ?? createCredentialMaskerFromEnvironment())
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
                settle(this.failure(
                  "turn-failed",
                  "Pi rejected follow-up reception (preflight rejected the prompt)",
                  [diagnostic("preflight-rejected", "Pi preflight rejected the follow-up prompt — model or credentials missing")],
                ))
              }
            },
          })
          report(projector.reconcile(session.value.messages))
        } catch (cause) {
          settle(this.failure("turn-failed", "Pi follow-up prompt failed", [diagnostic("prompt-failed", this.mask(message(cause)))]))
        } finally {
          unsubscribe()
        }
      })
    })
  }

  /**
   * Cancel against an active Pi Session turn (issue #451 / design D6).
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
    if (!runtimeSessionId) return this.failure("missing-session", "Pi cancel requires a bound Session", [resetDiagnostic()])
    const path = normalizedPath(runtimeSessionId)
    if (!path) return this.failure("incompatible-runtime", "Pi runtimeSessionId must be an absolute session-file path")
    const session = await this.resolveFollowupSession(path, request.target.workDir)
    if (!session.ok) return session.failure

    const diagnostics: PiDiagnostic[] = []
    let stopConfirmed = true
    try {
      await session.value.abort()
      await Promise.resolve()
      if (session.value.isStreaming) {
        stopConfirmed = false
        diagnostics.push(diagnostic("abort-unconfirmed", this.mask("Pi did not confirm that the turn stopped")))
      }
    } catch (cause) {
      stopConfirmed = false
      diagnostics.push(diagnostic("abort-unconfirmed", this.mask(message(cause))))
    }
    const facts: PiCancelFacts = { runtimeSessionId: path, workDir: request.target.workDir, cancelled: true, stopConfirmed }
    return { ok: true, value: facts, diagnostics }
  }

  private async resolveFollowupSession(path: string, workDir: string): Promise<{ ok: true; value: PiSdkSession } | { ok: false; failure: PiResult<never> }> {
    if (!this.state.services) return { ok: false, failure: this.unavailable() }
    let session: PiSdkSession
    try {
      session = this.sessions.get(path) ?? await this.state.services.openSession(path, workDir)
      this.sessions.set(path, session)
      return { ok: true, value: session }
    } catch (cause) {
      return {
        ok: false,
        failure: this.failure(
          "missing-session",
          "The bound Pi Session is missing or corrupt — issue a Reset to establish a fresh Pi Session, then retry",
          [resetDiagnostic(), diagnostic("session-open-failed", this.mask(message(cause)))],
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
    try {
      const services = await (this.deps.sdkFactory ?? realPiSdkFactory).create({ cwd: process.cwd(), agentDir: this.deps.agentDir })
      const models = await services.catalog()
      this.state.services = services
      this.state.catalog = { models: models.map((model) => ({ provider: model.provider, id: model.id, thinkingLevels: model.thinkingLevels ?? [] })) }
      this.state.diagnostic = this.state.catalog.models.length === 0 ? diagnostic("empty-catalog", "Pi model catalog is empty; model validity will be decided at turn time", "warning") : null
      this.state.ready = true
      return { ok: true, value: this.readyState(), diagnostics: this.state.diagnostic ? [this.state.diagnostic] : [] }
    } catch (cause) { this.state.services = null; this.state.diagnostic = diagnostic("pi-start-failed", this.mask(message(cause))); return this.unavailable() }
  }

  private policy(): PiProviderErrorPolicy { return this.deps.providerErrorPolicy ?? DEFAULT_PI_PROVIDER_ERROR_POLICY }
  private mask(value: string): string { return (this.deps.masker ?? createCredentialMaskerFromEnvironment()).mask(value) }
  private readyState(): PiReadyState { return { ready: this.state.ready, diagnostic: this.state.diagnostic, catalog: this.state.catalog } }
  private unavailable(): PiResult<never> { return { ok: false, error: piError("unavailable-runtime", "Pi runtime is not ready", this.state.diagnostic ? [this.state.diagnostic] : []), diagnostics: this.state.diagnostic ? [this.state.diagnostic] : [] } }
  private failure(kind: "invalid-input" | "missing-session" | "incompatible-runtime" | "turn-failed", messageText: string, diagnostics: readonly PiDiagnostic[] = []): PiResult<never> { return { ok: false, error: piError(kind, messageText, diagnostics), diagnostics } }
  private finishFailure(kind: "deadline-exceeded" | "interrupted" | "turn-failed", messageText: string, diagnostics: PiDiagnostic[] = []): PiResult<PiTurnResult> { return { ok: false, error: piError(kind, messageText, diagnostics), diagnostics } }
}

function normalizedPath(value: string | undefined): string | null { if (!value) return null; const path = value.replaceAll("\\", "/"); return path.startsWith("/") ? resolve(path) : null }
function splitModel(value: string): { provider: string; id: string } | null { const index = value.indexOf("/"); return index > 0 && index < value.length - 1 ? { provider: value.slice(0, index), id: value.slice(index + 1) } : null }
function message(cause: unknown): string { return cause instanceof Error ? cause.message || "Pi operation failed" : String(cause) }
function finalText(messages: readonly { role?: string; content?: unknown }[]): string | null { const assistant = [...messages].reverse().find((item) => item.role === "assistant"); return contentText(assistant?.content) }
function contentText(content: unknown): string | null { if (typeof content === "string") return content; if (!Array.isArray(content)) return null; const text = content.map((part) => typeof part === "string" ? part : part && typeof part === "object" && "text" in part && typeof part.text === "string" ? part.text : "").join(""); return text || null }
function lastMessageFailed(messages: readonly { role?: string; stopReason?: string }[]): boolean { const item = [...messages].reverse().find((entry) => entry.role === "assistant"); return item?.stopReason === "error" }
function lastMessageError(messages: readonly { role?: string; errorMessage?: string }[]): string | undefined { return [...messages].reverse().find((entry) => entry.role === "assistant")?.errorMessage }
function isRetryFailure(event: unknown, policy: PiProviderErrorPolicy): boolean { if (!event || typeof event !== "object" || (event as { type?: unknown }).type !== "auto_retry_start") return false; const value = event as { errorMessage?: unknown; attempt?: unknown }; const text = typeof value.errorMessage === "string" ? value.errorMessage : ""; return isProviderFailure(text, policy) || (typeof value.attempt === "number" && value.attempt >= policy.consecutiveRetryThreshold) }
async function abortAndDiagnose(session: PiSdkSession, diagnostics: PiDiagnostic[], mask: (text: string) => string): Promise<void> { try { await session.abort(); await Promise.resolve(); if (session.isStreaming) diagnostics.push(diagnostic("abort-unconfirmed", mask("Pi did not confirm that the turn stopped"))) } catch (cause) { diagnostics.push(diagnostic("abort-unconfirmed", mask(message(cause)))) } }
