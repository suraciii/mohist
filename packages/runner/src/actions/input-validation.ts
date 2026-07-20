import type { JsonObject, JsonValue } from "../core/types.js"
import {
  type ActionInputKind,
  type ActionManifest,
  type ValidatedInput,
  canonicalKindOrder,
} from "./manifest.js"

export const ENGINE_RESERVED_INPUT_KEY = "working-directory"

export function validateActionInput(
  manifest: ActionManifest,
  renderedWith: JsonObject | null | undefined,
): ValidatedInput {
  const raw = renderedWith ?? {}

  const declared = new Set(Object.keys(manifest.inputs))
  const unknownFields: string[] = []
  for (const fieldName of Object.keys(raw)) {
    if (fieldName === ENGINE_RESERVED_INPUT_KEY) continue
    if (!declared.has(fieldName)) unknownFields.push(fieldName)
  }
  if (unknownFields.length > 0) {
    const first = [...unknownFields].sort()[0]!
    return invalidInput(`Action '${manifest.name}' received unknown input '${first}'`)
  }

  const missingRequired: string[] = []
  for (const [name, declaration] of Object.entries(manifest.inputs)) {
    if (declaration.required === true && !Object.prototype.hasOwnProperty.call(raw, name)) {
      missingRequired.push(name)
    }
  }
  if (missingRequired.length > 0) {
    const first = [...missingRequired].sort()[0]!
    return invalidInput(`Action '${manifest.name}' input '${first}' is required`)
  }

  const validated: JsonObject = {}
  const fieldOrder = sortedFieldKeys(raw, manifest)
  for (const fieldName of fieldOrder) {
    if (fieldName === ENGINE_RESERVED_INPUT_KEY) continue
    const declaration = manifest.inputs[fieldName]
    if (!declaration) continue
    if (!Object.prototype.hasOwnProperty.call(raw, fieldName)) continue
    const value = raw[fieldName]
    if (value === null) {
      return invalidInput(
        `Action '${manifest.name}' input '${fieldName}' must be ${formatKinds(declaration.types)}, received null`,
      )
    }
    if (!declaration.types.some((kind) => matchesKind(kind, value))) {
      return invalidInput(
        `Action '${manifest.name}' input '${fieldName}' must be ${formatKinds(declaration.types)}, received ${actualKindLabel(value)}`,
      )
    }
    validated[fieldName] = value
  }

  for (const [name, declaration] of Object.entries(manifest.inputs)) {
    if (Object.prototype.hasOwnProperty.call(validated, name)) continue
    if (declaration.default !== undefined) {
      validated[name] = cloneJson(declaration.default) as JsonValue
    }
  }

  return { kind: "ok", input: validated }
}

function sortedFieldKeys(raw: JsonObject, manifest: ActionManifest): string[] {
  const keys = new Set<string>()
  for (const key of Object.keys(raw)) keys.add(key)
  for (const key of Object.keys(manifest.inputs)) keys.add(key)
  return [...keys].sort()
}

function matchesKind(kind: ActionInputKind, value: unknown): boolean {
  switch (kind) {
    case "string":
      return typeof value === "string"
    case "number":
      return typeof value === "number" && Number.isFinite(value)
    case "boolean":
      return typeof value === "boolean"
    case "object":
      return value !== null && typeof value === "object" && !Array.isArray(value)
    case "array":
      return Array.isArray(value)
  }
}

function formatKinds(kinds: ReadonlyArray<ActionInputKind>): string {
  return kinds.length === 1 ? kinds[0]! : kinds.join(" or ")
}

function actualKindLabel(value: unknown): string {
  if (value === null) return "null"
  if (Array.isArray(value)) return "array"
  if (typeof value === "object") return "object"
  return typeof value
}

function invalidInput(message: string): ValidatedInput {
  return { kind: "failure", error: { code: "invalid-input", message } }
}

function cloneJson(value: JsonValue | undefined): JsonValue | undefined {
  if (value === undefined) return undefined
  return structuredClone(value)
}

export function engineReservedWorkDirKey(): typeof ENGINE_RESERVED_INPUT_KEY {
  return ENGINE_RESERVED_INPUT_KEY
}

export function canonicalKindList(): ReadonlyArray<ActionInputKind> {
  return canonicalKindOrder()
}
