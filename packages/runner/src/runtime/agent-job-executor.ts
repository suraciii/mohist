import { errorMessage } from "../core/errors.js"
import type {
  ActionResult,
  JsonObject,
  RenderedWorkItem,
} from "../core/types.js"
import { isObject } from "../core/json.js"
import { parseModelIdentifier } from "./opencode/index.js"
import type { OpenCodeRuntime, RuntimeResult, RuntimeTurnFacts, RuntimeTurnObserver, RuntimeTurnRequest } from "./opencode/index.js"
import type { ServerConnection } from "../server/connection.js"

/**
 * Agent-owned execution entry for `ownerKind === "agent-job"` work.
 *
 * Branches on the owner kind BEFORE Action resolution and drives the
 * shared `OpenCodeRuntime.runTurn` directly. No `mohist/opencode` Action
 * contract or removed Action. The AgentJob
 * payload lives in `work.with` as a flat
 * `{ prompt, instructions?, model?, variant? }` shape — composed at
 * launch time from the resolved Agent snapshot and stable for the
 * lifetime of the in-flight request.
 *
 * The returned `ActionResult.output` keeps the legacy
 * `{ kind, status, runtimeSessionId, model, variant, text, error,
 *   failureCategory? }` shape so `AgentJobGrain.ReportResultAsync`'s
 * success/failure parsing and `FailureCategoryFrom` keep working
 * unchanged.
 *
 * The physical Session binding is reported back through the existing
 * `/api/runner/{runnerId}/agent-sessions/{projectId}/{sessionId}/attach`
 * endpoint (`ServerConnection.attachAgentSession`); no new wire is introduced.
 */
export class AgentJobExecutor {
  constructor(
    private readonly connection: ServerConnection,
    private readonly runtime: OpenCodeRuntime | null,
  ) {}

  async execute(work: RenderedWorkItem, signal: AbortSignal): Promise<ActionResult> {
    if (work.ownerKind !== "agent-job") {
      return failureResult(work, `AgentJobExecutor received non-agent-job work (ownerKind=${work.ownerKind ?? "null"})`)
    }

    const payload = work.with ?? null
    const prompt = readPrompt(payload)
    if (!prompt) {
      return failureResult(work, "AgentJob requires 'prompt' in dispatch with-payload")
    }

    const instructions = readOptionalString(payload, "instructions")
    const composed = composePrompt(prompt, instructions)

    const modelInput = readOptionalString(payload, "model")
    const variant = readOptionalString(payload, "variant")
    const model = parseModel(modelInput)
    if (modelInput && model.kind === "failure") {
      return failureResult(work, `AgentJob ${model.message}`)
    }

    const runtime = this.runtime
    if (!runtime) {
      return failureResult(work, "AgentJob requires the OpenCode runtime; the runner has not yet established the runtime or it is rebuilding")
    }
    if (!runtime.ready()) {
      const diagnostic = runtime.diagnostic()
      return failureResult(work, `AgentJob requires the OpenCode runtime to be ready: ${diagnostic?.message ?? "no readiness diagnostic"}`)
    }

    const workDir = resolveWorkDir(work)
    if (!workDir) {
      return failureResult(work, "AgentJob requires 'workspace.path' in dispatch variables")
    }

    let binding: BindingResolution
    try {
      binding = await resolveBinding(work, this.connection, signal)
    } catch (error) {
      return failureResult(work, `AgentJob failed to resolve the AgentSession binding: ${errorMessage(error)}`)
    }
    let eventWrite = Promise.resolve()

    const turnRequest: RuntimeTurnRequest = {
      target: {
        runtime: "opencode",
        runtimeSessionId: binding.runtimeSessionId,
        workDir,
      },
      prompt: composed,
      options: {
        model: model.kind === "ok" ? { providerID: model.value.providerID, modelID: model.value.modelID } : null,
        variant: variant ?? null,
        unknownKeys: collectUnknownKeys(payload),
      },
    }

    const agentSessionId = binding.agentSessionId
    const projectId = work.projectId
    const observer: RuntimeTurnObserver | undefined = agentSessionId && projectId
      ? {
        onSessionReady: async ({ runtimeSessionId, workDir: readyWorkDir }) => {
          await this.connection.attachAgentSession(
            projectId,
            agentSessionId,
            {
              runtimeSessionId,
              workDir: readyWorkDir,
              processPid: null,
              model: modelInput,
              workId: work.workId,
              agentJobId: work.agentJobId ?? null,
            },
            signal,
          )
          await this.connection.agentSessionRuntimeEvents(
            projectId,
            agentSessionId,
            {
              workId: work.workId,
              workType: work.workType,
              stage: work.stage,
              runtimeSessionId,
              runtimeEvents: [{
                type: "session.input",
                payload: {
                  text: composed,
                  kind: "task",
                  source: "agent-job",
                  role: "user",
                  runtimeSessionId,
                },
              }],
            },
            signal,
          )
        },
        onEvent: (event) => {
          eventWrite = eventWrite
            .then(() => this.connection.agentSessionRuntimeEvents(
              projectId,
              agentSessionId,
              {
                workId: work.workId,
                workType: work.workType,
                stage: work.stage,
                runtimeSessionId: event.runtimeSessionId,
                runtimeEvents: [{ type: event.type, payload: event.payload }],
              },
              signal,
            ))
            .catch((error) => {
              console.error(`agent-session runtime event failed for job ${work.agentJobId ?? "?"}: ${errorMessage(error)}`)
            })
        },
      }
      : undefined

    const result = await runtime.runTurn(turnRequest, signal, observer)
    await eventWrite
    if (!result.ok) {
      const error = result.error
      const isMissing = error.kind === "missing-session"
      const output = JSON.stringify({
        kind: "opencode",
        status: "failure",
        runtimeSessionId: null,
        model: modelInput ?? null,
        variant: variant ?? null,
        text: null,
        error: error.message,
        diagnostics: result.diagnostics.map((d) => ({ code: d.code, message: d.message })),
        ...(isMissing ? { hint: "reset" } : {}),
      })
      return {
        status: "failed",
        message: error.message,
        output,
        exitCode: 1,
      }
    }

    const facts = result.value.facts
    const output = JSON.stringify({
      kind: "opencode",
      status: "success",
      runtimeSessionId: facts.runtimeSessionId,
      model: modelInput ?? null,
      variant: variant ?? null,
      text: facts.finalAssistantText,
      error: null,
      diagnostics: result.value.diagnostics.map((d) => ({ code: d.code, message: d.message })),
    })
    return {
      status: "completed",
      message: "AgentJob completed",
      output,
      exitCode: 0,
    }
  }
}

