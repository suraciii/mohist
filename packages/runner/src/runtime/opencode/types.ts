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

export type RuntimeErrorKind =
  | "invalid-input"
  | "unavailable-runtime"
  | "missing-session"
  | "incompatible-runtime"
  | "permission-required"
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
