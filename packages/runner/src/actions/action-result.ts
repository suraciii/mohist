import type { ActionError, ActionResult, JsonObject, JsonValue } from "../core/types.js"

type ActionFacts = Pick<ActionResult, "exitCode" | "turnFact">

export function succeed(output: JsonObject | null = null, facts: ActionFacts = {}): ActionResult {
  return { output, ...facts }
}

export function fail(code: string, message: string, facts: ActionFacts = {}): ActionResult {
  return { error: { code, message }, ...facts }
}

export function actionErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

export function isActionFailure(result: ActionResult): result is Extract<ActionResult, { error: ActionError }> {
  return "error" in result
}

/**
 * Validates a successful Action result's `output` shape against the
 * Workflow Action contract: object root or `null`, every leaf a JSON
 * value (no `undefined`, functions, `bigint`, cycles, or non-finite
 * numbers). Returns `null` on success or an actionable message on
 * failure. Shared by task and check execution so the rule lives in
 * exactly one place.
 */
export function validateActionOutputShape(output: unknown): string | null {
  const path: string[] = []
  const seen = new WeakSet<object>()
  if (!validateJsonValue(output, path, seen)) {
    return `successful Action output must be a JSON object or null. Offending value at path '${path.join(".") || "<root>"}'.`
  }
  return null
}

function validateJsonValue(value: unknown, path: string[], seen: WeakSet<object>): boolean {
  if (value === null) return true
  if (typeof value === "string" || typeof value === "boolean") return true
  if (typeof value === "number") return Number.isFinite(value)
  if (typeof value === "undefined" || typeof value === "function" || typeof value === "bigint") return false
  if (typeof value === "object") {
    if (seen.has(value as object)) return false
    seen.add(value as object)
    if (Array.isArray(value)) {
      for (let i = 0; i < value.length; i++) {
        path.push(`[${i}]`)
        if (!validateJsonValue(value[i], path, seen)) return false
        path.pop()
      }
      return true
    }
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      path.push(key)
      if (!validateJsonValue(child, path, seen)) return false
      path.pop()
    }
    return true
  }
  return false
}

export type { JsonValue }
