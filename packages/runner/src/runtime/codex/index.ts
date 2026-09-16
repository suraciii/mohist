/**
 * Public surface of the Codex runtime deep module.
 *
 * Only Mohist-owned types are re-exported. App-server JSON-RPC
 * envelopes and DTOs are implementation details and MUST NOT cross
 * this boundary. Callers depend on these shapes only.
 */

export type {
  CodexCatalog,
  CodexCancelFacts,
  CodexCancelRequest,
  CodexCancelResult,
  CodexClock,
  CodexCompactFacts,
  CodexCompactRequest,
  CodexCompactResult,
  CodexDiagnostic,
  CodexDiagnosticSeverity,
  CodexError,
  CodexErrorKind,
  CodexFilePart,
  CodexFollowupFacts,
  CodexFollowupRequest,
  CodexFollowupResult,
  CodexModelDescriptor,
  CodexReadyState,
  CodexResetFacts,
  CodexResetRequest,
  CodexResetResult,
  CodexResult,
  CodexSessionTarget,
  CodexTurnFacts,
  CodexTurnOptions,
  CodexTurnRequest,
  CodexTurnResult,
  CodexCanonicalReasoningEffort,
  CodexNativeReasoningEffort,
} from './types.js'

export {
  CODEX_CANONICAL_REASONING_EFFORTS,
  CODEX_NATIVE_REASONING_EFFORTS,
  CODEX_DEFAULT_TIMEOUTS,
  CODEX_SUPPORTED_VERSION_RANGE,
} from './types.js'

export {
  errorKindForCodex,
  normalizeDeadlineExceededCodex,
  normalizeIncompatibleRuntimeCodex,
  normalizeInterruptedCodex,
  normalizeInvalidInputCodex,
  normalizeMissingSessionCodex,
  normalizePermissionRequiredCodex,
  normalizeTurnFailedCodex,
  normalizeUnavailableRuntimeCodex,
  normalizeUnknownCodex,
  normalizeUnsupportedExecutionConfigurationCodex,
  UNSUPPORTED_EXECUTION_CONFIGURATION_CATEGORY,
} from './errors.js'
export type { RawCodexError } from './errors.js'

export { CodexRuntime } from './runtime.js'
export type { CodexRuntimeDeps } from './runtime.js'

export {
  getCodexRuntimeFactory,
  createDefaultCodexRuntime,
} from './factory.js'
export type { CodexRuntimeFactory } from './factory.js'

export {
  buildCodexThreadResumeRequest,
  buildCodexThreadStartRequest,
  isStructuredThreadNotFound,
  resumeThread,
  startThread,
  type CodexThreadStartInput,
  type CodexThreadStartOutcome,
  type CodexThreadTransport,
} from './thread.js'

export {
  buildLostTurnStartUnknown,
  driveTurnToCompletion,
  submitTurnStart,
  type CodexRuntimeTurnEvent,
  type CodexTurnCompletionConfig,
  type CodexTurnCompletionOptions,
  type CodexTurnEventObserver,
  type CodexTurnStartOutcome,
  type CodexTurnStartSubmission,
  type CodexTurnTransport,
} from './turn.js'
