/**
 * Mohist-owned boundary types for the OpenCode runtime deep module.
 *
 * The runtime drives `@opencode-ai/sdk/v2` directly; every type that
 * crosses the module boundary is owned by Mohist, not the generated
 * SDK. This keeps SDK drift contained to one module: callers depend
 * on these shapes only, and the SDK is an implementation detail
 * inside the module.
 *
 * See `specs/opencode-runtime/spec.md` (deep-module isolation) and
 * `design/runtimes/opencode.md` (D2, D8).
 */

export type RuntimeDiagnosticSeverity = "info" | "warning" | "error"

export interface RuntimeDiagnostic {
  readonly severity: RuntimeDiagnosticSeverity
  readonly code: string
  readonly message: string
  readonly details?: Record<string, unknown>
}

export interface RuntimeModelDescriptor {
  readonly providerID: string
  readonly modelID: string
  readonly variants: readonly string[]
}

export interface RuntimeModelCatalog {
  readonly models: readonly RuntimeModelDescriptor[]
  readonly fetchedAt: number
}

export type RuntimeSessionTarget = {
  readonly runtime: "opencode"
  readonly runtimeSessionId: string | null
  readonly workDir: string
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
  readonly options?: RuntimeTurnOptions | null
  readonly onSessionReady?: (runtimeSessionId: string, workDir: string) => void | Promise<void>
  readonly onEvent?: (event: RuntimeTurnEvent) => void
}

export interface RuntimeTurnEvent {
  readonly type: string
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly payload: Record<string, unknown>
}

export interface RuntimeTurnOptions {
  readonly model?: { providerID: string; modelID: string } | null
  readonly variant?: string | null
  readonly unknownKeys?: readonly string[]
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
 * Inputs for a Follow-up turn on an existing Runtime Session. Wraps
 * `client.session.promptAsync` (issue-410 T-003 / design D3). The
 * runtime verifies the persisted binding still resolves to a live
 * physical Session before dispatching the prompt; a stale binding
 * surfaces as `missing-session` (the existing Reset hint).
 *
 * `options.model` / `options.variant` override the per-turn model on
 * the prompt body (same shape as {@link RuntimeTurnRequest}); the
 * physical Session is never rotated on a Follow-up — the binding
 * owns the lifetime, the prompt only sets the per-turn parameters.
 */
export interface RuntimeFollowupRequest {
  readonly target: RuntimeSessionTarget
  readonly prompt: string
  readonly options?: RuntimeTurnOptions | null
}

export interface RuntimeFollowupFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface RuntimeFollowupResult {
  readonly facts: RuntimeFollowupFacts
  readonly diagnostics: readonly RuntimeDiagnostic[]
}

/**
 * Inputs for a Cancel against an active Runtime Session turn. Wraps
 * `client.session.abort` (issue-410 T-003 / design D3). The runtime
 * resolves the binding first; a stale binding surfaces as
 * `missing-session` (the existing Reset hint). `cancelled: true` is
 * the authoritative reply — whether the agent honours the cancellation
 * is the agent's decision; the runtime reports the attempt honestly.
 */
export interface RuntimeCancelRequest {
  readonly target: RuntimeSessionTarget
}

export interface RuntimeCancelFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly cancelled: true
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
 * Defaults match `design/runtimes/opencode.md` Provider 错误失败策略:
 * the pattern set covers quota/credit/billing wording in both
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
}

export interface RuntimeHealthCheck {
  readonly ok: boolean
  readonly diagnostic: RuntimeDiagnostic | null
}
