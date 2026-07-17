import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { isObject } from "../core/json.js"
import { resolvePrompt } from "../core/prompt.js"
import { runCommand } from "../system/process.js"
import { buildPromptLoaderContext } from "./acp/agent-config.js"
import { emitSessionEvent } from "./acp/session-events.js"
import { runAcpWorkflowAgentSession } from "./acp/session-strategies.js"

export const OPENCODE_USES = "mohist/opencode"

export interface OpencodeOptions {
  model?: string
  variant?: string
}

export interface OpencodeValidatedInput {
  prompt: string
  session?: string
  options?: OpencodeOptions
}

type OptionsParse =
  | { kind: "ok", options: OpencodeOptions | undefined }
  | { kind: "failure", result: ActionResult }

async function restoreAgentToolNoise(context: ActionContext) {
  const log = context.log
  const lineOptions = log ? { onLine: (line: string) => log.write("action:opencode", line) } : undefined
  for (const path of [".opencode/package-lock.json", ".opencode/bun.lock", ".opencode/node_modules/.package-lock.json"]) {
    try {
      await runCommand("git", ["checkout", "--", path], context.workDir, context.signal, undefined, lineOptions)
    } catch {
      // Tool-noise cleanup must never turn a successful agent run into a failure.
    }
  }
}

/**
 * `mohist/opencode` Action contract (opencode-action-contract spec).
 *
 * Input shape is exactly:
 *   - `prompt`: required non-empty string after template resolution
 *   - `session`: optional logical session name (used by the existing
 *     ACP Session runtime underneath)
 *   - `options`: optional `{ model, variant }` object; `model` is
 *     `provider/model-id` (the model ID may contain additional `/`
 *     characters — the first `/` separates provider from model ID),
 *     `variant` is a sibling optional string.
 *
 * The Action MUST NOT require `agent`, `kind`, `type`, or any other
 * Workflow completion field. `expect` is a task-level completion
 * contract and never reaches the Action; the executor owns that
 * evaluation.
 *
 * The bridge handler validates the input then delegates turn execution
 * to the existing ACP runtime underneath (the ACP process still exists
 * while the native OpenCode SDK runtime is delivered by the sibling
 * issue). The native SDK replaces this handler; the contract on this
 * side does not change.
 *
 * Output projection: the bridge returns a rich diagnostic JSON in
 * `output` for debug-time inspection only. The Workflow task executor's
 * `projectTaskOutput` step discards that JSON and projects the public
 * Action Output to `null | { promise: <value> }` per the
 * opencode-action-contract spec scenario "Runtime and completion facts
 * stay out of OpenCode Action Output". Callers MUST NOT rely on the
 * shape of `output` returned here; treat it as internal debug state.
 */
export async function opencodeAction(context: ActionContext): Promise<ActionResult> {
  let prompt: string | undefined
  try {
    prompt = await resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))
  } catch (error) {
    return { status: "failure", message: error instanceof Error ? error.message : String(error) }
  }
  if (typeof prompt !== "string" || !prompt.trim()) {
    return { status: "failure", message: "mohist/opencode requires 'prompt' that resolves to non-empty text" }
  }

  const optionsParse = parseOpencodeInput(context.with ?? null)
  if (optionsParse.kind === "failure") return optionsParse.result
  const options = optionsParse.options

  const turnContext = withOpencodeAgentBinding(context, options)
  const silenceMissingModelWarning = options?.model === undefined
  const result = await runAcpWorkflowAgentSession(turnContext, prompt, { silenceMissingModelWarning })
  await restoreAgentToolNoise(turnContext)

  const ok = result.success
  const failureCategory = ok ? null : result.failureCategory ?? null
  const failureReason = ok ? null : result.error ?? "OpenCode agent task failed"
  if (turnContext.ownerKind !== "agent-job" || !turnContext.agentSessionId) {
    await emitSessionEvent(
      turnContext,
      "session.closed",
      { status: ok ? "completed" : "failed", failureReason, failureCategory, exitCode: result.exitCode ?? (ok ? 0 : 1) },
      result.acpSessionId ?? null,
    )
  }

  return {
    status: ok ? "success" : "failure",
    message: ok ? "OpenCode agent task completed" : failureReason,
    output: JSON.stringify({
      kind: "opencode",
      status: ok ? "success" : "failure",
      runtimeSessionId: result.acpSessionId,
      model: options?.model ?? null,
      variant: options?.variant ?? null,
      text: result.text,
      error: result.error,
      providerError: result.providerError,
    }),
    exitCode: result.exitCode ?? (ok ? 0 : 1),
    turnFact: { finalAssistantText: result.text ?? null },
  }
}

