import type {
  ActionCatalog,
  ActionCatalogEntry,
  ActionCatalogInput,
  ActionCatalogOutput,
  ActionCatalogError,
  ActionCatalogTombstone,
  ActionDefinition,
  ActionManifest,
  ActionTombstone,
  ResolvedAction,
} from "./manifest.js"
import { validateManifest } from "./define-action.js"

const NAME_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*\/[a-z0-9]+(?:-[a-z0-9]+)*$/

export class ActionRegistryConstructionError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "ActionRegistryConstructionError"
  }
}

export class ActionRegistry {
  private readonly byName = new Map<string, ActionDefinition>()
  private readonly tombstonesByName = new Map<string, ActionTombstone>()
  private readonly catalogValue: ActionCatalog

  constructor(definitions: ReadonlyArray<ActionDefinition>, tombstones: ReadonlyArray<ActionTombstone> = []) {
    for (const definition of definitions) {
      this.addDefinition(definition)
    }
    for (const tombstone of tombstones) {
      this.addTombstone(tombstone)
    }
    this.catalogValue = buildCatalog(definitions, tombstones)
  }

  resolve(uses?: string | null): ResolvedAction {
    const trimmed = uses?.trim()
    if (!trimmed) return { kind: "unknown", canonicalName: "" }
    const key = trimmed.toLowerCase()
    const definition = this.byName.get(key)
    if (definition) {
      return { kind: "definition", definition, canonicalName: definition.manifest.name }
    }
    const tombstone = this.tombstonesByName.get(key)
    if (tombstone) {
      return { kind: "tombstone", tombstone, canonicalName: tombstone.name }
    }
    return { kind: "unknown", canonicalName: key }
  }

  resolveExecutable(uses?: string | null): ActionDefinition | null {
    const resolved = this.resolve(uses)
    return resolved.kind === "definition" ? resolved.definition : null
  }

  resolveTombstone(uses?: string | null): ActionTombstone | null {
    const resolved = this.resolve(uses)
    return resolved.kind === "tombstone" ? resolved.tombstone : null
  }

  definitions(): ReadonlyArray<ActionDefinition> {
    return [...this.byName.values()]
  }

  tombstones(): ReadonlyArray<ActionTombstone> {
    return [...this.tombstonesByName.values()]
  }

  catalog(): ActionCatalog {
    return this.catalogValue
  }

  private addDefinition(definition: ActionDefinition): void {
    if (!definition || typeof definition !== "object") {
      throw new ActionRegistryConstructionError("Action definitions must be non-null objects")
    }
    const manifest = definition.manifest
    if (!manifest || typeof manifest !== "object") {
      throw new ActionRegistryConstructionError("Action definition is missing a manifest")
    }
    try {
      validateManifest(manifest)
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      throw new ActionRegistryConstructionError(message)
    }
    if (typeof manifest.name !== "string" || !NAME_PATTERN.test(manifest.name)) {
      throw new ActionRegistryConstructionError(
        `Action name '${String(manifest.name)}' must match lowercase <namespace>/<action>`,
      )
    }
    const key = manifest.name.toLowerCase()
    if (this.byName.has(key)) {
      throw new ActionRegistryConstructionError(`Duplicate Action name '${manifest.name}'`)
    }
    if (this.tombstonesByName.has(key)) {
      throw new ActionRegistryConstructionError(
        `Executable Action '${manifest.name}' collides with a tombstone`,
      )
    }
    this.byName.set(key, definition)
  }

  private addTombstone(tombstone: ActionTombstone): void {
    if (!tombstone || typeof tombstone !== "object") {
      throw new ActionRegistryConstructionError("Tombstones must be non-null objects")
    }
    if (typeof tombstone.name !== "string" || !NAME_PATTERN.test(tombstone.name)) {
      throw new ActionRegistryConstructionError(
        `Tombstone name '${String(tombstone.name)}' must match lowercase <namespace>/<action>`,
      )
    }
    const key = tombstone.name.toLowerCase()
    if (this.tombstonesByName.has(key)) {
      throw new ActionRegistryConstructionError(`Duplicate tombstone name '${tombstone.name}'`)
    }
    if (this.byName.has(key)) {
      throw new ActionRegistryConstructionError(
        `Tombstone '${tombstone.name}' collides with an executable Action`,
      )
    }
    if (typeof tombstone.guidance !== "string" || tombstone.guidance.length === 0) {
      throw new ActionRegistryConstructionError(`Tombstone '${tombstone.name}' must declare non-empty guidance`)
    }
    this.tombstonesByName.set(key, tombstone)
  }
}

function buildCatalog(definitions: ReadonlyArray<ActionDefinition>, tombstones: ReadonlyArray<ActionTombstone>): ActionCatalog {
  const entries: ActionCatalogEntry[] = definitions
    .map((definition) => projectEntry(definition.manifest))
    .sort((a, b) => a.name.localeCompare(b.name))
  const tombstoneEntries: ActionCatalogTombstone[] = tombstones
    .map((tombstone) => ({ name: tombstone.name, guidance: tombstone.guidance }))
    .sort((a, b) => a.name.localeCompare(b.name))
  return {
    actions: entries,
    tombstones: tombstoneEntries,
  }
}

export { createDefaultRegistry } from "./index.js"
export type { ActionDefinition } from "./manifest.js"

function projectEntry(manifest: ActionManifest): ActionCatalogEntry {
  const inputs: ActionCatalogInput[] = Object.keys(manifest.inputs)
    .filter((name) => manifest.inputs[name]?.engineSource === undefined)
    .sort()
    .map((name) => {
      const declaration = manifest.inputs[name]!
      const projected: { name: string; types: ReadonlyArray<string>; required: boolean; default?: unknown; description?: string } = {
        name,
        types: [...declaration.types],
        required: declaration.required === true,
      }
      if (declaration.default !== undefined) projected.default = declaration.default
      if (declaration.description !== undefined) projected.description = declaration.description
      return projected as ActionCatalogInput
    })
  const outputs: ActionCatalogOutput[] = [...manifest.outputs]
    .map((output) => ({ name: output.name, description: output.description }))
    .sort((a, b) => a.name.localeCompare(b.name))
  const errors: ActionCatalogError[] = [...manifest.errors]
    .map((error) => ({ code: error.code, description: error.description }))
    .sort((a, b) => a.code.localeCompare(b.code))
  const entry: ActionCatalogEntry = {
    name: manifest.name,
    inputs,
    outputs,
    errors,
    ...(manifest.capabilities !== undefined ? { capabilities: [...manifest.capabilities] } : {}),
  }
  if (manifest.description !== undefined) {
    return { ...entry, description: manifest.description }
  }
  return entry
}
