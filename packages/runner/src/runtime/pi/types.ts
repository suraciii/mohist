export type PiDiagnosticSeverity = "info" | "warning" | "error"

export interface PiDiagnostic {
  readonly severity: PiDiagnosticSeverity
  readonly code: string
  readonly message: string
  readonly details?: Record<string, unknown>
}

export type PiErrorKind =
  | "invalid-input"
  | "unavailable-runtime"
  | "missing-session"
  | "incompatible-runtime"
  | "deadline-exceeded"
  | "interrupted"
  | "turn-failed"

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
  readonly runtime: "pi"
  readonly runtimeSessionId: string | null
  readonly workDir: string
}

export interface PiSessionCreateRequest {
  readonly target: PiSessionTarget
}

export interface PiSessionResult {
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface PiTurnOptions {
  readonly model?: string | null
  readonly variant?: string | null
  readonly unknownKeys?: readonly string[]
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
  readonly onEvent?: (event: PiRuntimeEvent) => void
}

export interface PiProviderErrorPolicy {
  readonly nonRecoverablePatterns: readonly RegExp[]
  readonly consecutiveRetryThreshold: number
}
