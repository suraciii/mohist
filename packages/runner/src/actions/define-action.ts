import type {
  ActionDefinition,
  ActionErrorDeclaration,
  ActionInputDeclaration,
  ActionInputKind,
  ActionManifest,
  ActionOutputDeclaration,
} from "./manifest.js"
import { RESERVED_PLATFORM_ERROR_CODES, canonicalKindOrder } from "./manifest.js"
import type { ValidatedActionContext } from "./context.js"
import type { JsonValue } from "../core/types.js"

const NAME_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*\/[a-z0-9]+(?:-[a-z0-9]+)*$/
const CODE_PATTERN = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/
const OUTPUT_NAME_PATTERN = /^[a-zA-Z_][a-zA-Z0-9_-]*$/

export class ActionDefinitionError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "ActionDefinitionError"
  }
}

interface DefineActionInput {
  readonly manifest: ActionManifest
  readonly run: ActionDefinition["run"]
}

export function defineAction<M extends ActionManifest>(input: DefineActionInput): ActionDefinition<M> {
  validateManifest(input.manifest)
  if (typeof input.run !== "function") {
    throw new ActionDefinitionError(`Action '${input.manifest.name}' must provide an execution function`)
  }
  const frozenManifest = deepFreezeManifest(input.manifest)
  const definition = {
    manifest: frozenManifest,
    run: async (context: ValidatedActionContext<M>) => input.run(context),
  } as ActionDefinition<M>
  Object.freeze(definition)
  return definition
}

export function validateManifest(manifest: ActionManifest): void {
  if (!manifest || typeof manifest !== "object") {
    throw new ActionDefinitionError("Action manifest must be a non-null object")
  }
  if (typeof manifest.name !== "string" || !NAME_PATTERN.test(manifest.name)) {
    throw new ActionDefinitionError(
      `Action name '${String(manifest.name)}' must match lowercase <namespace>/<action> with kebab-case segments`,
    )
  }
  if (manifest.description !== undefined && typeof manifest.description !== "string") {
    throw new ActionDefinitionError(`Action '${manifest.name}' description must be a string when provided`)
  }
  if (!manifest.inputs || typeof manifest.inputs !== "object" || Array.isArray(manifest.inputs)) {
    throw new ActionDefinitionError(`Action '${manifest.name}' must declare an inputs record`)
  }
  const canonical = canonicalKindOrder()
  const seenNames = new Set<string>()
  for (const [name, declaration] of Object.entries(manifest.inputs)) {
    if (seenNames.has(name)) {
      throw new ActionDefinitionError(`Action '${manifest.name}' declares duplicate input '${name}'`)
    }
    seenNames.add(name)
    validateInputDeclaration(manifest.name, name, declaration, canonical)
  }
  if (!Array.isArray(manifest.outputs)) {
    throw new ActionDefinitionError(`Action '${manifest.name}' must declare an outputs array`)
  }
  const outputNames = new Set<string>()
  for (const output of manifest.outputs) {
    validateOutputDeclaration(manifest.name, output, outputNames)
  }
  if (!Array.isArray(manifest.errors)) {
    throw new ActionDefinitionError(`Action '${manifest.name}' must declare an errors array`)
  }
  const errorCodes = new Set<string>()
  for (const error of manifest.errors) {
    validateErrorDeclaration(manifest.name, error, errorCodes)
  }
}

function validateInputDeclaration(
  actionName: string,
  inputName: string,
  declaration: ActionInputDeclaration,
  canonical: ReadonlyArray<ActionInputKind>,
): void {
  if (inputName === "working-directory") {
    throw new ActionDefinitionError(`Action '${actionName}' must not declare engine-reserved input 'working-directory'`)
  }
  if (!declaration || typeof declaration !== "object" || Array.isArray(declaration)) {
    throw new ActionDefinitionError(`Action '${actionName}' input '${inputName}' declaration must be an object`)
  }
  const types = declaration.types
  if (!Array.isArray(types) || types.length === 0) {
    throw new ActionDefinitionError(`Action '${actionName}' input '${inputName}' must declare a non-empty types array`)
  }
  const seen = new Set<ActionInputKind>()
  for (const kind of types) {
    if (!canonical.includes(kind as ActionInputKind)) {
      throw new ActionDefinitionError(
        `Action '${actionName}' input '${inputName}' declares unsupported kind '${String(kind)}'; supported kinds: ${canonical.join(", ")}`,
      )
    }
    if (seen.has(kind as ActionInputKind)) {
      throw new ActionDefinitionError(`Action '${actionName}' input '${inputName}' declares duplicate kind '${String(kind)}'`)
    }
    seen.add(kind as ActionInputKind)
  }
  const orderedKinds = [...types].sort((a, b) => canonical.indexOf(a as ActionInputKind) - canonical.indexOf(b as ActionInputKind))
  if (orderedKinds.some((kind, index) => kind !== types[index])) {
    throw new ActionDefinitionError(
      `Action '${actionName}' input '${inputName}' types must use canonical order ${canonical.join(", ")}`,
    )
  }
  if (declaration.required === true && declaration.default !== undefined) {
    throw new ActionDefinitionError(`Action '${actionName}' input '${inputName}' must not be both required and defaulted`)
  }
  if (declaration.default !== undefined) {
    if (declaration.default === null) {
      throw new ActionDefinitionError(`Action '${actionName}' input '${inputName}' default must not be null`)
    }
    const defaultKind = jsonKindOf(declaration.default)
    if (defaultKind === null || !seen.has(defaultKind)) {
      throw new ActionDefinitionError(
        `Action '${actionName}' input '${inputName}' default must be one of the declared kinds ${[...seen].join(", ")}, received ${defaultKind ?? typeof declaration.default}`,
      )
    }
  }
  if (declaration.description !== undefined && typeof declaration.description !== "string") {
    throw new ActionDefinitionError(`Action '${actionName}' input '${inputName}' description must be a string when provided`)
  }
}

