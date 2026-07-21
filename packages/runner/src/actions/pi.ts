import type { ActionResult, JsonObject, ParentIssueContext } from "../core/types.js"
import type { ServerConnection } from "../server/connection.js"
import type { TaskLogger } from "../runtime/task-log.js"
import type { PiRuntime } from "../runtime/pi/index.js"
import type { ActionHost } from "./host.js"
import { isObject } from "../core/json.js"
import { resolvePrompt } from "../core/prompt.js"
import { sessionNameFromContext } from "./workflow-session-name.js"
import { parseModelIdentifier } from "../runtime/opencode/index.js"
import type { PiRuntimeEvent, PiTurnRequest } from "../runtime/pi/index.js"
import { actionErrorMessage, fail, succeed } from "./action-result.js"
import type { PromptLoaderContext } from "../core/prompt.js"

export const PI_USES = "mohist/pi"
export const PI_TURN_DURATION_MS = 60 * 60 * 1000

interface ActionInvocationContext {
  workflowRunId: string
  workId: string
  workType: string
  stage?: string | null
  title?: string | null
  with?: JsonObject | null
  workDir: string
  signal: AbortSignal
  projectId?: string | null
  issueNumber?: number | null
  epicNumber?: number | null
  parentIssueContext?: ParentIssueContext | null
  piRuntime?: PiRuntime | null
  serverConnection?: ServerConnection | null
  log?: TaskLogger | null
}

export function composePiPrompt(prompt: string, parentIssueContext?: ParentIssueContext | null): string {
  if (!parentIssueContext) return prompt
  const parent = JSON.stringify({ title: parentIssueContext.title, body: parentIssueContext.body })
  return `Parent issue context (read-only background; JSON):\n${parent}\n\nTreat the parent issue context above as read-only background. The current child issue body is authoritative and controls delivery scope.\n\n${prompt}`
}

interface PiOptions { model?: string; variant?: string; unknownKeys?: readonly string[] }

