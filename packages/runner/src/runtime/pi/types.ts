export type PiDiagnosticSeverity = 'info' | 'warning' | 'error'

export interface PiDiagnostic {
  readonly severity: PiDiagnosticSeverity
  readonly code: string
  readonly message: string
  readonly details?: Record<string, unknown>
}

export type PiErrorKind =
  | 'invalid-input'
  | 'unavailable-runtime'
  | 'missing-session'
  | 'incompatible-runtime'
  | 'deadline-exceeded'
  | 'interrupted'
  | 'turn-failed'
  | 'conflict'

export interface PiError {
  readonly kind: PiErrorKind
  readonly message: string
  readonly diagnostics: readonly PiDiagnostic[]
}

export type PiResult<T> =
  | { readonly ok: true; readonly value: T; readonly diagnostics: readonly PiDiagnostic[] }
  | { readonly ok: false; readonly error: PiError; readonly diagnostics: readonly PiDiagnostic[] }

export interface PiModelDescriptor {
  readonly provider: string
  readonly id: string
  readonly thinkingLevels: readonly string[]
}

export interface PiCatalog {
  readonly models: readonly PiModelDescriptor[]
}

export interface PiReadyState {
  readonly ready: boolean
  readonly diagnostic: PiDiagnostic | null
  readonly catalog: PiCatalog | null
}

export interface PiSessionTarget {
  readonly runtime: 'pi'
  readonly runtimeSessionId: string | null
  readonly workDir: string
}

export interface PiSessionResolveRequest {
  readonly target: PiSessionTarget
}

export interface PiSessionCreateRequest {
  readonly target: PiSessionTarget
}

export interface PiSessionResult {
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface PiSessionResolveResult extends PiSessionResult {
  readonly activeTurn: boolean
}

export interface PiTurnOptions {
  readonly model?: string | null
  readonly variant?: string | null
  readonly reasoningEffort?: string | null
  readonly unknownKeys?: readonly string[]
  readonly skills?: readonly { readonly name: string; readonly instructions: string }[]
}

export interface PiTurnRequest {
  readonly target: PiSessionTarget
  readonly prompt: string
  readonly options?: PiTurnOptions | null
  readonly durationMs?: number | null
}

export interface PiRuntimeEvent {
  readonly id: string
  readonly type: string
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly payload: Record<string, unknown>
}

export interface PiTurnFacts {
  readonly finalAssistantText: string | null
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface PiTurnResult {
  readonly facts: PiTurnFacts
  readonly diagnostics: readonly PiDiagnostic[]
}

export interface PiTurnObserver {
  readonly onEvent?: (event: PiRuntimeEvent) => void | Promise<void>
}

export interface PiProviderErrorPolicy {
  readonly nonRecoverablePatterns: readonly RegExp[]
  readonly consecutiveRetryThreshold: number
}

/**
 * Inputs for a Follow-up on a Pi-bound AgentSession.
 *
 * The runtime resolves the persisted binding first; a stale binding
 * surfaces as `missing-session` (the existing Reset hint) — the runtime
 * never silently creates a new Pi Session to fabricate continuous
 * context. The physical Pi Session binding SHALL NOT rotate on a
 * Follow-up: `runtimeSessionId` stays unchanged regardless of whether
 * the Follow-up joins an active turn (`steer`) or starts a new idle
 * turn (`prompt` + `preflight`).
 */
export interface PiFollowupRequest {
  readonly target: PiSessionTarget
  readonly prompt: string
  readonly options?: PiTurnOptions | null
}

export interface PiFollowupFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
}

/**
 * Result of a Follow-up. The Follow-up resolves to a success only once
 * the runtime has either injected into the running turn (`steer`) or
 * received `preflight(true)` from the SDK (`prompt`); a preflight
 * rejection on the idle path means the Follow-up is reported as a
 * command failure without automatic retry — see `PiResult<PiFollowupFacts>`.
 */
export type PiFollowupResult = PiResult<PiFollowupFacts>

/**
 * Inputs for a Cancel against an active Pi Session turn.
 * Mirrors `RuntimeCancelRequest`.
 *
 * `runtimeSessionId` carries the bound physical Pi Session path.
 * `null` means "no current binding — cancel is a no-op"; in that
 * case the runtime short-circuits to `cancelled: true` (the abort was
 * attempted against nothing) without claiming stop confirmation.
 */
export interface PiCancelRequest {
  readonly target: PiSessionTarget
}

/**
 * Facts carried by a Cancel reply. `cancelled: true` is set when the
 * runtime has *attempted* to interrupt the turn (not necessarily
 * confirmed it stopped). `stopConfirmed: true` when the Pi session's
 * `isStreaming` getter has cleared (and/or the event sequence shows
 * the turn no longer streaming); `stopConfirmed: false` when the
 * runtime could not confirm the turn stopped — the upper layers
 * surface this through `interruptUnconfirmed` on the cancel HTTP
 * response so the API/user is never told a still-running turn has
 * been safely stopped.
 */
export interface PiCancelFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly cancelled: true
  readonly stopConfirmed: boolean
}

export type PiCancelResult = PiResult<PiCancelFacts>

/**
 * Inputs for a Compact against an idle Pi Session.
 *
 * Compact operates on the bound physical Pi Session in-place — the
 * physical Pi Session identity SHALL NOT change after compaction, and
 * the compacted transcript SHALL remain visible through the existing
 * session event channel. The runtime executes Pi's native
 * `session.compact()`; it MUST NOT synthesize a summary or fabricate a
 * compaction record when the native call is unavailable or fails —
 * any such failure is surfaced as a `turn-failed` (or `missing-session`
 * if the bound file no longer exists) carrying the underlying error.
 */
export interface PiCompactRequest {
  readonly target: PiSessionTarget
}

/**
 * Compact result. Mirrors the OpenCode `RuntimeCompact` companion shape:
 * `ok: true` with no `runtimeSessionId` (the identity does not change
 * after compaction). Failures carry the existing `PiError` taxonomy
 * (`missing-session` for missing binding with a Reset hint; `conflict`
 * if the underlying turn is still streaming; `turn-failed` for any
 * other Pi-native compaction failure).
 */
export type PiCompactResult = PiResult<PiCompactFacts>

export interface PiCompactFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
}

/**
 * Inputs for a Reset on an idle Pi Session.
 *
 * Reset creates a new empty Pi session file in `workDir` and replaces
 * the binding with the new file path only after it is successfully
 * created. The current model and thinking level are carried onto the
 * new session when available. The runtime SHALL NOT migrate
 * conversation context from the prior Pi Session into the new one;
 * the prior session file remains queryable for audit.
 *
 * Note: Reset is the recovery operation. When the prior session file
 * is missing, Reset still proceeds (skipping the carry-over read) and
 * returns a fresh `runtimeSessionId`. The Server-side grain performs
 * the binding replacement and lineage append using the returned id —
 * the runtime itself does not touch lineage.
 */
export interface PiResetRequest {
  readonly target: PiSessionTarget
}

export interface PiResetFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
}

export type PiResetResult = PiResult<PiResetFacts>
