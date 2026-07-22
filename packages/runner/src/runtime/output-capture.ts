import type { ActionResult, JsonObject, JsonValue, DispatchWorkItem } from "../core/types.js"
import { isActionFailure } from "../actions/action-result.js"
import { isObject } from "../core/json.js"

export function captureOutputs(
  outputs: DispatchWorkItem["outputs"],
  actionResult: ActionResult,
): JsonObject | undefined {
  if (isActionFailure(actionResult)) return undefined
  if (!outputs || outputs.length === 0) return undefined
  if (actionResult.output === null || actionResult.output === undefined) return undefined
  if (!isObject(actionResult.output)) return undefined

  const captured: JsonObject = {}
  for (const output of outputs) {
    const value = readPath({ output: actionResult.output }, output.from.split("."))
    if (value !== undefined) captured[output.name] = value as JsonValue
  }

  return Object.keys(captured).length > 0 ? captured : undefined
}

function readPath(value: unknown, path: string[]): unknown {
  let current = value
  for (const part of path) {
    if (current === null || typeof current !== "object" || Array.isArray(current)) return undefined
    current = (current as Record<string, unknown>)[part]
  }
  return current
}
