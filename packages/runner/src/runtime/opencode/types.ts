/**
 * Mohist-owned boundary types for the OpenCode runtime deep module.
 *
 * The runtime drives `@opencode-ai/sdk/v2` directly; every type that
 * crosses the module boundary is owned by Mohist, not the generated
 * SDK. This keeps SDK drift contained to one module: callers depend
 * on these shapes only, and the SDK is an implementation detail
 * inside the module.
 */

export type RuntimeDiagnosticSeverity = "info" | "warning" | "error"

export interface RuntimeDiagnostic {
  readonly severity: RuntimeDiagnosticSeverity
  readonly code: string
  readonly message: string
  readonly details?: Record<string, unknown>
}

export type RuntimeSessionTarget = {
  readonly runtime: "opencode"
  readonly runtimeSessionId: string | null
  readonly workDir: string
}

export interface RuntimeSessionResolveRequest {
  readonly target: RuntimeSessionTarget
}

export interface RuntimeSessionResolveResult {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly activeTurn: boolean
}

export interface RuntimeSessionCreateRequest {
  readonly target: RuntimeSessionTarget
  readonly model?: { providerID: string; modelID: string } | null
}

export interface RuntimeSessionCreateResult {
  readonly runtimeSessionId: string
  readonly workDir: string
}

/**
 * Inputs for a Workflow-owned turn over the OpenCode runtime. The
 * runtime owns the SDK DTO construction and the per-turn application
 * of `model`/`variant`; callers pass only Mohist-owned shapes.
 *
 * `runtimeSessionId` carries the current logical Session's physical
 * binding. `null` means "no current binding — create a new physical
 * Session in `workDir`". A persisted binding whose physical Session
 * cannot be restored is the caller's problem (the runtime surfaces a
 * `Reset` hint via `missing-session`); the runtime itself never
 * implicitly calls `create` to fabricate continuous context.
 *
 * `options` is the Mohist Action's parsed `options` shape; the
 * runtime ignores unknown keys (tolerant of persisted legacy keys
 * such as `type` or liveness configuration) and reports them as
 * diagnostics.
 */
export interface RuntimeTurnRequest {
  readonly target: RuntimeSessionTarget
  readonly prompt: string
  /**
   * Optional per-turn deadline declaration. When set, the runtime
   * layers a timeout onto the external abort signal and schedules a
   * single task-agnostic wrap-up warning 5 minutes before the
   * deadline (or at turn start when the deadline is shorter than 5
   * minutes). Omitted means no deadline, no warning injection, no
   * internal timer — the runtime awaits the prompt and honours the
   * external signal for cancellation only.
   */
  readonly deadlineMs?: number | null
  /** Per-work budget; expiry is a containment event that quarantines the generation. */
  readonly resourceBudgetMs?: number | null
  readonly options?: RuntimeTurnOptions | null
  /**
   * Optional native file parts appended to the prompt body. The
   * runtime carries them as `FilePartInput` entries alongside the
   * single text part on `client.session.prompt`. Issue-513: this is
   * the per-runtime visibility hook for image attachments — the
   * Agent always has the workspace file as the source of truth; the
   * file part is additive so the model can see the image directly
   * when the runtime supports it.
   */
  readonly fileParts?: readonly RuntimeFilePart[] | null
}

/**
 * Native file part delivered through `client.session.prompt`'s
 * `parts` array. The OpenCode SDK accepts a data URL (e.g.
 * `data:image/png;base64,...`) for `url`; the Runner uses this only
 * for image attachments, and the data is fetched through the
 * owning-input scoped content route — never a caller temp URL.
 */
export interface RuntimeFilePart {
  readonly mime: string
  readonly filename: string
  readonly url: string
}

export interface RuntimeTurnEvent {
  readonly type: string
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly payload: Record<string, unknown>
}

export interface RuntimeTurnObserver {
  readonly onSessionReady?: (session: RuntimeSessionCreateResult) => void | Promise<void>
  readonly onEvent?: (event: RuntimeTurnEvent) => void
}

export interface RuntimeTurnOptions {
  readonly model?: { providerID: string; modelID: string } | null
  readonly variant?: string | null
  readonly unknownKeys?: readonly string[]
  readonly skills?: readonly { readonly name: string; readonly instructions: string }[]
}

