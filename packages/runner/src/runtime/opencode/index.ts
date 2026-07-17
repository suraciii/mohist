/**
 * Public surface of the OpenCode runtime deep module.
 *
 * Only Mohist-owned types are re-exported. Generated SDK DTOs are
 * implementation details and MUST NOT cross this boundary.
 */

export type {
  RuntimeDiagnostic,
  RuntimeDiagnosticSeverity,
  RuntimeError,
  RuntimeErrorKind,
  RuntimeHealthCheck,
  RuntimeModelCatalog,
  RuntimeModelDescriptor,
  RuntimeReadyState,
  RuntimeResult,
  RuntimeSessionCreateRequest,
  RuntimeSessionCreateResult,
  RuntimeSessionTarget,
} from "./types.js"

export { parseModelIdentifier } from "./model-string.js"
export type { ParsedModelIdentifier, ParseModelResult } from "./model-string.js"

export {
  isNonRecoverableProviderMessage,
  errorKindFor,
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

export {
  getOpenCodeRuntimeFactory,
  setOpenCodeRuntimeFactoryForTest,
  createDefaultOpenCodeRuntime,
} from "./factory.js"
export type { OpenCodeRuntimeFactory } from "./factory.js"
