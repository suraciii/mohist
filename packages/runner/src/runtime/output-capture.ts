import type { ActionResult, JsonObject, JsonValue, TaskOutputDefinition } from "../core/types.js"

export function captureOutputs(
  outputs: readonly TaskOutputDefinition[] | undefined | null,
  actionResult: ActionResult,
): Record<string, JsonValue> | undefined {
  if (!outputs || outputs.length === 0) return undefined
  if (!isSuccessStatus(actionResult.status)) return undefined

  const parsed = parseActionOutput(actionResult.output)
  if (parsed === undefined) return undefined

  const root: JsonObject = { output: parsed }
  const captured: Record<string, JsonValue> = {}

  for (const definition of outputs) {
    const path = definition.from.split(".").filter((part) => part.length > 0)
    if (path.length === 0) continue

    const value = readValueAt(root, path)
    if (value !== undefined) {
      captured[definition.name] = value
    }
  }

  return Object.keys(captured).length > 0 ? captured : undefined
}

function parseActionOutput(output: string | null | undefined): JsonValue | undefined {
  if (!output?.trim()) return undefined
  try {
    return JSON.parse(output) as JsonValue
  } catch {
    return undefined
  }
}

function isSuccessStatus(status: string): boolean {
  const normalized = status.toLowerCase()
  return ["completed", "success", "succeeded", "pass", "passed"].includes(normalized)
}

function readValueAt(value: JsonValue | undefined, path: string[]): JsonValue | undefined {
  let current: JsonValue | undefined = value
  for (const part of path) {
    if (current === null || typeof current !== "object" || Array.isArray(current)) {
      return undefined
    }
    current = (current as JsonObject)[part]
  }
  return current
}