type BindingResolution = { agentSessionId: string | null; runtimeSessionId: string | null }

async function resolveBinding(
  work: RenderedWorkItem,
  connection: ServerConnection,
  signal: AbortSignal,
): Promise<BindingResolution> {
  const agentSessionId = work.agentSessionId ?? null
  if (!agentSessionId || !work.projectId) {
    return { agentSessionId: null, runtimeSessionId: null }
  }
  const opened = await connection.getAgentSession(work.projectId, agentSessionId, signal)
  return {
    agentSessionId,
    runtimeSessionId: opened?.runtimeSessionId ?? null,
  }
}

function resolveWorkDir(work: RenderedWorkItem): string | null {
  const ws = work.variables?.["workspace"]
  if (!isObject(ws)) return null
  const path = ws["path"]
  return typeof path === "string" && path.length > 0 ? path : null
}

function readPrompt(payload: JsonObject | null): string | null {
  const prompt = payload?.["prompt"]
  if (typeof prompt === "string") {
    const trimmed = prompt.trim()
    return trimmed.length > 0 ? prompt : null
  }
  return null
}

function readOptionalString(payload: JsonObject | null, key: string): string | null {
  const value = payload?.[key]
  if (typeof value !== "string") return null
  return value.length > 0 ? value : null
}

function composePrompt(prompt: string, instructions: string | null): string {
  if (!instructions) return prompt
  return `${instructions}\n\n${prompt}`
}

type ParsedModel =
  | { kind: "ok"; value: { providerID: string; modelID: string } }
  | { kind: "failure"; message: string }
  | { kind: "absent" }

function parseModel(input: string | null): ParsedModel {
  if (!input) return { kind: "absent" }
  const parsed = parseModelIdentifier(input)
  if (parsed.kind === "failure") return { kind: "failure", message: parsed.message }
  return { kind: "ok", value: parsed.value }
}

function collectUnknownKeys(payload: JsonObject | null): readonly string[] | undefined {
  if (!payload || typeof payload !== "object") return undefined
  const known = new Set(["prompt", "instructions", "model", "variant"])
  const unknown: string[] = []
  for (const key of Object.keys(payload)) {
    if (!known.has(key)) unknown.push(key)
  }
  return unknown.length > 0 ? unknown : undefined
}

function failureResult(work: RenderedWorkItem, message: string): ActionResult {
  return {
    status: "failed",
    message,
  }
}

/**
 * Internal helper exposed for tests. Wraps a turn's runtime result in
 * the legacy output envelope used by `AgentJobGrain.ReportResultAsync`.
 */
export function buildActionOutputFromTurn(
  ok: boolean,
  runtimeSessionId: string | null,
  model: string | null,
  variant: string | null,
  text: string | null,
  error: string | null,
  diagnostics: readonly { code: string; message: string }[],
): string {
  return JSON.stringify({
    kind: "opencode",
    status: ok ? "success" : "failure",
    runtimeSessionId,
    model,
    variant,
    text,
    error,
    diagnostics,
  })
}

/**
 * Convert a {@link RuntimeResult} into the {@link ActionResult} shape
 * expected by the legacy `AgentJobGrain.ReportResultAsync` parsing
 * path. Used by tests to verify the runner's output envelope stays
 * drop-in compatible.
 */
export function projectTurnToActionResult(
  result: RuntimeResult<{ facts: RuntimeTurnFacts; diagnostics: readonly { code: string; message: string }[] }>,
  model: string | null,
  variant: string | null,
): ActionResult {
  if (!result.ok) {
    const error = result.error
    const output = JSON.stringify({
      kind: "opencode",
      status: "failure",
      runtimeSessionId: null,
      model,
      variant,
      text: null,
      error: error.message,
      diagnostics: result.diagnostics.map((d) => ({ code: d.code, message: d.message })),
    })
    return {
      status: "failure",
      message: error.message,
      output,
      exitCode: 1,
      turnFact: { finalAssistantText: null },
    }
  }
  const facts = result.value.facts
  const output = JSON.stringify({
    kind: "opencode",
    status: "success",
    runtimeSessionId: facts.runtimeSessionId,
    model,
    variant,
    text: facts.finalAssistantText,
    error: null,
    diagnostics: result.value.diagnostics.map((d) => ({ code: d.code, message: d.message })),
  })
  return {
    status: "success",
    message: "OpenCode agent task completed",
    output,
    exitCode: 0,
    turnFact: { finalAssistantText: facts.finalAssistantText },
  }
}
