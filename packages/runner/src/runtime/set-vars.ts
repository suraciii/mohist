import type { JsonObject, JsonValue } from "../core/types.js"
import { getPath, setPath } from "../core/json-path.js"

export interface SetVarsResult {
  vars: JsonObject | null
  error?: string
}

export function extractSetVars(
  setVars: Record<string, string> | null | undefined,
  output: JsonValue | null | undefined,
): SetVarsResult {
  if (!setVars || Object.keys(setVars).length === 0) return { vars: null }

  if (output === null || output === undefined) {
    return { vars: null, error: "task output is null; cannot project setVars source paths" }
  }
  if (typeof output !== "object" || Array.isArray(output)) {
    return { vars: null, error: "task output is not a JSON object" }
  }

  const outputObj = output as JsonObject
  const result: JsonObject = {}

  for (const [targetPath, sourcePath] of Object.entries(setVars)) {
    const resolvedPath = stripOutputPrefix(sourcePath)
    const value = getPath(outputObj, resolvedPath)
    if (value === undefined) {
      return { vars: null, error: `setVars source path '${sourcePath}' not found in task output` }
    }
    setPath(result, targetPath, value as JsonValue)
  }

  return { vars: result }
}

function stripOutputPrefix(path: string): string {
  const prefix = "output."
  return path.startsWith(prefix) ? path.slice(prefix.length) : path
}
