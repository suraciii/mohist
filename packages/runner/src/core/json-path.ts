import type { JsonObject, JsonValue } from "./types.js"

export function getSegments(obj: unknown, segments: readonly string[]): unknown {
  let current: unknown = obj
  for (const part of segments) {
    if (current === null || typeof current !== "object" || Array.isArray(current)) return undefined
    current = (current as Record<string, unknown>)[part]
  }
  return current
}

export function getPath(obj: unknown, path: string): unknown {
  return getSegments(obj, path.split("."))
}

export function stringAt(obj: unknown, segments: readonly string[]): string | undefined {
  const found = getSegments(obj, segments)
  return typeof found === "string" ? found : undefined
}

export function setPath(obj: JsonObject, path: string, value: JsonValue): void {
  const segments = path.split(".")
  let current: Record<string, unknown> = obj
  for (let i = 0; i < segments.length - 1; i++) {
    const segment = segments[i]
    const next = current[segment]
    if (next === null || next === undefined || typeof next !== "object" || Array.isArray(next)) {
      current[segment] = {}
    }
    current = current[segment] as Record<string, unknown>
  }
  current[segments[segments.length - 1]] = value
}