export function piAction(context: ActionInvocationContext): Promise<ActionResult>
export function piAction(inputs: JsonObject, host: ActionHost): Promise<ActionResult>
export async function piAction(contextOrInputs: ActionInvocationContext | JsonObject, host?: ActionHost): Promise<ActionResult> {
  const context: ActionInvocationContext = host
    ? {
      workflowRunId: "",
      workId: "pi",
      workType: "task",
      with: contextOrInputs as JsonObject,
      workDir: host.workDir,
      signal: host.signal,
      piRuntime: host.piRuntime,
      log: host.log,
    }
    : contextOrInputs as ActionInvocationContext
  const parsed = await parseInput(context)
  if (parsed.kind === "failure") return parsed.result
  const { prompt, options } = parsed
  const runtime = context.piRuntime
  if (!runtime) return fail("runtime-unavailable", "mohist/pi requires the Pi runtime")
  if (!runtime.ready()) return fail("runtime-unavailable", `mohist/pi requires the Pi runtime to be ready: ${runtime.diagnostic()?.message ?? "no readiness diagnostic"}`)

  const sessionName = sessionNameFromContext(context)
  const canBind = !!context.serverConnection && !!context.projectId
  let runtimeSessionId: string | null = null
  let expectedRuntime: string | null = null
  let expectedRuntimeSessionId: string | null = null
  if (canBind) {
    try {
      const opened = await context.serverConnection!.openWorkflowAgentSession(
        context.projectId!, context.workflowRunId, sessionName,
        { workId: context.workId, workType: context.workType, stage: context.stage, title: context.title, issueNumber: context.issueNumber, epicNumber: context.epicNumber, workDir: context.workDir, runtime: "pi" },
        context.signal,
      )
      if (opened.workDir && opened.workDir !== context.workDir) return fail("session-workspace-mismatch", "Workflow AgentSession is bound to a different workspace; rerun the stage with a new task attempt before retrying")
      runtimeSessionId = opened.runtimeSessionId ?? null
      expectedRuntime = opened.runtime ?? null
      expectedRuntimeSessionId = opened.runtimeSessionId ?? null
    } catch (error) {
      return fail("session-binding-failed", `Failed to resolve the Workflow AgentSession binding: ${actionErrorMessage(error)}`)
    }
  }

  if (runtimeSessionId === null || expectedRuntime !== "pi") {
    const created = await runtime.createSession({ target: { runtime: "pi", runtimeSessionId: null, workDir: context.workDir } })
    if (!created.ok) return runtimeFailure(created.error.kind, created.error.message, created.error.diagnostics)
    runtimeSessionId = created.value.runtimeSessionId
    if (canBind) {
      try {
        await context.serverConnection!.attachWorkflowAgentSession(
          context.projectId!, context.workflowRunId, sessionName,
          { runtimeSessionId, workDir: context.workDir, processPid: null, model: options.model ?? null, workId: context.workId, runtime: "pi", expectedRuntime, expectedRuntimeSessionId },
          context.signal,
        )
      } catch (error) {
        return fail("session-binding-failed", `Failed to persist the Workflow AgentSession binding: ${actionErrorMessage(error)}`, { exitCode: 1, turnFact: { finalAssistantText: null } })
      }
    }
  }

  const events: PiRuntimeEvent[] = []
  const report = async (facts: readonly PiRuntimeEvent[], signal = context.signal) => {
    if (!canBind || facts.length === 0) return
    await context.serverConnection!.workflowAgentSessionRuntimeEvents(
      context.projectId!, context.workflowRunId, sessionName,
      { workId: context.workId, workType: context.workType, stage: context.stage, runtimeSessionId, runtimeEvents: facts.map((event) => ({ id: event.id, type: event.type, payload: event.payload })) },
      signal,
    )
  }

  try {
    await report([inputEvent(runtimeSessionId, prompt, context)])
  } catch {
    return fail("session-reporting-failed", "Workflow AgentSession rejected session.input; prompt was not submitted", { exitCode: 1, turnFact: { finalAssistantText: null } })
  }

  const request: PiTurnRequest = { target: { runtime: "pi", runtimeSessionId, workDir: context.workDir }, prompt, durationMs: PI_TURN_DURATION_MS, options: { model: options.model ?? null, variant: options.variant ?? null, unknownKeys: options.unknownKeys } }
  let result
  try {
    result = await runtime.runTurn(request, context.signal, { onEvent: (event) => events.push(event) })
  } catch (error) {
    let terminalReportingFailed = false
    try {
      await reportWithTerminalSignal(report, [
        ...events,
        { id: `session-closed-${context.workId}`, type: "session.closed", runtimeSessionId, workDir: context.workDir, payload: { status: "failed", errorCode: "turn-failed" } },
      ])
    } catch {
      terminalReportingFailed = true
    }
    const message = actionErrorMessage(error)
    return fail("turn-failed", terminalReportingFailed
      ? `${message}; Session terminal reporting failed and terminal state was not accepted`
      : message, { exitCode: 1, turnFact: { finalAssistantText: null } })
  }

  const finalText = result.ok ? result.value.facts.finalAssistantText : null
  const runtimeCode = result.ok ? null : runtimeErrorCode(result.error.kind)
  const finalFacts = [...events]
  const submittedFailure = !result.ok && (result.error.kind === "deadline-exceeded" || result.error.kind === "interrupted" || result.error.kind === "turn-failed")
  if (result.ok || submittedFailure) {
    finalFacts.push({ id: `session-closed-${context.workId}`, type: "session.closed", runtimeSessionId, workDir: context.workDir, payload: { status: result.ok ? "completed" : "failed", ...(runtimeCode ? { errorCode: runtimeCode } : {}) } })
  }
  try {
    await reportWithTerminalSignal(report, finalFacts)
  } catch {
    if (result.ok) return fail("session-reporting-failed", "Workflow AgentSession did not accept the final Pi turn facts", { exitCode: 1, turnFact: { finalAssistantText: null } })
    return fail(runtimeCode ?? "turn-failed", `${result.error.message}; Session terminal reporting failed and terminal state was not accepted`, { exitCode: 1, turnFact: { finalAssistantText: null } })
  }
  if (!result.ok) return fail(runtimeCode ?? "turn-failed", result.error.message, { exitCode: 1, turnFact: { finalAssistantText: null } })
  return succeed(null, { exitCode: 0, turnFact: { finalAssistantText: finalText } })
}

