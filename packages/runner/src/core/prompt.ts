import type { JsonObject, JsonValue } from "./types.js"

export type StructuredPrompt = JsonObject

/**
 * Authoritative workflow prompt assembly contract: text prompt specs are used
 * verbatim, structured object specs render as XML through renderStructuredPrompt,
 * and loader specs (`uses` + `with`) dispatch to a PromptLoader whose returned
 * text or object is normalized by the same rule. Consumers that build prompts
 * for LLM input must route through resolvePrompt and must not add markdown
 * wrapping, prefixes, suffixes, or fallback prompt synthesis around its result.
 */
export type PromptSpec = string | StructuredPrompt

export interface PromptLoaderContext {
  with: JsonObject
  workDir: string
  workId: string
  title?: string | null
  stage?: string | null
}

export type PromptLoader = (ctx: PromptLoaderContext) => Promise<string | JsonObject>

export class PromptLoaderRegistry {
  private readonly loaders = new Map<string, PromptLoader>()

  register(name: string, loader: PromptLoader): void {
    const key = name?.trim()
    if (!key) throw new Error("Prompt loader name must be a non-empty string")
    this.loaders.set(key.toLowerCase(), loader)
  }

  unregister(name: string): void {
    if (!name?.trim()) return
    this.loaders.delete(name.toLowerCase())
  }

  resolve(name?: string | null): PromptLoader | undefined {
    if (!name) return undefined
    return this.loaders.get(name.toLowerCase())
  }

  has(name: string): boolean {
    if (!name?.trim()) return false
    return this.loaders.has(name.toLowerCase())
  }
}

const defaultRegistry = new PromptLoaderRegistry()
let activeRegistry: PromptLoaderRegistry = defaultRegistry

export function defaultPromptLoaderRegistry(): PromptLoaderRegistry {
  return defaultRegistry
}

export function setPromptLoaderRegistryForTest(registry: PromptLoaderRegistry | null): void {
  activeRegistry = registry ?? defaultRegistry
}

export async function resolvePrompt(
  spec: JsonValue | null | undefined,
  ctx: PromptLoaderContext,
): Promise<string | undefined> {
  if (spec === undefined || spec === null) return undefined
  if (typeof spec === "string") return spec
  if (typeof spec === "number" || typeof spec === "boolean") {
    throw new Error(`Prompt spec must be a string or object, received ${typeof spec}`)
  }
  if (Array.isArray(spec)) {
    throw new Error("Prompt spec must be a string or object, received an array")
  }

  const usesValue = spec["uses"]
  if (usesValue !== undefined) {
    if (typeof usesValue !== "string" || !usesValue.trim()) {
      throw new Error("Prompt loader spec 'uses' must be a non-empty string")
    }
    const loader = activeRegistry.resolve(usesValue)
    if (!loader) {
      throw new Error(`Unknown prompt loader: '${usesValue}'`)
    }
    const loaderWith = extractLoaderWith(usesValue, spec)
    const result = await loader({ ...ctx, with: loaderWith })
    return normalizeLoaderResult(usesValue, result)
  }

  return renderStructuredPrompt(spec)
}

export function renderStructuredPrompt(value: JsonObject): string {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("Structured prompt must be a JSON object")
  }
  const entries = Object.entries(value)
  if (entries.length !== 1) {
    const keys = entries.map(([key]) => key).join(", ")
    throw new Error(
      `Structured prompt must have exactly one root key, received ${entries.length}${keys ? ` (${keys})` : ""}`,
    )
  }
  const [rootTag, rootValue] = entries[0]
  if (!isValidTagName(rootTag)) {
    throw new Error(`Structured prompt root key must be a valid tag name: '${rootTag}'`)
  }
  return renderBlock(rootTag, rootValue, 0)
}

function extractLoaderWith(loaderName: string, spec: JsonObject): JsonObject {
  const value = spec["with"]
  if (value === undefined || value === null) return {}
  if (typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`Prompt loader '${loaderName}' spec 'with' must be an object`)
  }
  return value
}

