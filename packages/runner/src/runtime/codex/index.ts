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
} from './errors.js'
export type { RawCodexError } from './errors.js'

export { CodexRuntime } from './runtime.js'
export type { CodexRuntimeDeps } from './runtime.js'

export {
  getCodexRuntimeFactory,
  getCodexServerFactory,
  createDefaultCodexRuntime,
} from './factory.js'
export type { CodexRuntimeFactory } from './factory.js'

export {
  performCodexInitialization,
  codexInitializationTransportFromHandle,
} from './initialization.js'
export type {
  CodexInitializationOptions,
  CodexInitializationOutcome,
  CodexInitializationTransport,
} from './initialization.js'

export {
  evaluateCodexReadiness,
  isCodexVersionSupported,
} from './readiness.js'
export type {
  CodexAuthenticationProbe,
  CodexCatalogLoader,
  CodexCliProbe,
  CodexReadinessOptions,
  CodexReadinessOutcome,
  CodexReadinessProbe,
} from './readiness.js'

export {
  maskCodexCredentialString,
  redactCodexCredentialString,
  redactCodexCredentialEnvelope,
  redactCodexCredentialDiagnostic,
  redactCodexCredentialStringWithIndex,
  codexCredentialSecretDigest,
  CODEX_CREDENTIAL_MASK_PLACEHOLDER,
} from './credential.js'
export type { CodexCredentialSecretIndexEntry } from './credential.js'

export {
  createSpawnedCodexServer,
  DEFAULT_CODEX_STARTUP_TIMEOUT_MS,
  DEFAULT_CODEX_SHUTDOWN_TIMEOUT_MS,
} from './server-process.js'
export type {
  CodexServerFactory,
  CodexServerFactoryOptions,
  CodexServerHandle,
} from './server-process.js'

export {
  CODEX_APPROVAL_POLICY,
  CODEX_SANDBOX_POLICY,
  CODEX_LOCKED_METHODS,
  isCodexLockedMethod,
} from './protocol-types.js'
export type {
  CodexLockedMethod,
  CodexInitializeParams,
  CodexInitializeResult,
  CodexInitializedParams,
  CodexThreadStartParams,
  CodexThreadStartResult,
  CodexThreadResumeParams,
  CodexThreadResumeResult,
  CodexThreadCompactStartParams,
  CodexThreadCompactStartResult,
  CodexTurnStartParams,
  CodexTurnStartResult,
  CodexTurnInputItem,
  CodexTurnSteerParams,
  CodexTurnInterruptParams,
  CodexTurnInterruptResult,
  CodexModelListParams,
  CodexModelListResult,
  CodexJsonRpcMessage,
  CodexJsonRpcRequest,
  CodexJsonRpcSuccess,
  CodexJsonRpcError,
  CodexJsonRpcNotification,
  CodexJsonRpcServerRequest,
  CodexItemEvent,
  CodexTurnCompletedEvent,
  CodexTurnCompletedStatus,
  CodexThreadStatusEvent,
  CodexServerRequestParams,
} from './protocol-types.js'
export {
  isCodexInitializeRequest,
  isCodexInitializeResult,
  isCodexThreadStartRequest,
  isCodexThreadStartResult,
  isCodexThreadResumeRequest,
  isCodexThreadCompactStartRequest,
  isCodexTurnStartRequest,
  isCodexTurnSteerRequest,
  isCodexTurnInterruptRequest,
  isCodexTurnInterruptResult,
  isCodexTurnInputItem,
  isCodexModelListResult,
  isCodexServerRequest,
  isCodexItemEvent,
  isCodexTurnCompletedEvent,
  isCodexThreadStatusEvent,
} from './protocol-types.js'
