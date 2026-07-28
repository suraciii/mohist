import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionHost } from "./host.js"
import { isObject, numberInput, stringInput } from "../core/json.js"
import { resolvePrompt } from "../core/prompt.js"
import { parseModelIdentifier } from "../runtime/opencode/index.js"
import { actionErrorMessage, fail } from "./action-result.js"

export const OPENCODE_USES = "mohist/opencode"

export const DEFAULT_TURN_DEADLINE_MS = 60 * 60 * 1000

export async function opencodeAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult> {
  if (!host.agent) {
    return fail("runtime-unavailable", "mohist/opencode requires the agent-turn capability")
  }

  let prompt: string | undefined
  try {
    prompt = await resolvePrompt(inputs.prompt, { with: {}, workDir: host.workDir, workId: "", title: null, stage: null })
  } catch (error) {
    return fail("invalid-input", actionErrorMessage(error))
  }
  if (typeof prompt !== "string" || !prompt.trim()) {
    return fail("invalid-input", "mohist/opencode requires 'prompt' that resolves to non-empty text")
  }

  const optionsParse = parseOpencodeInput(inputs as unknown as JsonObject)
  if (optionsParse.kind === "failure") return optionsParse.result
  const options = optionsParse.options

  const session = stringInput(inputs, "session")
  const deadlineMs = numberInput(inputs, "timeout") || DEFAULT_TURN_DEADLINE_MS

  return await host.agent.turn({
    prompt,
    session: session ?? undefined,
    options,
    deadlineMs,
  })
}

export function composeOpencodePrompt(prompt: string, parentIssueContext?: { title: string; body: string | null } | null): string {
  if (!parentIssueContext) return prompt
  const parent = JSON.stringify({ title: parentIssueContext.title, body: parentIssueContext.body })
  return `Parent issue context (read-only background; JSON):\n${parent}\n\nTreat the parent issue context above as read-only background. The current child issue body is authoritative and controls delivery scope.\n\n${prompt}`
}

export interface OpencodeOptions {
  model?: string
  variant?: string
  skills?: readonly string[]
  instructions?: string | null
}

type OptionsParse =
  | { kind: "ok", options: OpencodeOptions | undefined }
  | { kind: "failure", result: ActionResult }

export function parseOpencodeInput(withInput: JsonObject | null): OptionsParse {
  if (!withInput) return { kind: "ok", options: undefined }
  const rawOptions = withInput["options"]
  if (rawOptions === undefined || rawOptions === null) return { kind: "ok", options: undefined }
  if (!isObject(rawOptions)) {
    return {
      kind: "failure",
      result: fail("invalid-input", "mohist/opencode 'options' must be an object when present"),
    }
  }
  return parseOpencodeOptions(rawOptions as Record<string, unknown>)
}

type ParsedOptions =
  | { kind: "ok", options: OpencodeOptions }
  | { kind: "failure", result: ActionResult }

function parseOpencodeOptions(raw: Record<string, unknown>): ParsedOptions {
  const options: OpencodeOptions = {}
  if ("model" in raw) {
    const value = raw["model"]
    if (value === null || value === undefined) {
    } else if (typeof value !== "string") {
      return {
        kind: "failure",
        result: fail("invalid-input", "mohist/opencode 'options.model' must be a string when present"),
      }
    } else {
      const parsed = parseModelIdentifier(value)
      if (parsed.kind === "failure") {
        return { kind: "failure", result: fail("invalid-input", `mohist/opencode ${parsed.message}`) }
      }
      options.model = value.trim()
    }
  }
  if ("variant" in raw) {
    const value = raw["variant"]
    if (value === null || value === undefined) {
    } else if (typeof value !== "string") {
      return {
        kind: "failure",
        result: fail("invalid-input", "mohist/opencode 'options.variant' must be a string when present"),
      }
    } else {
      options.variant = value
    }
  }
  if ("instructions" in raw && raw.instructions !== null && raw.instructions !== undefined) {
    if (typeof raw.instructions !== "string") return { kind: "failure", result: fail("invalid-input", "mohist/opencode 'options.instructions' must be a string when present") }
    options.instructions = raw.instructions
  }
  if ("skills" in raw) {
    const value = raw.skills
    if (!Array.isArray(value) || value.some((skill) => typeof skill !== "string")) return { kind: "failure", result: fail("invalid-input", "mohist/opencode 'options.skills' must be an array of strings when present") }
    options.skills = value
  }
  return { kind: "ok", options }
}

export { type PromptLoaderContext } from "../core/prompt.js"
