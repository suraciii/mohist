import type { ActionContext, JsonObject, JsonValue } from "../core/types.js"
import type { ActionInputKind, ActionManifest } from "./manifest.js"

export type InferInputShape<M extends ActionManifest> = {
  readonly [K in keyof M["inputs"]]: InferInputValue<M["inputs"][K]>
}

export type InferInputValue<D> = D extends { required: true }
  ? InferKind<D extends { types: ReadonlyArray<infer T extends ActionInputKind> } ? T : never>
  : InferKind<D extends { types: ReadonlyArray<infer T extends ActionInputKind> } ? T : never> | undefined

export type InferKind<T extends ActionInputKind> = T extends "string"
  ? string
  : T extends "number"
    ? number
    : T extends "boolean"
      ? boolean
      : T extends "object"
        ? JsonObject
        : T extends "array"
          ? ReadonlyArray<JsonValue>
          : never

export type ValidatedWith<M extends ActionManifest> = M extends ActionManifest
  ? { readonly [K in keyof M["inputs"]]: InferInputValue<M["inputs"][K]> }
  : never

/**
 * Variable-free Action invocation context. Every field is engine-owned
 * host context (workflow/run identity, work metadata, the resolved
 * working directory, dispatch metadata used by existing capabilities)
 * or the validated `with` payload. The Runner projects this narrower
 * shape from the wider engine `ActionContext` so a built-in or custom
 * Action cannot observe `variables` at compile time or runtime.
 */
export type ActionInvocationContext = Omit<ActionContext, "with" | "variables"> & {
  readonly with: JsonObject
}

/**
 * Manifest-typed variant of {@link ActionInvocationContext}. The Runner
 * substitutes the validated `with` shape for the selected manifest
 * before calling `ActionDefinition.run`.
 */
export type ValidatedActionContext<M extends ActionManifest = ActionManifest> = Omit<ActionInvocationContext, "with"> & {
  readonly with: JsonObject & ValidatedWith<M>
}

export function isVariableFreeActionContext(value: unknown): value is ActionInvocationContext {
  if (!value || typeof value !== "object") return false
  const record = value as Record<string, unknown>
  if ("variables" in record) return false
  return typeof record["workDir"] === "string" && typeof record["workflowRunId"] === "string"
}
