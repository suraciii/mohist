import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { isObject, numberInput } from "../core/json.js"
import { resolvePrompt } from "../core/prompt.js"
import { buildPromptLoaderContext, sessionNameFromContext } from "./opencode-helpers.js"
import { parseModelIdentifier } from "../runtime/opencode/index.js"
import type { OpenCodeRuntime } from "../runtime/opencode/index.js"
import { actionErrorMessage, fail, succeed } from "./action-result.js"

export const OPENCODE_USES = "mohist/opencode"

export const DEFAULT_TURN_DEADLINE_MS = 60 * 60 * 1000

export interface OpencodeOptions {
  model?: string
  variant?: string
}

type OptionsParse =
  | { kind: "ok", options: OpencodeOptions | undefined }
  | { kind: "failure", result: ActionResult }

/**
 * `mohist/opencode` Action contract.
 *
 * Input shape is exactly:
 *   - `prompt`: required non-empty string after template resolution
 *   - `session`: optional logical session name; the runner resolves
 *     it to the current physical binding via the existing AgentSession
 *     model (#407) and passes the binding to the runtime
 *   - `options`: optional `{ model, variant }` object; `model` is
 *     `provider/model-id` (the model ID may contain additional `/`
 *     characters — the first `/` separates provider from model ID),
 *     `variant` is a sibling optional string. Unknown option keys
 *     (e.g. legacy `type`, liveness settings) are ignored with a
 *     diagnostic — they MUST NOT make an otherwise valid turn fail.
 *
 * The Action MUST NOT require `agent`, `kind`, `type`, or any other
 * Workflow completion field. `expect` is a task-level completion
 * contract and never reaches the Action; the executor owns that
 * evaluation.
 *
 * The Action runs the turn through `OpenCodeRuntime.runTurn` — the
 * native SDK v2 path introduced by #409. The runtime owns the
 * `client.session.create()` / `prompt()` / `abort()` calls, the
 * provider-error failure policy, the executor-owned deadline
 * backstop, and the physical-Session reuse invariants. The Action
 * itself never shells out to OpenCode CLI, and
 * never cleans up a `.opencode` lockfile.
 *
 * Output projection: the Workflow task executor's `projectTaskOutput`
 * step discards any debug payload in `output` and projects the
 * public Action Output to `null | { promise: <value> }` per the
 * opencode-action-contract spec scenario "Runtime and completion
 * facts stay out of OpenCode Action Output". Callers MUST NOT rely
 * on the shape of `output` returned here; treat it as internal
 * debug state. The private turn fact (`turnFact.finalAssistantText`)
 * is the corpus the executor evaluates `path: _output` against; the
 * runtime synthesizes that fact, the Action does NOT synthesize the
 * `{ promise }` output (the executor does, per #408).
 */
export async function opencodeAction(context: ActionContext): Promise<ActionResult> {
  let prompt: string | undefined
  try {
    prompt = await resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))
  } catch (error) {
    return fail("invalid-input", actionErrorMessage(error))
  }
  if (typeof prompt !== "string" || !prompt.trim()) {
    return fail("invalid-input", "mohist/opencode requires 'prompt' that resolves to non-empty text")
  }

  const optionsParse = parseOpencodeInput(context.with ?? null)
  if (optionsParse.kind === "failure") return optionsParse.result
  const options = optionsParse.options

  const runtime = context.openCodeRuntime
  if (!runtime) {
    return fail("runtime-unavailable", "mohist/opencode requires the OpenCode runtime; the runner has not yet established the runtime or it is rebuilding")
  }
  if (!runtime.ready()) {
    const diagnostic = runtime.diagnostic()
    return fail("runtime-unavailable", `mohist/opencode requires the OpenCode runtime to be ready: ${diagnostic?.message ?? "no readiness diagnostic"}`)
  }

  const sessionName = sessionNameFromContext(context)
  let binding: { runtimeSessionId: string | null; workDir: string } | null = null
  if (sessionName && context.serverConnection && context.projectId) {
    try {
      const opened = await context.serverConnection.openWorkflowAgentSession(
        context.projectId,
        context.workflowRunId,
        sessionName,
        {
          workId: context.workId,
          workType: context.workType,
          stage: context.stage,
          title: context.title,
          issueNumber: context.issueNumber,
          epicNumber: context.epicNumber,
          workDir: context.workDir,
        },
        context.signal,
      )
      if (opened.workDir && opened.workDir !== context.workDir) {
        return fail("session-workspace-mismatch", "Workflow AgentSession is bound to a different workspace; rerun the stage with a new task attempt before retrying")
      }
      binding = {
        runtimeSessionId: opened.runtimeSessionId ?? null,
        workDir: context.workDir,
      }
    } catch (error) {
      return fail("session-binding-failed", `Failed to resolve the Workflow AgentSession binding: ${actionErrorMessage(error)}`)
    }
  }
  if (!binding) {
    binding = { runtimeSessionId: null, workDir: context.workDir }
  }

  if (binding.runtimeSessionId === null && sessionName && context.serverConnection && context.projectId) {
    const created = await runtime.createSession({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: binding.workDir },
      model: runtimeModel(options),
    })
    if (!created.ok) {
      return fail(runtimeErrorCode(created.error.kind), created.error.message, { exitCode: 1, turnFact: { finalAssistantText: null } })
    }
    try {
      await context.serverConnection.attachWorkflowAgentSession(
        context.projectId,
        context.workflowRunId,
        sessionName,
        {
          runtimeSessionId: created.value.runtimeSessionId,
          workDir: created.value.workDir,
          processPid: null,
          model: options?.model ?? null,
          workId: context.workId,
        },
        context.signal,
      )
    } catch (error) {
      return fail("session-binding-failed", `Failed to persist the Workflow AgentSession binding: ${actionErrorMessage(error)}`, { exitCode: 1, turnFact: { finalAssistantText: null } })
    }
    binding = {
      runtimeSessionId: created.value.runtimeSessionId,
      workDir: created.value.workDir,
    }
  }

  const turnRequest = buildTurnRequest(binding, prompt, options, resolveTurnDeadlineMs(context))
  const result = await runtime.runTurn(turnRequest, context.signal)
  if (!result.ok) {
    return fail(runtimeErrorCode(result.error.kind), result.error.message, { exitCode: 1, turnFact: { finalAssistantText: null } })
  }
  const facts = result.value.facts
  const output = JSON.stringify({
    kind: "opencode",
    status: "success",
    runtimeSessionId: facts.runtimeSessionId,
    model: options?.model ?? null,
    variant: options?.variant ?? null,
    text: facts.finalAssistantText,
    diagnostics: result.value.diagnostics.map((d) => ({ code: d.code, message: d.message })),
  })
  return succeed(output, { exitCode: 0, turnFact: { finalAssistantText: facts.finalAssistantText } })
}

