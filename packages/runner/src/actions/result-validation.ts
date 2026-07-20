import type { ActionError, ActionResult, JsonObject } from "../core/types.js"
import type { ActionManifest } from "./manifest.js"
import { RESERVED_PLATFORM_ERROR_CODES } from "./manifest.js"
import { validateActionOutputShape } from "./action-result.js"

export const MALFORMED_RESULT_ERROR_CODE = "unexpected-error" as const
export const UNDECLARED_RESULT_ERROR_CODE = "unexpected-error" as const

export interface NormalizedOk {
  readonly kind: "ok"
  readonly output: JsonObject | null
}

export interface NormalizedError {
  readonly kind: "error"
  readonly error: ActionError
}

export type NormalizedActionResult = NormalizedOk | NormalizedError

export function normalizeActionResult(
  result: unknown,
  manifest: ActionManifest,
): NormalizedActionResult | { readonly kind: "malformed"; readonly message: string } {
  if (!result || typeof result !== "object") {
    return malformed(`Action '${manifest.name}' returned a non-object result`)
  }
  const obj = result as Record<string, unknown>
  const hasOutput = Object.prototype.hasOwnProperty.call(obj, "output")
  const hasError = Object.prototype.hasOwnProperty.call(obj, "error")
  if (hasOutput === hasError) {
    return malformed(
      `Action '${manifest.name}' result must contain exactly one of 'output' or 'error'; received both or neither`,
    )
  }
  if (hasError) {
    const error = obj["error"]
    if (!error || typeof error !== "object" || Array.isArray(error)) {
      return malformed(`Action '${manifest.name}' result 'error' must be an object`)
    }
    const code = (error as { code?: unknown }).code
    const message = (error as { message?: unknown }).message
    if (typeof code !== "string" || typeof message !== "string") {
      return malformed(`Action '${manifest.name}' result 'error' must declare string 'code' and 'message'`)
    }
    if (RESERVED_PLATFORM_ERROR_CODES.has(code)) {
      return {
        kind: "error",
        error: { code, message },
      }
    }
    const declared = new Set(manifest.errors.map((entry) => entry.code))
    if (!declared.has(code)) {
      return {
        kind: "error",
        error: {
          code: UNDECLARED_RESULT_ERROR_CODE,
          message,
        },
      }
    }
    return { kind: "error", error: { code, message } }
  }
  const output = obj["output"]
  if (output !== null && (typeof output !== "object" || Array.isArray(output))) {
    return malformed(`Action '${manifest.name}' successful output must be a JSON object or null`)
  }
  const shapeError = validateActionOutputShape(output as JsonObject | null)
  if (shapeError) {
    return malformed(`Action '${manifest.name}' successful output is malformed: ${shapeError}`)
  }
  return { kind: "ok", output: (output as JsonObject | null) ?? null }
}

export function malformedToUnexpectedError(message: string): ActionResult {
  return { error: { code: MALFORMED_RESULT_ERROR_CODE, message } }
}

function malformed(message: string): { readonly kind: "malformed"; readonly message: string } {
  return { kind: "malformed", message }
}

export function expectedResultCodes(manifest: ActionManifest): ReadonlySet<string> {
  const codes = new Set<string>(RESERVED_PLATFORM_ERROR_CODES)
  for (const entry of manifest.errors) codes.add(entry.code)
  return codes
}

export function passThroughExitCode(result: unknown): number | null | undefined {
  if (!result || typeof result !== "object") return undefined
  if (!Object.prototype.hasOwnProperty.call(result, "exitCode")) return undefined
  const value = (result as { exitCode?: unknown }).exitCode
  return typeof value === "number" ? value : undefined
}

export function passThroughTurnFact(result: unknown): unknown {
  if (!result || typeof result !== "object") return undefined
  if (!Object.prototype.hasOwnProperty.call(result, "turnFact")) return undefined
  return (result as { turnFact?: unknown }).turnFact
}
