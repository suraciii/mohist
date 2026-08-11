/**
 * Public surface of the OpenCode runtime deep module.
 *
 * Only Mohist-owned types are re-exported. Generated SDK DTOs are
 * implementation details and MUST NOT cross this boundary.
 */

export type {
  RuntimeCancelFacts,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeDiagnostic,
  RuntimeDiagnosticSeverity,
  RuntimeError,
  RuntimeErrorKind,
  RuntimeFollowupFacts,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeHealthCheck,
  RuntimeProviderErrorPolicy,
  RuntimeReadyState,
  RuntimeResult,
  RuntimeSessionCreateRequest,
  RuntimeSessionCreateResult,
  RuntimeSessionTarget,
  RuntimeTurnFacts,
  RuntimeTurnEvent,
  RuntimeTurnObserver,
  RuntimeTurnOptions,
  RuntimeTurnRequest,
  RuntimeTurnResult,
  RuntimeFilePart,
  RuntimeOwnershipSnapshot,
} from "./types.js"

export { parseModelIdentifier } from "./model-string.js"
export type { ParsedModelIdentifier, ParseModelResult } from "./model-string.js"

export {
  DEFAULT_PROVIDER_ERROR_POLICY,
  hasUnconfirmedCleanup,
  isNonRecoverableProviderMessage,
  isNonRecoverableProviderRetry,
  errorKindFor,
  normalizeAbortUnconfirmed,
  normalizeDeadlineExceeded,
  normalizeInterrupted,
  normalizeInvalidInput,
  normalizeIncompatibleRuntime,
  normalizeMissingSession,
  normalizePermissionRequired,
  normalizeTurnFailed,
  normalizeUnavailableRuntime,
} from "./errors.js"

export { OpenCodeRuntime } from "./runtime.js"
export type { OpenCodeRuntimeDeps } from "./runtime.js"
export type { RuntimeClock } from "./runtime.js"

export type {
  DirectoryReclaimResult,
  DirectoryReleaseOutcome,
  DirectoryReleaseResult,
} from "./directory-instance.js"

export type { WorkspaceRemovalFenceResult } from "../workspace-removal-fence.js"

export {
  getOpenCodeRuntimeFactory,
  setOpenCodeRuntimeFactoryForTest,
  createDefaultOpenCodeRuntime,
} from "./factory.js"
export type { OpenCodeRuntimeFactory } from "./factory.js"