/**
 * Resolve and validate the `mohist/opencode` Action input shape.
 *
 * Rules:
 *   - `options` MUST be an object when present.
 *   - `options.model` MUST be a `provider/model-id` string when
 *     present (provider non-empty before the first `/`, model-id
 *     non-empty after it; additional `/` are allowed in the
 *     model-id portion).
 *   - `options.variant` MUST be a string when present and MUST NOT
 *     be appended to or parsed from the model identifier.
 *   - Unknown option keys (e.g. legacy `type`, liveness settings)
 *     are tolerated and reported via diagnostics inside the runtime;
 *     they MUST NOT make an otherwise valid turn fail.
 *
 * Exposed for tests so the validator can be exercised independently
 * of the OpenCode runtime turn.
 */
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
      // Treat explicit null/undefined as "not set".
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
      // Treat explicit null/undefined as "not set".
    } else if (typeof value !== "string") {
      return {
        kind: "failure",
        result: fail("invalid-input", "mohist/opencode 'options.variant' must be a string when present"),
      }
    } else {
      options.variant = value
    }
  }
  return { kind: "ok", options }
}

function runtimeErrorCode(kind: string): string {
  if (kind === "deadline-exceeded") return "timeout"
  if (kind === "missing-session") return "runtime-session-missing"
  return kind
}

function resolveTurnDeadlineMs(context: ActionContext): number {
  const override = numberInput(context.with, "timeout")
  if (typeof override === "number" && Number.isFinite(override) && override > 0) return override
  return DEFAULT_TURN_DEADLINE_MS
}

/**
 * Build the `RuntimeTurnRequest` the Action hands to
 * `OpenCodeRuntime.runTurn`. Exported for tests so the deadline
 * declaration can be asserted independently of the runtime turn.
 */
export function buildTurnRequest(
  binding: { runtimeSessionId: string | null; workDir: string },
  prompt: string,
  options: OpencodeOptions | undefined,
  deadlineMs: number,
): Parameters<OpenCodeRuntime["runTurn"]>[0] {
  const rawOptions = options as Record<string, unknown> | undefined
  return {
    target: {
      runtime: "opencode",
      runtimeSessionId: binding.runtimeSessionId,
      workDir: binding.workDir,
    },
    prompt,
    deadlineMs,
    options: {
      model: runtimeModel(options),
      variant: options?.variant ?? null,
      unknownKeys: collectUnknownKeys(rawOptions),
    },
  }
}

function runtimeModel(options: OpencodeOptions | undefined): { providerID: string; modelID: string } | null {
  const model = options?.model ? parseModelIdentifier(options.model) : undefined
  return model?.kind === "ok" ? { providerID: model.value.providerID, modelID: model.value.modelID } : null
}

function collectUnknownKeys(raw: Record<string, unknown> | undefined): readonly string[] | undefined {
  if (!raw || typeof raw !== "object") return undefined
  const known = new Set(["model", "variant"])
  const unknown: string[] = []
  for (const key of Object.keys(raw)) {
    if (!known.has(key)) unknown.push(key)
  }
  return unknown.length > 0 ? unknown : undefined
}
