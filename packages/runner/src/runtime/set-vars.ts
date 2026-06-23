import type { JsonObject, JsonValue } from "../core/types.js"

export interface SetVarsResult {
  vars: JsonObject | null
  error?: string
}

export function extractSetVars(
  setVars: Record<string, string> | null | undefined,
  output: string | null | undefined,
): SetVarsResult {
  if (!setVars || Object.keys(setVars).length === 0) return { vars: null }

  const parsed = parseOutput(output)
  if (parsed === null) {
    return { vars: null, error: "task output is empty or not valid JSON" }
  }
  if (typeof parsed !== "object" || Array.isArray(parsed)) {
    return { vars: null, error: "task output is not a JSON object" }
  }

  const outputObj = parsed as JsonObject
  const result: JsonObject = {}

  for (const [targetPath, sourcePath] of Object.entries(setVars)) {
    const resolvedPath = stripOutputPrefix(sourcePath)
    const value = readPath(outputObj, resolvedPath)
    if (value === undefined) {
      return { vars: null, error: `setVars source path '${sourcePath}' not found in task output` }
    }
    setPath(result, targetPath, value)
  }

  return { vars: result }
}

function stripOutputPrefix(path: string): string {
  const prefix = "output."
  return path.startsWith(prefix) ? path.slice(prefix.length) : path
}

function parseOutput(output: string | null | undefined): JsonValue | null {
  if (!output || !output.trim()) return null
  try {
    return JSON.parse(output) as JsonValue
  } catch {
    return null
  }
}

function readPath(obj: JsonObject, path: string): JsonValue | undefined {
  const segments = path.split(".")
  let current: unknown = obj
  for (const segment of segments) {
    if (current === null || typeof current !== "object" || Array.isArray(current)) return undefined
    current = (current as Record<string, unknown>)[segment]
  }
  return current as JsonValue | undefined
}

function setPath(obj: JsonObject, path: string, value: JsonValue): void {
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
