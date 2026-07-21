import { ActionRegistry } from "./registry.js"
import { BUILT_IN_ACTION_TOMBSTONES, builtInActions } from "./built-ins.js"

export function createDefaultRegistry(): ActionRegistry {
  return new ActionRegistry(builtInActions, BUILT_IN_ACTION_TOMBSTONES)
}

export { ActionRegistry } from "./registry.js"
export { defineAction, ActionDefinitionError } from "./define-action.js"
export {
  type ActionCatalog,
  type ActionCatalogEntry,
  type ActionCatalogError,
  type ActionCatalogInput,
  type ActionCatalogOutput,
  type ActionCatalogTombstone,
  type ActionDefinition,
  type ActionErrorDeclaration,
  type ActionInputDeclaration,
  type ActionInputKind,
  type ActionManifest,
  type ActionOutputDeclaration,
  type ActionTombstone,
  type ResolvedAction,
  type ResolvedDefinition,
  type ResolvedTombstone,
  type ResolvedUnknown,
  type ValidatedInput,
  RESERVED_PLATFORM_ERROR_CODES,
  canonicalKindOrder,
} from "./manifest.js"
export type { ValidatedActionContext, ValidatedWith, InferInputShape } from "./context.js"
export { validateActionInput, ENGINE_RESERVED_INPUT_KEY, engineReservedWorkDirKey, canonicalKindList } from "./input-validation.js"
export {
  normalizeActionResult,
  malformedToUnexpectedError,
  expectedResultCodes,
  passThroughExitCode,
  passThroughTurnFact,
  MALFORMED_RESULT_ERROR_CODE,
  UNDECLARED_RESULT_ERROR_CODE,
} from "./result-validation.js"
export { builtInActions, BUILT_IN_ACTION_TOMBSTONES, ACP_AGENT_TOMBSTONE, builtInActionNames } from "./built-ins.js"
