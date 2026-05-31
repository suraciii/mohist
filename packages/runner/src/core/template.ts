import type { JsonObject, JsonValue } from "./types.js"

const MAX_TEMPLATE_PASSES = 5

export function renderTemplate(input: JsonObject | null | undefined, variables: JsonObject) {
  if (!input) return null
  return Object.fromEntries(Object.entries(input).map(([key, value]) => [key, renderValue(value, variables)]))
}

function renderValue(value: JsonValue, variables: JsonObject): JsonValue {
  if (typeof value === "string") return renderString(value, variables)
  if (Array.isArray(value)) return value.map((item) => renderValue(item, variables))
  if (typeof value === "object" && value !== null) return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, renderValue(child, variables)]))
  return value
}

function renderString(value: string, variables: JsonObject): JsonValue {
  let current = value
  for (let pass = 0; pass < MAX_TEMPLATE_PASSES; pass += 1) {
    const full = current.match(/^\s*\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$/)
    if (full) {
      const resolved = resolveRequired(variables, full[1])
      if (typeof resolved !== "string") return resolved
      current = resolved
    } else {
      const next = current.replace(/\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}/g, (_, path: string) => templateString(resolveRequired(variables, path)))
      if (next === current) return current
      current = next
    }
  }

  if (/\$\{\{\s*[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*\s*\}\}/.test(current)) {
    throw new Error("Template variable expansion exceeded maximum depth")
  }
  return current
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
