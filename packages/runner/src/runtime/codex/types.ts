/**
 * Mohist-owned boundary types for the Codex runtime deep module.
 *
 * The runtime drives `codex app-server` over a line-framed JSON-RPC v2
 * channel; every type that crosses the module boundary is owned by
 * Mohist, not the generated app-server DTO. This keeps protocol drift
 * contained to one module — callers depend only on these shapes, and
 * the app-server protocol is an implementation detail inside the
 * module.
 *
 * The Codex Thread ID is persisted as the existing opaque
 * `runtimeSessionId`. The current Codex Turn ID is volatile Runtime
 * correlation used to route events and interrupt the exact Turn; it
 * never crosses the boundary as a Mohist identity and never reaches
 * persistence.
 */

/**
 * `reasoningEffort` canonical ↔ native mapping.
 *
 * Canonical Mohist values map onto Codex's native effort names inside
 * the runtime; the cross-boundary types speak only the canonical
 * vocabulary. Native `none` ↔ canonical `off`; `minimal`, `low`,
 * `medium`, `high`, `xhigh`, `max` map by exact name. Unknown native
 * values remain diagnostics.
 */
export const CODEX_CANONICAL_REASONING_EFFORTS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] as const
export type CodexCanonicalReasoningEffort = (typeof CODEX_CANONICAL_REASONING_EFFORTS)[number]

export const CODEX_NATIVE_REASONING_EFFORTS = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] as const
export type CodexNativeReasoningEffort = (typeof CODEX_NATIVE_REASONING_EFFORTS)[number]

export type CodexDiagnosticSeverity = 'info' | 'warning' | 'error'

export interface CodexDiagnostic {
  readonly severity: CodexDiagnosticSeverity
  readonly code: string
  readonly message: string
  readonly details?: Record<string, unknown>
}

export type CodexErrorKind =
  | 'invalid-input'
  | 'unavailable-runtime'
  | 'missing-session'
  | 'incompatible-runtime'
  | 'unsupported-execution-configuration'
  | 'permission-required'
  | 'deadline-exceeded'
  | 'interrupted'
  | 'turn-failed'
  | 'unknown'

export interface CodexError {
  readonly kind: CodexErrorKind
  readonly message: string
  readonly diagnostics: readonly CodexDiagnostic[]
}

export type CodexResult<T> =
  | { readonly ok: true; readonly value: T; readonly diagnostics: readonly CodexDiagnostic[] }
  | { readonly ok: false; readonly error: CodexError; readonly diagnostics: readonly CodexDiagnostic[] }

/**
 * Inputs for a Codex turn on a Thread bound to the AgentSession. The
 * runtime owns the app-server DTO construction and the per-turn
 * application of `model` / `reasoningEffort`; callers pass only
 * Mohist-owned shapes.
 *
 * `runtimeSessionId` carries the current logical Session's physical
 * binding. `null` means "no current binding — create a new Thread in
 * `workDir`". A persisted binding whose Thread cannot be restored is
 * surfaced as `missing-session` (the runtime never silently rotates to
 * a fresh Thread to fabricate continuous context).
 */
export interface CodexSessionTarget {
  readonly runtime: 'codex'
  readonly runtimeSessionId: string | null
  readonly workDir: string
}

/**
 * Native file part delivered through the Turn input array. The runtime
 * carries them as Codex image / local-image entries alongside the text
 * part. Attachments always have the workspace file as the source of
 * truth; the file part is additive so the model can see the image
 * directly when the runtime supports it.
 */
export interface CodexFilePart {
  readonly mime: string
  readonly filename: string
  /** Data URL or workspace-relative path resolved through the runner content route. */
  readonly url: string
}

/**
 * Optional per-turn execution configuration. `variant` is rejected at
 * execution start as `unsupported-execution-configuration` (Codex v1
 * has no variant). Unknown keys are surfaced as diagnostics.
 */
export interface CodexTurnOptions {
  readonly model?: string | null
  readonly reasoningEffort?: CodexCanonicalReasoningEffort | null
  /** Codex v1 has no variant; a configured value is rejected at turn/start. */
  readonly variant?: string | null
  readonly unknownKeys?: readonly string[]
}

export interface CodexTurnRequest {
  readonly target: CodexSessionTarget
  readonly prompt: string
  readonly deadlineMs?: number | null
  readonly options?: CodexTurnOptions | null
  readonly fileParts?: readonly CodexFilePart[] | null
}