function validateOutputDeclaration(actionName: string, output: ActionOutputDeclaration, seen: Set<string>): void {
  if (!output || typeof output !== "object" || Array.isArray(output)) {
    throw new ActionDefinitionError(`Action '${actionName}' output declaration must be an object`)
  }
  if (typeof output.name !== "string" || !OUTPUT_NAME_PATTERN.test(output.name)) {
    throw new ActionDefinitionError(`Action '${actionName}' output name '${String(output.name)}' is invalid`)
  }
  if (seen.has(output.name)) {
    throw new ActionDefinitionError(`Action '${actionName}' declares duplicate output '${output.name}'`)
  }
  seen.add(output.name)
  if (output.description !== undefined && typeof output.description !== "string") {
    throw new ActionDefinitionError(`Action '${actionName}' output '${output.name}' description must be a string when provided`)
  }
}

function validateErrorDeclaration(actionName: string, error: ActionErrorDeclaration, seen: Set<string>): void {
  if (!error || typeof error !== "object" || Array.isArray(error)) {
    throw new ActionDefinitionError(`Action '${actionName}' error declaration must be an object`)
  }
  if (typeof error.code !== "string" || !CODE_PATTERN.test(error.code)) {
    throw new ActionDefinitionError(`Action '${actionName}' error code '${String(error.code)}' must be lowercase kebab-case`)
  }
  if (RESERVED_PLATFORM_ERROR_CODES.has(error.code)) {
    throw new ActionDefinitionError(
      `Action '${actionName}' declares reserved platform error code '${error.code}' as a business error`,
    )
  }
  if (seen.has(error.code)) {
    throw new ActionDefinitionError(`Action '${actionName}' declares duplicate error code '${error.code}'`)
  }
  seen.add(error.code)
  if (error.description !== undefined && typeof error.description !== "string") {
    throw new ActionDefinitionError(`Action '${actionName}' error '${error.code}' description must be a string when provided`)
  }
}

function jsonKindOf(value: unknown): ActionInputKind | null {
  if (typeof value === "string") return "string"
  if (typeof value === "number") return "number"
  if (typeof value === "boolean") return "boolean"
  if (Array.isArray(value)) return "array"
  if (value !== null && typeof value === "object") {
    const proto = Object.getPrototypeOf(value)
    if (proto === Object.prototype || proto === null) return "object"
  }
  return null
}

function deepFreezeManifest(manifest: ActionManifest): ActionManifest {
  const inputs: Record<string, ActionInputDeclaration> = {}
  for (const [name, declaration] of Object.entries(manifest.inputs)) {
    const frozenTypes = Object.freeze([...declaration.types] as ReadonlyArray<ActionInputKind>)
    const clonedDefault = declaration.default === undefined ? undefined : cloneJsonValue(declaration.default)
    inputs[name] = Object.freeze({
      types: frozenTypes,
      required: declaration.required,
      default: clonedDefault,
      description: declaration.description,
    })
  }
  const outputs = manifest.outputs.map((output) =>
    Object.freeze({ name: output.name, description: output.description }) as ActionOutputDeclaration,
  )
  const errors = manifest.errors.map((error) =>
    Object.freeze({ code: error.code, description: error.description }) as ActionErrorDeclaration,
  )
  const frozen = Object.freeze({
    name: manifest.name,
    description: manifest.description,
    inputs: Object.freeze(inputs) as Readonly<Record<string, ActionInputDeclaration>>,
    outputs: Object.freeze(outputs),
    errors: Object.freeze(errors),
  } as ActionManifest)
  return frozen
}

function cloneJsonValue(value: JsonValue): JsonValue {
  if (value === null || typeof value !== "object") return value
  if (Array.isArray(value)) return value.map((entry) => cloneJsonValue(entry))
  const result: Record<string, JsonValue> = {}
  for (const [key, child] of Object.entries(value as Record<string, JsonValue>)) {
    result[key] = cloneJsonValue(child)
  }
  return result
}