function normalizeLoaderResult(loaderName: string, result: unknown): string {
  if (typeof result === "string") return result
  if (result && typeof result === "object" && !Array.isArray(result)) {
    return renderStructuredPrompt(result as JsonObject)
  }
  throw new Error(
    `Prompt loader '${loaderName}' returned an invalid value (expected string or object)`,
  )
}

function renderBlock(tag: string, value: JsonValue, indent: number): string {
  const padStr = pad(indent)
  if (value === null || value === undefined) return `${padStr}<${tag}></${tag}>`
  if (typeof value === "string") return renderPrimitiveBlock(tag, value, indent)
  if (typeof value === "number" || typeof value === "boolean") {
    return renderPrimitiveBlock(tag, String(value), indent)
  }
  if (Array.isArray(value)) return renderArrayBlock(tag, value, indent)
  return renderObjectBlock(tag, value, indent)
}

function renderPrimitiveBlock(tag: string, value: string, indent: number): string {
  const padStr = pad(indent)
  if (!value.includes("\n")) return `${padStr}<${tag}>${value}</${tag}>`
  return `${padStr}<${tag}>\n${value}\n${padStr}</${tag}>`
}

function renderArrayBlock(tag: string, items: JsonValue[], indent: number): string {
  const padStr = pad(indent)
  if (items.length === 0) return `${padStr}<${tag}></${tag}>`
  const lines = items.map((item) => `- ${stringifyArrayItem(tag, item)}`)
  return `${padStr}<${tag}>\n${lines.join("\n")}\n${padStr}</${tag}>`
}

function stringifyArrayItem(tag: string, item: JsonValue): string {
  if (item === null || item === undefined) return ""
  if (typeof item === "string") return item
  if (typeof item === "number" || typeof item === "boolean") return String(item)
  throw new Error(`Structured prompt list '${tag}' supports only primitive items (string, number, boolean)`)
}

function renderObjectBlock(tag: string, block: JsonObject, indent: number): string {
  const padStr = pad(indent)
  const attrs = extractAttrs(tag, block)
  const attrStr = renderAttrs(tag, attrs)
  const children = Object.entries(block).filter(([key]) => key !== "attrs")
  if (children.length === 0) {
    return `${padStr}<${tag}${attrStr}></${tag}>`
  }
  const rendered = children.map(([key, value]) => {
    if (!isValidTagName(key)) {
      throw new Error(`Structured prompt child key must be a valid tag name: '${key}'`)
    }
    return renderBlock(key, value, indent + 2)
  })
  return `${padStr}<${tag}${attrStr}>\n\n${rendered.join("\n\n")}\n\n${padStr}</${tag}>`
}

function extractAttrs(tag: string, block: JsonObject): JsonObject | undefined {
  const value = block["attrs"]
  if (value === undefined || value === null) return undefined
  if (typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`Structured prompt block '${tag}' 'attrs' must be an object`)
  }
  return value
}

function renderAttrs(tag: string, attrs: JsonObject | undefined): string {
  if (!attrs) return ""
  const parts: string[] = []
  for (const [key, value] of Object.entries(attrs)) {
    if (!isValidTagName(key)) {
      throw new Error(`Structured prompt attribute key invalid on '${tag}': '${key}'`)
    }
    parts.push(`${key}="${escapeAttrValue(stringifyAttrValue(tag, key, value))}"`)
  }
  if (parts.length === 0) return ""
  return ` ${parts.join(" ")}`
}

function stringifyAttrValue(tag: string, key: string, value: JsonValue): string {
  if (value === null || value === undefined) return ""
  if (typeof value === "string") return value
  if (typeof value === "number" || typeof value === "boolean") return String(value)
  throw new Error(`Structured prompt attribute '${key}' on '${tag}' must be a string, number, or boolean`)
}

function escapeAttrValue(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/"/g, "&quot;")
}

function pad(indent: number): string {
  return " ".repeat(indent)
}

function isValidTagName(name: string): boolean {
  return typeof name === "string" && /^[A-Za-z_][A-Za-z0-9_-]*$/.test(name)
}
