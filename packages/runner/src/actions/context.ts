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

export type ValidatedActionContext<M extends ActionManifest = ActionManifest> = Omit<ActionContext, "with"> & {
  readonly with: ValidatedWith<M>
}
