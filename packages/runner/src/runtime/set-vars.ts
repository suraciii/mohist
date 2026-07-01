import type { JsonObject, JsonValue, WorkItemResult } from "../core/types.js"
import { getPath, setPath } from "../core/json-path.js"

export interface SetVarsPatcher {
  patchRunVars(workflowRunId: string, vars: JsonObject, signal: AbortSignal): Promise<unknown>
}

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
    const value = getPath(outputObj, resolvedPath)
    if (value === undefined) {
      return { vars: null, error: `setVars source path '${sourcePath}' not found in task output` }
    }
    setPath(result, targetPath, value as JsonValue)
  }

  return { vars: result }
}

/**
 * Apply an extracted {@link SetVarsResult} to the server via the
 * provided patcher. Returns `null` when the extraction has nothing to
 * patch (no vars), letting the caller leave the task result unchanged.
 * On patch failure returns a `failed` {@link WorkItemResult} that the
 * executor merges into its result.
 */
export async function applyExtractedSetVars(
  patcher: SetVarsPatcher,
  workflowRunId: string,
  extraction: SetVarsResult,
  signal: AbortSignal,
): Promise<{ status: "failed"; message: string } | null> {
  if (!extraction.vars) return null
  try {
    await patcher.patchRunVars(workflowRunId, extraction.vars, signal)
    return null
  } catch (error) {
    return {
      status: "failed",
      message: `setVars patch failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
    }
  }
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