/**
 * Resolve and validate the `mohist/opencode` Action input shape.
 *
 * Rules:
 *   - `options` MUST be an object when present.
 *   - `options.model` MUST be a string when present; the value MUST
 *     match `provider/model-id` (provider non-empty before the first
 *     `/`, model-id non-empty after it; additional `/` are allowed in
 *     the model-id portion).
 *   - `options.variant` MUST be a string when present and MUST NOT
 *     be appended to or parsed from the model identifier.
 *   - Unknown option keys (e.g. legacy `type`, liveness settings) are
 *     ignored with a diagnostic — they MUST NOT make an otherwise
 *     valid turn fail.
 *
 * Exposed for tests so the validator can be exercised independently
 * of the ACP turn.
 */
export function parseOpencodeInput(withInput: JsonObject | null): OptionsParse {
  if (!withInput) return { kind: "ok", options: undefined }
  const rawOptions = withInput["options"]
  if (rawOptions === undefined || rawOptions === null) return { kind: "ok", options: undefined }
  if (!isObject(rawOptions)) {
    return {
      kind: "failure",
      result: { status: "failure", message: "mohist/opencode 'options' must be an object when present" },
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
      // Treat explicit null/undefined as "not set".
    } else if (typeof value !== "string") {
      return {
        kind: "failure",
        result: { status: "failure", message: "mohist/opencode 'options.model' must be a string when present" },
      }
    } else {
      const trimmed = value.trim()
      if (!trimmed) {
        return {
          kind: "failure",
          result: { status: "failure", message: "mohist/opencode 'options.model' must be a non-empty 'provider/model' string" },
        }
      }
      const split = trimmed.match(/^([^/\s]+)\/(\S+)$/)
      if (!split) {
        return {
          kind: "failure",
          result: { status: "failure", message: "mohist/opencode 'options.model' must be 'provider/model' (provider and model-id required)" },
        }
      }
      options.model = trimmed
    }
  }
  if ("variant" in raw) {
    const value = raw["variant"]
    if (value === null || value === undefined) {
      // Treat explicit null/undefined as "not set".
    } else if (typeof value !== "string") {
      return {
        kind: "failure",
        result: { status: "failure", message: "mohist/opencode 'options.variant' must be a string when present" },
      }
    } else {
      options.variant = value
    }
  }
  return { kind: "ok", options }
}

/**
 * Build the ActionContext the underlying ACP runtime expects. The
 * `mohist/opencode` contract carries `options`; the ACP runtime
 * reads `with.agent` (and the legacy `with.model`/`with.variant`
 * fallbacks). The bridge translates `options.model`/`options.variant`
 * into the equivalent `with.agent` shape without duplicating fields
 * — `variant` is a sibling and MUST NOT be appended to the model ID.
 */
function withOpencodeAgentBinding(context: ActionContext, options: OpencodeOptions | undefined): ActionContext {
  if (!options || (options.model === undefined && options.variant === undefined)) {
    return context
  }
  const agent: JsonObject = {}
  if (options.model !== undefined) agent["model"] = options.model
  if (options.variant !== undefined) agent["variant"] = options.variant
  return {
    ...context,
    with: { ...(context.with ?? {}), agent },
  }
}