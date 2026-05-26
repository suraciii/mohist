import type { JsonObject, JsonValue } from "./types.js"

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
  const full = value.match(/^\s*\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$/)
  if (full) return resolveRequired(variables, full[1])
  return value.replace(/\$\{\{\s*([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}/g, (_, path: string) => templateString(resolveRequired(variables, path)))
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
