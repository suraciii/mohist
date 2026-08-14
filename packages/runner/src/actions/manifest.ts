import type { ActionError, ActionResult, JsonObject, JsonValue } from "../core/types.js"
import type { ValidatedWith } from "./context.js"
import type { ActionHostFor } from "./host.js"

export type ActionInputKind = "string" | "number" | "boolean" | "object" | "array"

const CANONICAL_KIND_ORDER: ReadonlyArray<ActionInputKind> = ["string", "number", "boolean", "object", "array"]

export function canonicalKindOrder(): ReadonlyArray<ActionInputKind> {
  return CANONICAL_KIND_ORDER
}

export type ActionCapability = "agent-turn" | "issue-fields" | "workflow-checkpoint" | "add-tasks" | "write-vars"

const VALID_CAPABILITIES: ReadonlyArray<ActionCapability> = [
  "agent-turn",
  "issue-fields",
  "workflow-checkpoint",
  "add-tasks",
  "write-vars",
]

export function validCapabilities(): ReadonlyArray<ActionCapability> {
  return VALID_CAPABILITIES
}

export type InputRenderTiming = "immediate" | "deferred"

export interface ActionInputDeclaration {
  readonly types: ReadonlyArray<ActionInputKind>
  readonly required?: true
  readonly default?: JsonValue
  readonly description?: string
  readonly render?: InputRenderTiming
  readonly engineSource?: "prompts.build"
}

export interface ActionOutputDeclaration {
  readonly name: string
  readonly description?: string
}

export interface ActionErrorDeclaration {
  readonly code: string
  readonly description?: string
}

export interface ActionManifest {
  readonly name: string
  readonly description?: string
  readonly inputs: Readonly<Record<string, ActionInputDeclaration>>
  readonly outputs: ReadonlyArray<ActionOutputDeclaration>
  readonly errors: ReadonlyArray<ActionErrorDeclaration>
  readonly capabilities?: ReadonlyArray<ActionCapability>
}

export interface ActionDefinition<M extends ActionManifest = ActionManifest> {
  readonly manifest: M
  run(inputs: ValidatedWith<M>, host: ActionHostFor<M>): Promise<ActionResult>
}

export type ActionCapabilitySet = ReadonlySet<ActionCapability>

export interface ActionTombstone {
  readonly name: string
  readonly guidance: string
}

export interface ResolvedDefinition<M extends ActionManifest = ActionManifest> {
  readonly kind: "definition"
  readonly definition: ActionDefinition<M>
  readonly canonicalName: string
}

export interface ResolvedTombstone {
  readonly kind: "tombstone"
  readonly tombstone: ActionTombstone
  readonly canonicalName: string
}

export interface ResolvedUnknown {
  readonly kind: "unknown"
  readonly canonicalName: string
}

export type ResolvedAction = ResolvedDefinition | ResolvedTombstone | ResolvedUnknown

export interface ActionCatalogInput {
  readonly name: string
  readonly types: ReadonlyArray<ActionInputKind>
  readonly required: boolean
  readonly default?: JsonValue
  readonly description?: string
}

export interface ActionCatalogOutput {
  readonly name: string
  readonly description?: string
}

export interface ActionCatalogError {
  readonly code: string
  readonly description?: string
}

export interface ActionCatalogEntry {
  readonly name: string
  readonly description?: string
  readonly inputs: ReadonlyArray<ActionCatalogInput>
  readonly outputs: ReadonlyArray<ActionCatalogOutput>
  readonly errors: ReadonlyArray<ActionCatalogError>
  readonly capabilities?: ReadonlyArray<ActionCapability>
}

export interface ActionCatalogTombstone {
  readonly name: string
  readonly guidance: string
}

export interface ActionCatalog {
  readonly actions: ReadonlyArray<ActionCatalogEntry>
  readonly tombstones: ReadonlyArray<ActionCatalogTombstone>
}

export const RESERVED_PLATFORM_ERROR_CODES: ReadonlySet<string> = new Set([
  "invalid-input",
  "unexpected-error",
  "timeout",
])

export interface ValidatedInputSuccess {
  readonly kind: "ok"
  readonly input: JsonObject
}

export interface ValidatedInputFailure {
  readonly kind: "failure"
  readonly error: ActionError
}

export type ValidatedInput = ValidatedInputSuccess | ValidatedInputFailure

export function isActionError(value: unknown): value is ActionError {
  return !!value && typeof value === "object" && typeof (value as ActionError).code === "string" && typeof (value as ActionError).message === "string"
}

export function isPlainJsonObject(value: unknown): value is JsonObject {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return false
  const proto = Object.getPrototypeOf(value)
  return proto === Object.prototype || proto === null
}
