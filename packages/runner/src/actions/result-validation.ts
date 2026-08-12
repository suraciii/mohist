import type { ActionError, ActionResult, JsonObject } from "../core/types.js"
import type { ActionManifest, ActionCapabilitySet } from "./manifest.js"
import { RESERVED_PLATFORM_ERROR_CODES, isPlainJsonObject } from "./manifest.js"
import { validateActionOutputShape } from "./action-result.js"
import type { ActionEffects } from "./host.js"

export const MALFORMED_RESULT_ERROR_CODE = "unexpected-error" as const
export const UNDECLARED_RESULT_ERROR_CODE = "unexpected-error" as const

export interface NormalizedOk {
  readonly kind: "ok"
  readonly output: JsonObject | null
  readonly effects: ActionEffects
}

export interface NormalizedError {
  readonly kind: "error"
  readonly error: ActionError
}

export interface MalformedActionResult {
  readonly kind: "malformed"
  readonly message: string
  readonly reason: "result" | "output" | "effects"
}

export type NormalizedActionResult = NormalizedOk | NormalizedError

export function normalizeActionResult(
  result: unknown,
  manifest: ActionManifest,
  capabilitySet: ActionCapabilitySet,
): NormalizedActionResult | MalformedActionResult {
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
    if (Object.prototype.hasOwnProperty.call(obj, "effects")) {
      return malformedEffects("Action result effects are only valid on successful results")
    }
    const error = obj["error"]
    if (!error || typeof error !== "object" || Array.isArray(error)) {
      return malformed(`Action '${manifest.name}' result 'error' must be an object`)
    }
    const code = (error as { code?: unknown }).code
    const message = (error as { message?: unknown }).message
    if (typeof code !== "string" || typeof message !== "string") {
      return malformed(`Action '${manifest.name}' result 'error' must declare string 'code' and 'message'`)
    }
    const effects = extractEffects(obj, manifest, capabilitySet)
    if (effects.kind === "malformed") return effects
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
    return malformedOutput(`Action '${manifest.name}' successful output must be a JSON object or null`)
  }
  const shapeError = validateActionOutputShape(output as JsonObject | null)
  if (shapeError) {
    return malformedOutput(`Action '${manifest.name}' successful output is malformed: ${shapeError}`)
  }
  const effects = extractEffects(obj, manifest, capabilitySet)
  if (effects.kind === "malformed") return effects
  return { kind: "ok", output: (output as JsonObject | null) ?? null, effects: effects.effects }
}

function extractEffects(
  obj: Record<string, unknown>,
  manifest: ActionManifest,
  capabilitySet: ActionCapabilitySet,
): { kind: "ok"; effects: ActionEffects } | MalformedActionResult {
  if (!Object.prototype.hasOwnProperty.call(obj, "effects")) {
    return { kind: "ok", effects: {} }
  }
  const raw = obj["effects"]
  if (!isPlainJsonObject(raw)) {
    return malformedEffects("Action result 'effects' must be a JSON object when present")
  }
  const effectsObj = raw as Record<string, unknown>
  for (const key of Object.keys(effectsObj)) {
    if (key !== "addTasks" && key !== "writeVars") return malformedEffects(`Action result contains unknown effect '${key}'`)
  }

  let addTasks: ActionEffects["addTasks"]
  if ("addTasks" in effectsObj) {
    if (!capabilitySet.has("add-tasks")) {
      return malformedEffects(`Action '${manifest.name}' requested task additions without declaring 'add-tasks' capability`)
    }
    const taskList = effectsObj["addTasks"]
    if (taskList === undefined || taskList === null) {
      addTasks = undefined
    } else if (!Array.isArray(taskList)) {
      return malformedEffects("'effects.addTasks' must be an array")
    } else {
      const validated: ActionEffects["addTasks"] = []
      for (let i = 0; i < taskList.length; i++) {
        const entry = taskList[i]
        if (!isPlainJsonObject(entry)) return malformedEffects(`'effects.addTasks[${i}]' must be an object`)
        const id = (entry as Record<string, unknown>)["id"]
        const title = (entry as Record<string, unknown>)["title"]
        if (typeof id !== "string" || !id.trim()) return malformedEffects(`'effects.addTasks[${i}]' must have a non-empty string 'id'`)
        if (typeof title !== "string" || !title.trim()) return malformedEffects(`'effects.addTasks[${i}]' must have a non-empty string 'title'`)
        const uses = (entry as Record<string, unknown>)["uses"]
        const withObj = (entry as Record<string, unknown>)["with"]
        const expect = (entry as Record<string, unknown>)["expect"]
        if (uses !== undefined && uses !== null && typeof uses !== "string") return malformedEffects(`'effects.addTasks[${i}].uses' must be a string or null`)
        if (withObj !== undefined && withObj !== null && !isPlainJsonObject(withObj)) return malformedEffects(`'effects.addTasks[${i}].with' must be an object or null`)
        if (expect !== undefined && expect !== null && !isPlainJsonObject(expect)) return malformedEffects(`'effects.addTasks[${i}].expect' must be an object or null`)
        validated.push({
          id,
          title,
          uses: uses !== undefined ? uses as string | null : null,
          with: withObj !== undefined ? withObj as JsonObject | null : null,
          expect: expect !== undefined ? expect as JsonObject | null : null,
        })
      }
      addTasks = validated
    }
  }

  let writeVars: ActionEffects["writeVars"]
  if ("writeVars" in effectsObj) {
    if (!capabilitySet.has("write-vars")) {
      return malformedEffects(`Action '${manifest.name}' requested variable writes without declaring 'write-vars' capability`)
    }
    const vars = effectsObj["writeVars"]
    if (vars === undefined || vars === null) {
      writeVars = undefined
    } else if (!isPlainJsonObject(vars)) {
      return malformedEffects("'effects.writeVars' must be a JSON object")
    } else {
      writeVars = vars as JsonObject
    }
  }

  return { kind: "ok", effects: { addTasks, writeVars } }
}

export function malformedToUnexpectedError(message: string): ActionResult {
  return { error: { code: MALFORMED_RESULT_ERROR_CODE, message } }
}

function malformed(message: string): MalformedActionResult {
  return { kind: "malformed", message, reason: "result" }
}

function malformedOutput(message: string): MalformedActionResult {
  return { kind: "malformed", message, reason: "output" }
}

function malformedEffects(message: string): MalformedActionResult {
  return { kind: "malformed", message: message, reason: "effects" }
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

export function passThroughOutcome(result: unknown): "unknown" | undefined {
  if (!result || typeof result !== "object") return undefined
  return (result as { outcome?: unknown }).outcome === "unknown" ? "unknown" : undefined
}