export interface RuntimeTurnFacts {
  readonly finalAssistantText: string | null
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface RuntimeTurnResult {
  readonly facts: RuntimeTurnFacts
  readonly diagnostics: readonly RuntimeDiagnostic[]
}

/**
 * Inputs for a Follow-up turn on an existing Runtime Session. The
 * runtime verifies the persisted binding still resolves to a live
 * physical Session before running the prompt; a stale binding surfaces
 * as `missing-session` (the existing Reset hint).
 *
 * `options.model` / `options.variant` override the per-turn model on
 * the prompt body (same shape as {@link RuntimeTurnRequest}); the
 * physical Session is never rotated on a Follow-up — the binding
 * owns the lifetime, the prompt only sets the per-turn parameters.
 */
export interface RuntimeFollowupRequest {
  readonly target: RuntimeSessionTarget
  readonly prompt: string
  readonly fileParts?: readonly RuntimeFilePart[] | null
  readonly options?: RuntimeTurnOptions | null
}

export interface RuntimeFollowupFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly finalAssistantText?: string | null
}

export interface RuntimeFollowupResult {
  readonly facts: RuntimeFollowupFacts
  readonly diagnostics: readonly RuntimeDiagnostic[]
}

/**
 * Inputs for a Cancel against an active Runtime Session turn. Wraps
 * `client.session.abort`. The runtime
 * resolves the binding first; a stale binding surfaces as
 * `missing-session` (the existing Reset hint). `cancelled: true` records
 * the abort attempt, while `stopConfirmed` records the follow-up status
 * confirmation honestly.
 */
export interface RuntimeCancelRequest {
  readonly target: RuntimeSessionTarget
}

export interface RuntimeCancelFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly cancelled: true
  readonly stopConfirmed: boolean
}

export interface RuntimeCancelResult {
  readonly facts: RuntimeCancelFacts
  readonly diagnostics: readonly RuntimeDiagnostic[]
}

/**
 * Configuration for the runtime's provider-error failure policy.
 *
 * - A known structured `action.reason` is authoritative. Otherwise,
 *   `nonRecoverablePatterns` are matched against the `message` of a
 *   `session.status` retry event; a match on first occurrence aborts
 *   the turn with a `turn-failed` result carrying the provider message
 *   as diagnostics.
 * - `consecutiveRetryThreshold` is the maximum `attempt` value on
 *   a recoverable retry event after which the runtime aborts the
 *   turn (`turn-failed`). A recoverable retry sequence that
 *   completes the turn before `attempt` reaches the threshold is
 *   left to OpenCode.
 *
 * Defaults: the pattern set covers quota/credit/billing wording in both
 * English and Chinese, and the threshold defaults to 5.
 */
export interface RuntimeProviderErrorPolicy {
  readonly nonRecoverablePatterns: readonly RegExp[]
  readonly consecutiveRetryThreshold: number
}

export type RuntimeErrorKind =
  | "invalid-input"
  | "unavailable-runtime"
  | "missing-session"
  | "incompatible-runtime"
  | "permission-required"
  | "deadline-exceeded"
  | "interrupted"
  | "turn-failed"
  | "resource-containment"
  | "generation-drain-timeout"

export interface RuntimeError {
  readonly kind: RuntimeErrorKind
  readonly message: string
  readonly diagnostics: readonly RuntimeDiagnostic[]
}

export type RuntimeResult<T> =
  | { ok: true; value: T; diagnostics: readonly RuntimeDiagnostic[] }
  | { ok: false; error: RuntimeError; diagnostics: readonly RuntimeDiagnostic[] }

export interface RuntimeReadyState {
  readonly ready: boolean
  readonly diagnostic: RuntimeDiagnostic | null
  readonly ownership: RuntimeOwnershipSnapshot
}

export interface RuntimeOwnershipSnapshot {
  readonly ownerIds: readonly string[]
  readonly idleSince: number | null
  readonly activeOperations: number
  readonly generation: number | null
}

export interface RuntimeHealthCheck {
  readonly ok: boolean
  readonly diagnostic: RuntimeDiagnostic | null
}
