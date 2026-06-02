import type { JsonObject, JsonValue } from "./types.js"

const MAX_TEMPLATE_PASSES = 5
const ESCAPE_PATTERN = /\\\$\{\{/g
const ESCAPE_SENTINEL = "\u0000LITERAL_DOLLAR_BRACE\u0000"
const REFERENCE_PATTERN = /\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}/g

// Field paths that are never template-rendered. These are general
// (action-agnostic) fields whose value is meant to be a literal string, not
// a template. Loader-internal fields like description/notes/output/etc. are
// not in this list: they are no longer placed in `with` at all (see
// openspec.ts mergeTaskWith), so the renderer never sees them.
const LITERAL_FIELD_PATHS = new Set<string>([
  // Marker text used as a literal search string by core/artifact-exists,
  // core/marker, and mohist/acp-agent — not a file path.
  "expect.markers.*.contains",
])

export function renderTemplate(input: JsonObject | null | undefined, variables: JsonObject) {
  if (!input) return null
  return renderObject(input, variables, "")
}

function renderValue(value: JsonValue, variables: JsonObject, currentPath: string): JsonValue {
  if (isLiteralFieldPath(currentPath)) return value
  if (typeof value === "string") return renderString(value, variables)
  if (Array.isArray(value)) return value.map((item, index) => renderValue(item, variables, appendPath(currentPath, String(index))))
  if (typeof value === "object" && value !== null) {
    return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, renderValue(child, variables, appendPath(currentPath, key))]))
  }
  return value
}

function renderObject(value: JsonObject, variables: JsonObject, currentPath: string): JsonObject {
  return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, renderValue(child, variables, appendPath(currentPath, key))]))
}

function renderString(value: string, variables: JsonObject): JsonValue {
  // Consume \${{ -> sentinel so it survives template expansion as a literal ${{.
  let current = value.replace(ESCAPE_PATTERN, ESCAPE_SENTINEL)

  for (let pass = 0; pass < MAX_TEMPLATE_PASSES; pass += 1) {
    const full = current.match(/^\s*\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$/)
    if (full) {
      const resolved = resolveRequired(variables, full[1])
      if (typeof resolved !== "string") return resolved
      current = resolved
    } else {
      const next = current.replace(REFERENCE_PATTERN, (_, path: string) => templateString(resolveRequired(variables, path)))
      if (next === current) {
        // Restore the escape sentinel to a literal ${{.
        return current.split(ESCAPE_SENTINEL).join("${{")
      }
      current = next
    }
  }

  if (REFERENCE_PATTERN.test(current)) {
    throw new Error("Template variable expansion exceeded maximum depth")
  }
  return current.split(ESCAPE_SENTINEL).join("${{")
}

function resolveRequired(variables: JsonObject, path: string): JsonValue {
  const found = path.split(".").reduce<JsonValue | undefined>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return current[part]
  }, variables)
  if (found === undefined) throw new Error(`Template variable '${path}' was not found`)
  return found
}

function templateString(value: JsonValue) {
  if (value === null) return ""
  if (typeof value === "string") return value
  return JSON.stringify(value)
}

function appendPath(current: string, segment: string): string {
  return current ? `${current}.${segment}` : segment
}

function isLiteralFieldPath(path: string): boolean {
  if (!path) return false
  // Normalize array indices to a wildcard so "expect.markers.0.contains" matches
  // the "expect.markers.*.contains" entry.
  const normalized = path.replace(/\.\d+(\.|$)/g, ".*$1")
  return LITERAL_FIELD_PATHS.has(normalized)
}

// Walks the input and returns every unique ${{ path }} reference found in
// string values. Skips literal-field paths and consumes the \${{ escape. Used
// to surface a clear error before renderTemplate would throw.
export function findTemplateReferences(input: JsonValue | null | undefined): string[] {
  const refs = new Set<string>()
  if (input === null || input === undefined) return []
  walkForReferences(input, "", refs)
  return [...refs]
}

function walkForReferences(value: JsonValue, currentPath: string, refs: Set<string>) {
  if (isLiteralFieldPath(currentPath)) return
  if (typeof value === "string") {
    const unescaped = value.replace(ESCAPE_PATTERN, "")
    for (const match of unescaped.matchAll(REFERENCE_PATTERN)) {
      refs.add(match[1])
    }
    return
  }
  if (Array.isArray(value)) {
    value.forEach((item, index) => walkForReferences(item, appendPath(currentPath, String(index)), refs))
    return
  }
  if (typeof value === "object" && value !== null) {
    for (const [key, child] of Object.entries(value)) {
      walkForReferences(child, appendPath(currentPath, key), refs)
    }
  }
}

export function unresolvedReferences(input: JsonValue | null | undefined, variables: JsonObject): string[] {
  const refs = findTemplateReferences(input)
  return refs.filter((path) => resolvePath(variables, path) === undefined)
}

function resolvePath(variables: JsonObject, path: string): JsonValue | undefined {
  return path.split(".").reduce<JsonValue | undefined>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return current[part]
  }, variables)
}