function buildPromptLoaderContext(context: Pick<ActionInvocationContext, "workDir" | "workId" | "title" | "stage">): PromptLoaderContext {
  return {
    with: {},
    workDir: context.workDir,
    workId: context.workId,
    title: context.title ?? null,
    stage: context.stage ?? null,
  }
}

async function reportWithTerminalSignal(report: (facts: readonly PiRuntimeEvent[], signal?: AbortSignal) => Promise<void>, facts: readonly PiRuntimeEvent[]): Promise<void> {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), 30_000)
  try {
    await report(facts, controller.signal)
  } finally {
    clearTimeout(timeout)
  }
}

async function parseInput(context: ActionInvocationContext): Promise<{ kind: "ok"; prompt: string; options: PiOptions } | { kind: "failure"; result: ActionResult }> {
  const input = context.with ?? {}
  const allowed = new Set(["prompt", "session", "options", "working-directory"])
  const invalid = Object.keys(input).find((key) => !allowed.has(key))
  if (invalid) return { kind: "failure", result: fail("invalid-input", `mohist/pi does not accept top-level input '${invalid}'`) }
  const rawSession = input.session
  if (rawSession !== undefined && rawSession !== null && typeof rawSession !== "string") return { kind: "failure", result: fail("invalid-input", "mohist/pi 'session' must be a string when present") }
  if (rawSession !== undefined && rawSession !== null && !rawSession.trim()) return { kind: "failure", result: fail("invalid-input", "mohist/pi 'session' must not be empty") }
  let prompt: string | undefined
  try { prompt = await resolvePrompt(input.prompt, buildPromptLoaderContext(context)) } catch (error) { return { kind: "failure", result: fail("invalid-input", actionErrorMessage(error)) } }
  if (!prompt?.trim()) return { kind: "failure", result: fail("invalid-input", "mohist/pi requires 'prompt' that resolves to non-empty text") }
  const rawOptions = input.options
  if (rawOptions !== undefined && rawOptions !== null && !isObject(rawOptions)) return { kind: "failure", result: fail("invalid-input", "mohist/pi 'options' must be an object when present") }
  const options: PiOptions = {}
  const record = (rawOptions ?? {}) as Record<string, unknown>
  for (const key of ["model", "variant"] as const) {
    const value = record[key]
    if (value === undefined || value === null) continue
    if (typeof value !== "string") return { kind: "failure", result: fail("invalid-input", `mohist/pi 'options.${key}' must be a string when present`) }
    if (key === "model") {
      const parsed = parseModelIdentifier(value)
      if (parsed.kind === "failure") return { kind: "failure", result: fail("invalid-input", `mohist/pi ${parsed.message}`) }
    }
    options[key] = value
  }
  const unknownKeys = Object.keys(record).filter((key) => key !== "model" && key !== "variant")
  if (unknownKeys.length > 0) options.unknownKeys = unknownKeys
  return { kind: "ok", prompt: composePiPrompt(prompt, context.parentIssueContext), options }
}

function inputEvent(runtimeSessionId: string, prompt: string, context: ActionInvocationContext): PiRuntimeEvent {
  return { id: `session-input-${context.workId}`, type: "session.input", runtimeSessionId, workDir: context.workDir, payload: { text: prompt, kind: context.workType, source: "workflow", role: "user", runtimeSessionId } }
}

function runtimeErrorCode(kind: string): string {
  if (kind === "deadline-exceeded") return "timeout"
  if (kind === "missing-session") return "runtime-session-missing"
  return kind
}

function runtimeFailure(kind: string, message: string, diagnostics: readonly { code: string; message: string }[]): ActionResult {
  const code = runtimeErrorCode(kind)
  const hint = kind === "missing-session" ? " Reset the Workflow Session before retrying." : ""
  return fail(code, `${message}${hint}`, { exitCode: 1, turnFact: { finalAssistantText: null } })
}
