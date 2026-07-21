import { createCredentialMaskerFromEnvironment, CredentialMasker } from "../task-log.js"
import { resolve } from "node:path"
import { diagnostic, piError, resetDiagnostic } from "./errors.js"
import { isProviderFailure, DEFAULT_PI_PROVIDER_ERROR_POLICY } from "./policy.js"
import { createPiProjector } from "./projector.js"
import { realPiSdkFactory, type PiSdkFactory, type PiSdkServices, type PiSdkSession } from "./sdk.js"
import type { PiDiagnostic, PiProviderErrorPolicy, PiResult, PiRuntimeEvent, PiSessionCreateRequest, PiSessionResult, PiTurnObserver, PiTurnRequest, PiTurnResult, PiReadyState, PiCatalog } from "./types.js"

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
  private readonly state: { ready: boolean; diagnostic: PiDiagnostic | null; catalog: PiCatalog | null; services: PiSdkServices | null } = { ready: false, diagnostic: null, catalog: null, services: null }
  private startInFlight: Promise<PiResult<PiReadyState>> | null = null

  constructor(deps: PiRuntimeDeps) { this.deps = deps }

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
      const promptOutcome = await Promise.race([
        session.prompt(request.prompt, { expandPromptTemplates: false }).then(() => "completed" as const),
        fixedSignal.then(() => "fixed" as const),
      ])
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

  async shutdown(): Promise<void> { for (const session of this.sessions.values()) session.dispose(); this.sessions.clear(); await this.state.services?.close(); this.state.services = null; this.state.ready = false }

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
