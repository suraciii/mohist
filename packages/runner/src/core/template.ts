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
//
// The `expect` completion contract now lives beside `with` (a separate
// render root, not nested under `with.expect`). The matchers below are
// listed both with the `expect.` prefix and without so that legacy
// callers rendering `with.expect` and the new contract rendering
// `expect` standalone both honor the literal-field rule.
const LITERAL_FIELD_PATHS = new Set<string>([
  // Marker text used as a literal search string by core/artifact-exists,
  // core/marker, and the Workflow completion evaluator — not a file path.
  "expect.markers.*.contains",
  "markers.*.contains",
  // Marker accepted-value list (string entries). Each entry is a literal
  // promise shape that must not be template-rendered (templates would
  // mangle `<promise>PASS</promise>` syntax).
  "expect.markers.*.oneOf.*",
  "markers.*.oneOf.*",
])

export function renderTemplate(input: JsonObject | null | undefined, variables: JsonObject) {
  if (!input) return null
  if (typeof input !== "object" || Array.isArray(input)) {
    throw new Error("renderTemplate expects a JSON object, received " + (Array.isArray(input) ? "array" : typeof input))
  }
  return renderObject(input, variables, "")
}

/**
 * Render every top-level field of `input` except those in `skip`, which are
 * passed through untouched. Each rendered value is produced by `renderValue`,
 * which dispatches by JSON kind (string → `renderString`, object/array →
 * recurse). Callers MUST NOT pass a non-object value to `renderTemplate`;
 * doing so would iterate the value's own entries (e.g. a string's character
 * indices) and silently corrupt it into `{"0":"a","1":"b",…}`.
 */
export function renderWithSkippedFields(
  input: JsonObject | null | undefined,
  variables: JsonObject,
  skip: ReadonlySet<string>,
): JsonObject | null {
  if (!input) return null
  if (skip.size === 0) return renderObject(input, variables, "")
  const rendered: JsonObject = {}
  for (const [key, value] of Object.entries(input)) {
    rendered[key] = skip.has(key) ? value : renderValue(value, variables, appendPath("", key))
  }
  return rendered
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

  const seenValues = new Set<string>()
  for (let pass = 0; pass < MAX_TEMPLATE_PASSES; pass += 1) {
    if (seenValues.has(current)) throw new Error("Template variable expansion cycle detected")
    seenValues.add(current)
    const full = current.match(/^\s*\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$/)
    if (full) {
      const resolved = resolvePath(variables, full[1])
      if (resolved === undefined) {
        throw new Error(`Template variable '${full[1]}' was not found`)
      }
      if (typeof resolved !== "string") return resolved
      current = resolved
    } else {
      let resolvedAny = false
      const next = current.replace(REFERENCE_PATTERN, (match, path: string) => {
        const resolved = resolvePath(variables, path)
        if (resolved === undefined) {
          throw new Error(`Template variable '${path}' was not found`)
        }
        if (isObjectOrArray(resolved)) {
          throw new Error(`Template variable '${path}' resolves to an object or array and cannot be embedded in a string`)
        }
        resolvedAny = true
        return templateString(resolved)
      })
      if (next === current || !resolvedAny) {
        return next.split(ESCAPE_SENTINEL).join("${{")
      }
      current = next
    }
  }

  if (REFERENCE_PATTERN.test(current)) {
    throw new Error("Template variable expansion exceeded maximum depth")
  }
  return current.split(ESCAPE_SENTINEL).join("${{")
}

function templateString(value: JsonValue) {
  if (value === null) return ""
  if (typeof value === "string") return value
  return String(value)
}

function isObjectOrArray(value: JsonValue): value is JsonValue[] | { [key: string]: JsonValue } {
  return Array.isArray(value) || (typeof value === "object" && value !== null)
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
    for (const match of unescaped.matchAll(new RegExp(REFERENCE_PATTERN.source, "g"))) {
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

// Retained for callers that need to distinguish whole-value references. Strict
// rendering uses unresolvedReferences so embedded references are checked too.
export function wholeStringUnresolvedReferences(input: JsonValue | null | undefined, variables: JsonObject): string[] {
  const unresolved = new Set<string>()
  if (input === null || input === undefined) return []
  walkForWholeStringUnresolved(input, "", variables, unresolved)
  return [...unresolved]
}

function walkForWholeStringUnresolved(
  value: JsonValue,
  currentPath: string,
  variables: JsonObject,
  unresolved: Set<string>,
) {
  if (isLiteralFieldPath(currentPath)) return
  if (typeof value === "string") {
    const unescaped = value.replace(ESCAPE_PATTERN, "")
    const full = unescaped.match(/^\s*\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$/)
    if (full && resolvePath(variables, full[1]) === undefined) {
      unresolved.add(full[1])
    }
    return
  }
  if (Array.isArray(value)) {
    value.forEach((item, index) => walkForWholeStringUnresolved(item, appendPath(currentPath, String(index)), variables, unresolved))
    return
  }
  if (typeof value === "object" && value !== null) {
    for (const [key, child] of Object.entries(value)) {
      walkForWholeStringUnresolved(child, appendPath(currentPath, key), variables, unresolved)
    }
  }
}

function resolvePath(variables: JsonObject, path: string): JsonValue | undefined {
  const result = path.split(".").reduce<JsonValue | undefined>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return current[part]
  }, variables)
  return result
}