export interface CodexTurnFacts {
  readonly finalAssistantText: string | null
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface CodexTurnResult {
  readonly facts: CodexTurnFacts
  readonly diagnostics: readonly CodexDiagnostic[]
}

/**
 * Inputs for a Follow-up on a Codex-bound AgentSession. The runtime
 * resolves the persisted binding first; a stale binding surfaces as
 * `missing-session` (the existing Reset hint). The runtime routes
 * active-Turn follow-ups through `turn/steer` with the frozen
 * `expectedTurnId`; idle follow-ups queue a new `turn/start` on the
 * same Thread.
 */
export interface CodexFollowupRequest {
  readonly target: CodexSessionTarget
  readonly prompt: string
  readonly options?: CodexTurnOptions | null
  readonly fileParts?: readonly CodexFilePart[] | null
}

export interface CodexFollowupFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly finalAssistantText: string | null
}

export interface CodexFollowupResult {
  readonly facts: CodexFollowupFacts
  readonly diagnostics: readonly CodexDiagnostic[]
}

/**
 * Inputs for a Cancel against an active Codex Thread turn. The runtime
 * resolves the binding first; a stale binding surfaces as
 * `missing-session`. The `cancelled: true` flag records the abort
 * attempt; `stopConfirmed` records whether the matching terminal event
 * confirmed interruption.
 */
export interface CodexCancelRequest {
  readonly target: CodexSessionTarget
}

export interface CodexCancelFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
  readonly cancelled: true
  readonly stopConfirmed: boolean
}

export interface CodexCancelResult {
  readonly facts: CodexCancelFacts
  readonly diagnostics: readonly CodexDiagnostic[]
}

/**
 * Inputs for a Compact against an idle Codex Thread. Compact runs only
 * while idle and uses `thread/compact/start`. The matching compaction
 * Turn must reach `turn/completed` and emit a `contextCompaction` item
 * before the runtime reports success.
 */
export interface CodexCompactRequest {
  readonly target: CodexSessionTarget
}

export interface CodexCompactFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface CodexCompactResult {
  readonly facts: CodexCompactFacts
  readonly diagnostics: readonly CodexDiagnostic[]
}

/**
 * Inputs for a Reset on an idle Codex Thread. Reset creates an empty
 * Thread in the same working directory, then atomically replaces the
 * binding. The previous transcript is never replayed into the new
 * Thread.
 */
export interface CodexResetRequest {
  readonly target: CodexSessionTarget
}

export interface CodexResetFacts {
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface CodexResetResult {
  readonly facts: CodexResetFacts
  readonly diagnostics: readonly CodexDiagnostic[]
}

/**
 * Locked Codex model descriptor exposed across the boundary. Reasoning
 * effort values are canonical — native mapping is an internal concern
 * of `model-catalog.ts`. Variants are never published in v1.
 */
export interface CodexModelDescriptor {
  readonly id: string
  readonly displayName: string | null
  readonly reasoningEfforts: readonly CodexCanonicalReasoningEffort[]
  readonly defaultReasoningEffort: CodexCanonicalReasoningEffort | null
  readonly supportsReasoningEffort: true
}

export interface CodexCatalog {
  readonly models: readonly CodexModelDescriptor[]
  readonly complete: boolean
  readonly capabilityRevision: string
}

export interface CodexReadyState {
  readonly ready: boolean
  readonly diagnostic: CodexDiagnostic | null
  readonly catalog: CodexCatalog | null
  readonly generation: number | null
}

/**
 * Optional clock seam used by readiness, the deadline scheduler, and
 * the bounded shutdown. Production uses `Date.now()` / `setTimeout`;
 * tests inject a deterministic clock.
 */
export interface CodexClock {
  readonly now: () => number
  readonly setTimeout: (callback: () => void, delayMs: number) => unknown
  readonly clearTimeout: (handle: unknown) => void
}

export const CODEX_DEFAULT_TIMEOUTS = Object.freeze({
  startupMs: 10_000,
  shutdownMs: 5_000,
  cancelConfirmationMs: 5_000,
  closeoutWarningMs: 5 * 60_000,
})

export const CODEX_SUPPORTED_VERSION_RANGE = Object.freeze({
  min: '0.153.0',
  max: '0.154.0',
} as const)
