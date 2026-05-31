import { spawn, type ChildProcess } from "node:child_process"
import { Readable, Writable } from "node:stream"
import { ClientSideConnection, ndJsonStream, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput, objectInput, stringInput } from "../core/json.js"
import { killProcess, runCommand, sanitizedEnvironment } from "../system/process.js"
import { verifyExpectations } from "./expectations.js"
import type { AcpSessionManager, SharedAcpConnection } from "../runtime/acp-connection.js"
import { acpArgs, acpCommand } from "../runtime/acp-command.js"

export interface AcpProcessHandle {
  readonly stream: Stream
  readonly processPid: number | null
  readonly spawnFailure: Promise<never>
  readonly exitFailure: Promise<never>
  markInitialized(): void
  exitCode(): number | null
  cleanup(): Promise<void>
}

export type AcpProcessFactory = (context: ActionContext) => AcpProcessHandle

let acpProcessFactory: AcpProcessFactory = createSpawnedAcpProcess

export function setAcpProcessFactoryForTest(factory: AcpProcessFactory | null) {
  acpProcessFactory = factory ?? createSpawnedAcpProcess
}

function getAcpProcessFactory() {
  return acpProcessFactory
}

const DEFAULT_TIMEOUT_MS = 30 * 60 * 1000
const DEFAULT_SESSION_START_TIMEOUT_MS = 30 * 1000
const DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000
const DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024
const PROBE_PROMPT = "If this session is still alive, briefly report the current step and continue from existing context. Do not restart completed work."

const QUALIFYING_LIVENESS_NOTIFICATION_TYPES = new Set([
  "agent_message_chunk",
  "agent_thought_chunk",
  "tool_call",
  "tool_call_update",
  "tool_result",
  "tool_result_update",
])

type AcpLivenessActivity =
  | { isActivity: false }
  | { isActivity: true; activityType: string }

function classifyAcpLivenessActivity(source:
  | { kind: "session_update"; update: SessionNotification["update"] }
  | { kind: "protocol_response"; response: "initialize" | "new_session" | "resume_session" | "set_session_config" | "set_session_model" }
): AcpLivenessActivity {
  if (source.kind === "protocol_response") {
    return { isActivity: true, activityType: source.response }
  }

  const update = source.update
  const type = update.sessionUpdate
  if (!type) return { isActivity: false }
  if (QUALIFYING_LIVENESS_NOTIFICATION_TYPES.has(type)) {
    return { isActivity: true, activityType: type }
  }

  if (type === "session_info_update" && hasMessageGrowth(update)) {
    return { isActivity: true, activityType: "message_growth" }
  }

  if (type.includes("tool") && (type.includes("result") || type.includes("output") || type.includes("update"))) {
    return { isActivity: true, activityType: type }
  }

  return { isActivity: false }
}

function assistantMessageChunkText(update: SessionNotification["update"]): string | undefined {
  if (update.sessionUpdate !== "agent_message_chunk") return undefined
  if (!("content" in update) || !update.content || typeof update.content !== "object") return undefined
  return "text" in update.content ? String(update.content.text) : undefined
}

function hasMessageGrowth(update: SessionNotification["update"]): boolean {
  const candidate = update as Record<string, unknown>
  for (const key of ["messages", "message", "messageCount", "messageDelta"]) {
    const value = candidate[key]
    if (Array.isArray(value) && value.length > 0) return true
    if (typeof value === "string" && value.trim().length > 0) return true
    if (typeof value === "number" && value > 0) return true
  }
  return false
}

function recordLivenessActivity(notify: (activityType?: string) => void, activity: AcpLivenessActivity) {
  if (activity.isActivity) notify(activity.activityType)
}

function createAcpSessionUpdateHandler(options: {
  notifyData(activityType?: string): void
  appendAssistantText(text: string): void
  emitUpdate(type: string, update: SessionNotification["update"]): Promise<void>
}) {
  return async (notification: SessionNotification) => {
    const update = notification.update
    const type = update.sessionUpdate
    recordLivenessActivity(options.notifyData, classifyAcpLivenessActivity({ kind: "session_update", update }))

    const chunkText = assistantMessageChunkText(update)
    if (chunkText !== undefined) options.appendAssistantText(chunkText)

    await options.emitUpdate(type, update)
  }
}

interface AgentConfig {
  model?: string
  timeoutMs?: number
  sessionStartTimeoutMs?: number
  livenessQuietThresholdMs?: number
  probeTimeoutMs?: number
}

interface LivenessProbeState {
  probeSentAt?: string
  probeDeadlineAt?: string
  probeVersion?: number
  lastDataAt: number
  lastActivityType?: string
  dataVersion: number
  postProbeActivity?: boolean
}

interface SessionLivenessState {
  probeSentAt?: string
  probeDeadlineAt?: string
  probeVersion?: number
  lastDataAt: number
  lastActivityType?: string
  dataVersion: number
}

type LivenessFailureReason = "probe_timeout" | "probe_send_failed" | "protocol_disconnect" | "process_exit"

function createSessionLivenessState(): SessionLivenessState {
  return {
    lastDataAt: Date.now(),
    dataVersion: 0,
  }
}

function recordSessionLivenessActivity(state: SessionLivenessState, activityType?: string) {
  state.lastDataAt = Date.now()
  state.dataVersion += 1
  if (activityType) state.lastActivityType = activityType
}

function beginLivenessProbe(state: SessionLivenessState, probeTimeoutMs: number) {
  const probeSentAt = new Date()
  const probeDeadlineAt = new Date(probeSentAt.getTime() + probeTimeoutMs)
  state.probeSentAt = probeSentAt.toISOString()
  state.probeDeadlineAt = probeDeadlineAt.toISOString()
  state.probeVersion = state.dataVersion
  return { probeSentAt: state.probeSentAt, probeDeadlineAt: state.probeDeadlineAt, probeVersion: state.probeVersion }
}

function clearLivenessProbe(state: SessionLivenessState) {
  state.probeSentAt = undefined
  state.probeDeadlineAt = undefined
  state.probeVersion = undefined
}

function hasPostProbeActivity(state: SessionLivenessState) {
  return state.probeVersion !== undefined && state.dataVersion > state.probeVersion
}

function probeWasSatisfied(state: SessionLivenessState) {
  if (state.probeVersion === undefined || !state.probeDeadlineAt) return false
  return hasPostProbeActivity(state) && state.lastDataAt <= Date.parse(state.probeDeadlineAt)
}

function buildLivenessEventPayload(context: ActionContext, state: SessionLivenessState, status: "probing" | "running" | "failed", extras?: {
  acpSessionId?: string
  activeProbeVersion?: number
  satisfiedProbeVersion?: number
  failureReason?: LivenessFailureReason
  postProbeActivity?: boolean
}): JsonObject {
  return cleanJson({
    sessionName: sessionNameFromContext(context),
    acpSessionId: extras?.acpSessionId ?? null,
    workId: context.workId,
    workType: context.workType,
    stage: context.stage ?? null,
    status,
    lastDataAt: new Date(state.lastDataAt).toISOString(),
    lastActivityType: state.lastActivityType,
    probeSentAt: state.probeSentAt,
    probeDeadlineAt: state.probeDeadlineAt,
    probeVersion: state.probeVersion,
    dataVersion: state.dataVersion,
    postProbeActivity: extras?.postProbeActivity,
    activeProbeVersion: extras?.activeProbeVersion,
    satisfiedProbeVersion: extras?.satisfiedProbeVersion,
    failureReason: extras?.failureReason,
  })
}

async function emitLivenessStatusEvent(context: ActionContext, state: SessionLivenessState, status: "probing" | "running" | "failed", extras?: {
  acpSessionId?: string
  activeProbeVersion?: number
  satisfiedProbeVersion?: number
  failureReason?: LivenessFailureReason
  postProbeActivity?: boolean
}) {
  await emitSessionEvent(context, "agent_liveness_status", buildLivenessEventPayload(context, state, status, extras))
}

interface AcpSessionResult {
  text: string
  success: boolean
  error?: string
  acpSessionId?: string
  exitCode?: number | null
}

export async function acpAgentAction(context: ActionContext): Promise<ActionResult> {
  const prompt = buildPromptWithMohistContext(context, stringInput(context.with, "prompt") ?? buildFallbackPrompt(context))
  if (!prompt?.trim()) return { status: "failure", message: "ACP agent requires 'prompt'" }

  const result = await runAcpWorkflowAgentSession(context, prompt)
  await restoreAgentToolNoise(context)
  const verification = await verifyExpectations(context)
  const ok = result.success && verification.satisfied
  const agentConfig = resolveAgentConfig(context.with)
  await emitSessionEvent(context, "agent_session_terminal", { status: ok ? "completed" : "failed", failureReason: ok ? null : result.error ?? verification.message, exitCode: result.exitCode ?? (ok ? 0 : 1) })
  return {
    status: ok ? "success" : "failure",
    message: ok ? "ACP agent task completed" : result.error ?? verification.message,
    output: JSON.stringify({ kind: "acp-agent", status: ok ? "success" : "failure", acpSessionId: result.acpSessionId, model: agentConfig?.model, text: result.text, error: result.error, expectation: verification }),
    exitCode: result.exitCode ?? (ok ? 0 : 1),
  }
}

export function buildPromptWithMohistContext(context: Pick<ActionContext, "variables" | "issueNumber">, prompt?: string) {
  if (!prompt) return prompt

  const issue = objectInput(context.variables, "issue")
  const title = promptContextField(issue, "title")
  const body = promptContextField(issue, "body")
  const number = promptContextField(issue, "number") ?? (context.issueNumber != null ? String(context.issueNumber) : undefined)
  if (!number && !title?.trim() && !body?.trim()) return prompt

  return [
    "## Mohist Issue Context",
    "This is the exact issue being implemented. Keep all artifacts and code changes aligned to this issue; do not substitute a different change.",
    number ? `Number: ${number}` : "",
    title?.trim() ? `Title: ${title.trim()}` : "",
    body?.trim() ? `Body:\n${body.trim()}` : "",
    "## Task Prompt",
    prompt,
  ].filter(Boolean).join("\n\n")
}

function promptContextField(value: JsonObject | undefined, key: string) {
  const found = value?.[key]
  if (typeof found === "string") return found
  if (typeof found === "number" || typeof found === "boolean") return String(found)
  return undefined
}

async function restoreAgentToolNoise(context: ActionContext) {
  for (const path of [".opencode/package-lock.json", ".opencode/bun.lock", ".opencode/node_modules/.package-lock.json"]) {
    try {
      await runCommand("git", ["checkout", "--", path], context.workDir, context.signal)
    } catch {
      // Tool-noise cleanup must never turn a successful agent run into a failure.
    }
  }
}

function resolveAgentConfig(with_?: JsonObject | null): AgentConfig | undefined {
  if (!with_) return undefined
  const agent = objectInput(with_, "agent")
  if (agent && typeof agent === "object") {
    return {
      model: stringInput(agent as JsonObject, "model") ?? undefined,
      timeoutMs: numberInput(agent as JsonObject, "timeout") ?? undefined,
      sessionStartTimeoutMs: numberInput(agent as JsonObject, "sessionStartTimeout") ?? undefined,
      livenessQuietThresholdMs: numberInput(agent as JsonObject, "livenessQuietThresholdMs") ?? undefined,
      probeTimeoutMs: numberInput(agent as JsonObject, "probeTimeoutMs") ?? undefined,
    }
  }
  return {
    model: stringInput(with_, "model") ?? undefined,
    timeoutMs: numberInput(with_, "timeout") ?? undefined,
    sessionStartTimeoutMs: numberInput(with_, "sessionStartTimeout") ?? undefined,
    livenessQuietThresholdMs: numberInput(with_, "livenessQuietThresholdMs") ?? undefined,
    probeTimeoutMs: numberInput(with_, "probeTimeoutMs") ?? undefined,
  }
}

function buildFallbackPrompt(context: ActionContext) {
  const title = context.title ?? stringInput(context.with, "title")
  const description = stringInput(context.with, "description")
  if (!title?.trim() && !description?.trim()) return undefined

  const sections = [
    title?.trim() ? `Implement this task: ${title.trim()}` : "Implement this task.",
    description?.trim() ? `## Description\n${description.trim()}` : "",
    valueSection("Acceptance Criteria", context.with?.acceptanceCriteria),
    valueSection("Depends On", context.with?.dependsOn),
    valueSection("Output", context.with?.output),
    valueSection("Notes", context.with?.notes),
    "Follow the repository conventions. Make the smallest complete change that satisfies the task, and run the relevant verification before reporting completion.",
  ].filter(Boolean)
  return sections.join("\n\n")
}

function valueSection(title: string, value: unknown) {
  if (value === undefined || value === null) return ""
  if (Array.isArray(value) && value.length === 0) return ""
  return `## ${title}\n${formatValue(value)}`
}

function formatValue(value: unknown): string {
  if (Array.isArray(value)) return value.map((item) => `- ${String(item)}`).join("\n")
  if (typeof value === "object") return JSON.stringify(value, null, 2)
  return String(value)
}

async function runAcpWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const sessionName = sessionNameFromContext(context)
  const manager = context.acpSessionManager
  const projectId = context.projectId

  if (sessionName && context.serverConnection && projectId) {
    await context.serverConnection.ensureWorkflowAgentSession(projectId, context.workflowRunId, sessionName, {
      workId: context.workId,
      workType: context.workType,
      stage: context.stage,
      title: context.title,
      issueNumber: context.issueNumber,
    }, context.signal)
  }

  if (sessionName && manager && context.serverConnection && projectId) {
    const key = manager.key(context.workflowRunId, sessionName)
    const session = await context.serverConnection.ensureWorkflowAgentSession(projectId, context.workflowRunId, sessionName, {
      workId: context.workId,
      workType: context.workType,
      stage: context.stage,
      title: context.title,
      issueNumber: context.issueNumber,
    }, context.signal)

    if (session.acpSessionId) {
      const cached = manager.get(key)
      if (cached?.sessionId === session.acpSessionId) return runPromptOnExistingWorkflowAgentSession(context, prompt, cached)
      const result = await runResumedWorkflowAgentSession(context, prompt, session.acpSessionId, session.workDir ?? context.workDir)
      if (result.success && result.acpSessionId) manager.set(key, { sessionId: result.acpSessionId, workDir: session.workDir ?? context.workDir })
      return result
    }

    const result = await runNewWorkflowAgentSession(context, prompt)
    if (result.success && result.acpSessionId) manager.set(key, { sessionId: result.acpSessionId, workDir: context.workDir })
    return result
  }

  return runEphemeralWorkflowAgentSession(context, prompt)
}

async function runPromptOnExistingWorkflowAgentSession(context: ActionContext, prompt: string, entry: { sessionId: string; workDir: string }): Promise<AcpSessionResult> {
  const acp = context.acpConnection
  if (!acp) return { text: "", success: false, error: "No shared ACP connection available", exitCode: 1 }

  const connection = acp.connection
  const agentConfig = resolveAgentConfig(context.with)
  const timeoutMs = agentConfig?.timeoutMs ?? numberInput(context.with, "timeout") ?? DEFAULT_TIMEOUT_MS
  let agentText = ""
  let agentTextTruncated = false
  const toolIds = new ToolCallIdGenerator()
  const liveness = createSessionLivenessState()
  const dataWaiters = new Set<() => void>()
  const notifyData = (activityType?: string) => {
    recordSessionLivenessActivity(liveness, activityType)
    for (const waiter of dataWaiters) waiter()
    dataWaiters.clear()
  }
  const appendAssistantText = (chunkText: string) => {
    if (agentTextTruncated) return
    agentText += chunkText
    if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
      agentText = truncateAgentText(agentText)
      agentTextTruncated = true
    }
  }

  acp.setActiveHandlers(
    createAcpSessionUpdateHandler({
      notifyData,
      appendAssistantText,
      emitUpdate: async (type, update) => {
        await emitSessionEvent(context, type, normalizeSessionUpdate(update as unknown as JsonObject, entry.sessionId, toolIds))
      },
    }),
    async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
      const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
      return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
    },
  )

  await emitSessionStarted(context, entry.sessionId, acp.processPid, agentConfig)
  await emitSessionEvent(context, "mohist_prompt", buildPromptEvent(context, prompt, entry.sessionId))

  try {
    const promptResult = await monitorPrompt(context, connection, entry.sessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
    })
    if (promptResult !== "completed") {
      return { text: agentText, success: false, error: promptResult.error, acpSessionId: entry.sessionId, exitCode: 1 }
    }
    return { text: agentText, success: true, acpSessionId: entry.sessionId, exitCode: 0 }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: entry.sessionId, exitCode: 1 }
  } finally {
    acp.clearActiveHandlers()
  }
}

async function runResumedWorkflowAgentSession(context: ActionContext, prompt: string, acpSessionId: string, workDir: string): Promise<AcpSessionResult> {
  const acp = context.acpConnection
  if (!acp) return { text: "", success: false, error: "No shared ACP connection available", exitCode: 1 }

  const connection = acp.connection
  const agentConfig = resolveAgentConfig(context.with)
  const timeoutMs = agentConfig?.timeoutMs ?? numberInput(context.with, "timeout") ?? DEFAULT_TIMEOUT_MS
  const sessionStartTimeoutMs = agentConfig?.sessionStartTimeoutMs ?? numberInput(context.with, "sessionStartTimeout") ?? DEFAULT_SESSION_START_TIMEOUT_MS
  let agentText = ""
  let agentTextTruncated = false
  const liveness = createSessionLivenessState()
  const dataWaiters = new Set<() => void>()
  const toolIds = new ToolCallIdGenerator()
  const notifyData = (activityType?: string) => {
    recordSessionLivenessActivity(liveness, activityType)
    for (const waiter of dataWaiters) waiter()
    dataWaiters.clear()
  }
  const appendAssistantText = (chunkText: string) => {
    if (agentTextTruncated) return
    agentText += chunkText
    if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
      agentText = truncateAgentText(agentText)
      agentTextTruncated = true
    }
  }

  acp.setActiveHandlers(
    createAcpSessionUpdateHandler({
      notifyData,
      appendAssistantText,
      emitUpdate: async (type, update) => {
        await emitSessionEvent(context, type, normalizeSessionUpdate(update as unknown as JsonObject, acpSessionId, toolIds))
      },
    }),
    async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
      const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
      return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
    },
  )

  try {
    const resumeResult = await Promise.race([
      connection.resumeSession({ sessionId: acpSessionId, cwd: workDir, mcpServers: [] }),
      timeout(sessionStartTimeoutMs),
    ])
    if (resumeResult === "timeout") throw new Error("Timed out during ACP resumeSession")
    recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "resume_session" }))

    const model = agentConfig?.model ?? stringInput(context.with, "model")
    if (model?.trim()) {
      try {
          await connection.setSessionConfigOption({ sessionId: acpSessionId, configId: "model", value: model })
          recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_config" }))
        } catch {
          try {
            await connection.unstable_setSessionModel({ sessionId: acpSessionId, modelId: model })
            recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_model" }))
          } catch {}
        }
      }

    await emitSessionStarted(context, acpSessionId, acp.processPid, agentConfig)
    await emitSessionEvent(context, "mohist_prompt", buildPromptEvent(context, prompt, acpSessionId))

    const promptResult = await monitorPrompt(context, connection, acpSessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
    })
    if (promptResult !== "completed") {
      return { text: agentText, success: false, error: promptResult.error, acpSessionId, exitCode: 1 }
    }

    return { text: agentText, success: true, acpSessionId, exitCode: 0 }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId, exitCode: 1 }
  } finally {
    acp.clearActiveHandlers()
  }
}

async function runNewWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const acp = context.acpConnection
  if (!acp) return runEphemeralWorkflowAgentSession(context, prompt)

  const connection = acp.connection
  const agentConfig = resolveAgentConfig(context.with)
  const timeoutMs = agentConfig?.timeoutMs ?? numberInput(context.with, "timeout") ?? DEFAULT_TIMEOUT_MS
  const sessionStartTimeoutMs = agentConfig?.sessionStartTimeoutMs ?? numberInput(context.with, "sessionStartTimeout") ?? DEFAULT_SESSION_START_TIMEOUT_MS
  let sessionId = ""
  let agentText = ""
  let agentTextTruncated = false
  const liveness = createSessionLivenessState()
  const dataWaiters = new Set<() => void>()
  const toolIds = new ToolCallIdGenerator()
  const notifyData = (activityType?: string) => {
    recordSessionLivenessActivity(liveness, activityType)
    for (const waiter of dataWaiters) waiter()
    dataWaiters.clear()
  }
  const appendAssistantText = (chunkText: string) => {
    if (agentTextTruncated) return
    agentText += chunkText
    if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
      agentText = truncateAgentText(agentText)
      agentTextTruncated = true
    }
  }

  acp.setActiveHandlers(
    createAcpSessionUpdateHandler({
      notifyData,
      appendAssistantText,
      emitUpdate: async (type, update) => {
        await emitSessionEvent(context, type, normalizeSessionUpdate(update as unknown as JsonObject, sessionId, toolIds))
      },
    }),
    async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
      const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
      return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
    },
  )

  try {
    const session = await Promise.race([
      connection.newSession({ cwd: context.workDir, mcpServers: [] }),
      timeout(sessionStartTimeoutMs),
    ])
    if (session === "timeout") throw new Error("Timed out during ACP newSession")
    sessionId = session.sessionId
    recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "new_session" }))

    const model = agentConfig?.model ?? stringInput(context.with, "model")
    if (model?.trim()) {
      try {
          await connection.setSessionConfigOption({ sessionId, configId: "model", value: model })
          recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_config" }))
        } catch {
          try {
            await connection.unstable_setSessionModel({ sessionId, modelId: model })
            recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_model" }))
          } catch {}
        }
      }

    await emitSessionStarted(context, sessionId, acp.processPid, agentConfig)
    await emitSessionEvent(context, "mohist_prompt", buildPromptEvent(context, prompt, sessionId))

    const promptResult = await monitorPrompt(context, connection, sessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
    })
    if (promptResult !== "completed") {
      try { await connection.closeSession?.({ sessionId }) } catch {}
      return { text: agentText, success: false, error: promptResult.error, acpSessionId: sessionId, exitCode: 1 }
    }

    return { text: agentText, success: true, acpSessionId: sessionId, exitCode: 0 }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, exitCode: 1 }
  } finally {
    acp.clearActiveHandlers()
  }
}

async function runEphemeralWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const acpProcess = getAcpProcessFactory()(context)
  const agentConfig = resolveAgentConfig(context.with)
  let sessionId = ""
  let agentText = ""
  let agentTextTruncated = false
  const liveness = createSessionLivenessState()
  const dataWaiters = new Set<() => void>()
  const toolIds = new ToolCallIdGenerator()
  const notifyData = (activityType?: string) => {
    recordSessionLivenessActivity(liveness, activityType)
    for (const waiter of dataWaiters) waiter()
    dataWaiters.clear()
  }
  const appendAssistantText = (chunkText: string) => {
    if (agentTextTruncated) return
    agentText += chunkText
    if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
      agentText = truncateAgentText(agentText)
      agentTextTruncated = true
    }
  }

  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: createAcpSessionUpdateHandler({
        notifyData,
        appendAssistantText,
        emitUpdate: async (type, update) => {
          await emitSessionEvent(context, type, normalizeSessionUpdate(update as unknown as JsonObject, sessionId, toolIds))
        },
      }),
      requestPermission: async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
        return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
      },
    }),
    acpProcess.stream,
  )

  try {
    const timeoutMs = agentConfig?.timeoutMs ?? numberInput(context.with, "timeout") ?? DEFAULT_TIMEOUT_MS
    const initialize = await Promise.race([
      connection.initialize({ protocolVersion: PROTOCOL_VERSION, clientInfo: { name: "mohist-runner", version: "0.1.0" } }),
      timeout(timeoutMs),
      acpProcess.spawnFailure,
    ])
    acpProcess.markInitialized()
    if (initialize === "timeout") throw new Error("Timed out during ACP initialize")
    recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "initialize" }))

    const session = await Promise.race([
      connection.newSession({ cwd: context.workDir, mcpServers: [] }),
      timeout(timeoutMs),
      acpProcess.exitFailure,
    ])
    if (session === "timeout") throw new Error("Timed out during ACP newSession")
    sessionId = session.sessionId
    recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "new_session" }))

    const model = agentConfig?.model ?? stringInput(context.with, "model")
    if (model?.trim()) {
      try {
          await connection.setSessionConfigOption({ sessionId, configId: "model", value: model })
          recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_config" }))
        } catch {
          try {
            await connection.unstable_setSessionModel({ sessionId, modelId: model })
            recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_model" }))
          } catch {}
        }
      }

    await emitSessionStarted(context, sessionId, acpProcess.processPid, agentConfig)
    await emitSessionEvent(context, "mohist_prompt", buildPromptEvent(context, prompt, sessionId))

    const promptResult = await monitorPrompt(context, connection, sessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
      exitFailure: acpProcess.exitFailure,
    })
    if (promptResult !== "completed") {
      return { text: agentText, success: false, error: promptResult.error, acpSessionId: sessionId, exitCode: acpProcess.exitCode() }
    }

    return { text: agentText, success: true, acpSessionId: sessionId, exitCode: acpProcess.exitCode() ?? 0 }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: sessionId || undefined, exitCode: acpProcess.exitCode() ?? 1 }
  } finally {
    await acpProcess.cleanup()
  }
}

async function monitorPrompt(context: ActionContext, connection: ClientSideConnection, sessionId: string, prompt: string, options: { timeoutMs: number; livenessQuietThresholdMs: number; probeTimeoutMs: number; livenessState: SessionLivenessState; waitForData(version: number): Promise<"data">; exitFailure?: Promise<never> }): Promise<"completed" | { error: string }> {
  const startedAt = Date.now()
  const promptPromise = connection.prompt({ sessionId, prompt: [{ type: "text", text: prompt }] })
  const exitFailure = options.exitFailure ?? new Promise<never>(() => {})

  while (true) {
    const now = Date.now()
    const timeoutRemaining = startedAt + options.timeoutMs - now
    if (timeoutRemaining <= 0) return await cancelAndReturn(connection, sessionId, `Timed out after ${options.timeoutMs / 1000}s`)
    const quietRemaining = Math.max(0, options.livenessState.lastDataAt + options.livenessQuietThresholdMs - now)
    const waitMs = quietRemaining
    const result = await Promise.race([
      promptPromise.then(() => "completed" as const),
      timeout(Math.min(timeoutRemaining, Math.max(waitMs, 1))),
      aborted(context.signal),
      exitFailure.catch((error) => error),
    ])
    if (result === "completed") return "completed"
    if (result === "aborted") return await cancelAndReturn(connection, sessionId, "Agent stopped by user")
    if (result instanceof Error) {
      const failureReason: LivenessFailureReason = result.message.includes("[PROCESS_EXIT]") ? "process_exit" : "protocol_disconnect"
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason, postProbeActivity: hasPostProbeActivity(options.livenessState) })
      return { error: result.message }
    }
    if (Date.now() - options.livenessState.lastDataAt < options.livenessQuietThresholdMs) continue

    const activeProbe = beginLivenessProbe(options.livenessState, options.probeTimeoutMs)
    await emitLivenessStatusEvent(context, options.livenessState, "probing", { acpSessionId: sessionId, activeProbeVersion: activeProbe.probeVersion })
    try {
      await ensurePromptAcceptedOrPending(connection.prompt({ sessionId, prompt: [{ type: "text", text: PROBE_PROMPT }] }))
    } catch (error) {
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason: "probe_send_failed", activeProbeVersion: activeProbe.probeVersion, postProbeActivity: hasPostProbeActivity(options.livenessState) })
      const message = error instanceof Error ? error.message : String(error)
      return { error: `Failed to send liveness probe: ${message}` }
    }
    const probeResult = await Promise.race([
      promptPromise.then(() => "completed" as const),
      options.waitForData(activeProbe.probeVersion),
      timeout(options.probeTimeoutMs),
      aborted(context.signal),
      exitFailure.catch((error) => error),
    ])
    if (probeResult === "completed") return "completed"
    if (probeResult === "data" && probeWasSatisfied(options.livenessState)) {
      await emitLivenessStatusEvent(context, options.livenessState, "running", { acpSessionId: sessionId, satisfiedProbeVersion: activeProbe.probeVersion })
      clearLivenessProbe(options.livenessState)
      continue
    }
    if (probeResult === "aborted") return await cancelAndReturn(connection, sessionId, "Agent stopped by user")
    if (probeResult instanceof Error) {
      const failureReason: LivenessFailureReason = probeResult.message.includes("[PROCESS_EXIT]") ? "process_exit" : "protocol_disconnect"
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: hasPostProbeActivity(options.livenessState) })
      return { error: probeResult.message }
    }
    const probeState: LivenessProbeState = {
      probeSentAt: options.livenessState.probeSentAt,
      probeDeadlineAt: options.livenessState.probeDeadlineAt,
      probeVersion: options.livenessState.probeVersion,
      lastDataAt: options.livenessState.lastDataAt,
      ...(options.livenessState.lastActivityType ? { lastActivityType: options.livenessState.lastActivityType } : {}),
      dataVersion: options.livenessState.dataVersion,
      postProbeActivity: hasPostProbeActivity(options.livenessState),
    }
    await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason: "probe_timeout", activeProbeVersion: activeProbe.probeVersion, postProbeActivity: probeState.postProbeActivity })
    return { error: `Session liveness probe timed out ${JSON.stringify(probeState)}` }
  }
}

async function ensurePromptAcceptedOrPending(promptPromise: Promise<unknown>) {
  let settled = false
  let rejected: unknown
  void promptPromise.then(
    () => { settled = true },
    (error) => {
      settled = true
      rejected = error
    },
  )
  await new Promise<void>((resolve) => queueMicrotask(resolve))
  if (settled && rejected !== undefined) throw rejected
}

async function cancelAndReturn(connection: ClientSideConnection, sessionId: string, error: string) {
  try { await connection.cancel({ sessionId }) } catch {}
  return { error }
}

function waitForData(waiters: Set<() => void>, done: () => boolean): Promise<"data"> {
  if (done()) return Promise.resolve("data")
  return new Promise((resolve) => waiters.add(() => resolve("data")))
}

async function emitSessionStarted(context: ActionContext, agentSessionId: string, processPid: number | null, agentConfig: AgentConfig | undefined) {
  const sessionName = sessionNameFromContext(context)
  const projectId = context.projectId
  if (sessionName && context.serverConnection && projectId) {
    await context.serverConnection.attachWorkflowAgentSession(projectId, context.workflowRunId, sessionName, { agentSessionId, workDir: context.workDir, processPid, model: agentConfig?.model ?? stringInput(context.with, "model") }, context.signal)
  }
}

async function emitSessionEvent(context: ActionContext, type: string, payload: JsonObject) {
  const sessionName = sessionNameFromContext(context)
  const projectId = context.projectId
  if (sessionName && context.serverConnection && projectId) {
    await context.serverConnection.workflowAgentSessionEvents(projectId, context.workflowRunId, sessionName, { workId: context.workId, workType: context.workType, stage: context.stage, events: [{ type, payload }] }, context.signal)
  }
}

function sessionNameFromContext(context: ActionContext) {
  return stringInput(context.with, "session") ?? context.workId
}

function buildPromptEvent(context: ActionContext, prompt: string, sessionId: string): JsonObject {
  return { role: "mohist", text: prompt, kind: "task", sentAt: new Date().toISOString(), executionId: context.workId, stage: context.stage ?? null, title: context.title ?? null, issueId: context.issueNumber != null ? String(context.issueNumber) : null, acpSessionId: sessionId, outputPath: extractOutputPath(prompt) ?? null, contextFiles: extractContextFiles(prompt) ?? null }
}

function extractOutputPath(prompt: string) {
  const match = prompt.match(/<contract>([\s\S]*?)<\/contract>/i)
  return match ? match[1].trim().split("\n")[0]?.trim() : undefined
}

function extractContextFiles(prompt: string) {
  const match = prompt.match(/<context[-_]files>([\s\S]*?)<\/context[-_]files>/i)
  if (!match) return undefined
  const files = match[1].trim().split("\n").map((line) => line.trim()).filter((line) => line && !line.startsWith("<!--")).map((line) => line.match(/^@(\S+)/)?.[1] ?? line.match(/<file\s+path="([^"]+)"/i)?.[1] ?? line)
  return files.length > 0 ? files.slice(0, 5) : undefined
}

class ToolCallIdGenerator {
  private counter = 0
  private started = new Map<string, string[]>()
  next(sessionId: string, toolName: string, state: "started" | "completed") {
    if (state === "started") {
      const id = `${sessionId}-${toolName}-${this.counter++}`
      this.remember(sessionId, toolName, id)
      return id
    }
    const key = `${sessionId}-${toolName}`
    const ids = this.started.get(key) ?? []
    const id = ids.shift() ?? `${sessionId}-${toolName}-${this.counter++}`
    ids.length > 0 ? this.started.set(key, ids) : this.started.delete(key)
    return id
  }
  remember(sessionId: string, toolName: string, id: string) {
    const key = `${sessionId}-${toolName}`
    const ids = this.started.get(key) ?? []
    if (!ids.includes(id)) ids.push(id)
    this.started.set(key, ids)
  }
}

function normalizeSessionUpdate(update: JsonObject, sessionId: string, ids: ToolCallIdGenerator): JsonObject {
  const type = stringField(update, "sessionUpdate")
  if (type !== "tool_call" && type !== "tool_call_update") return update
  const nested = objectField(update, "toolCall") ?? {}
  const providerId = stringField(nested, "toolCallId") ?? stringField(update, "toolCallId") ?? stringField(update, "id") ?? stringField(update, "callId")
  const toolName = stringField(nested, "toolName") ?? stringField(nested, "name") ?? stringField(update, "toolName") ?? stringField(update, "name") ?? inferToolName(update) ?? "unknown"
  const status = stringField(nested, "status") ?? stringField(update, "status") ?? (type === "tool_call_update" ? "completed" : "in_progress")
  const state = status === "completed" ? "completed" : "started"
  const toolCallId = providerId ?? ids.next(sessionId, toolName, state)
  if (providerId && state === "started") ids.remember(sessionId, toolName, providerId)
  return {
    ...update,
    toolCall: cleanJson({
      ...nested,
      toolCallId,
      toolName,
      status,
      title: stringField(nested, "title") ?? stringField(update, "title") ?? toolName,
      input: nested.input ?? update.input ?? update.rawInput,
      output: nested.output ?? update.output ?? update.rawOutput,
      metadata: nested.metadata ?? update.metadata ?? null,
    }),
  }
}

function inferToolName(payload: unknown): string | undefined {
  if (typeof payload !== "object" || payload === null || Array.isArray(payload)) return undefined
  const record = payload as Record<string, unknown>
  const title = typeof record.title === "string" ? record.title.toLowerCase() : ""
  if (title.includes("bash") || title.includes("command")) return "bash"
  if (title.includes("patch")) return "apply_patch"
  for (const value of [record.rawInput, record.input, record.rawOutput, record.output]) {
    if (typeof value === "object" && value !== null && !Array.isArray(value)) {
      const nested = value as Record<string, unknown>
      if (typeof nested.command === "string" || typeof nested.script === "string") return "bash"
      if (typeof nested.patchText === "string" || typeof nested.patch === "string") return "apply_patch"
      if (typeof nested.pattern === "string") return "grep"
      if (typeof nested.filePath === "string" || typeof nested.file_path === "string" || typeof nested.path === "string") return "read"
    }
  }
  return undefined
}

function cleanJson(value: Record<string, unknown>): JsonObject {
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined)) as JsonObject
}

function stringField(value: JsonObject, key: string) {
  return typeof value[key] === "string" ? value[key] : undefined
}

function objectField(value: JsonObject, key: string): JsonObject | undefined {
  const found = value[key]
  return typeof found === "object" && found !== null && !Array.isArray(found) ? found as JsonObject : undefined
}

function createSpawnedAcpProcess(context: ActionContext): AcpProcessHandle {
  const command = acpCommand()
  const args = acpArgs()
  const proc = spawn(command, args, {
    cwd: context.workDir,
    stdio: ["pipe", "pipe", "inherit"],
    env: sanitizedEnvironment(),
  })
  return new SpawnedAcpProcess(proc)
}

class SpawnedAcpProcess implements AcpProcessHandle {
  private initialized = false
  private exited = false
  private code: number | null = null
  private rejectOnSpawn: ((error: Error) => void) | undefined
  private rejectOnExit: ((error: Error) => void) | undefined
  readonly spawnFailure: Promise<never>
  readonly exitFailure: Promise<never>
  readonly stream: Stream

  constructor(private readonly proc: ChildProcess) {
    this.spawnFailure = new Promise<never>((_, reject) => { this.rejectOnSpawn = reject })
    this.exitFailure = new Promise<never>((_, reject) => { this.rejectOnExit = reject })
    proc.on("error", (error) => {
      if (!this.initialized) this.rejectOnSpawn?.(new Error(`[SPAWN_FAILED] ${error.message}`))
    })
    proc.on("exit", (exitCode) => {
      this.exited = true
      this.code = exitCode
      try { proc.stdin?.destroy() } catch {}
      try { proc.stdout?.destroy() } catch {}
      if (!this.initialized && exitCode !== 0) this.rejectOnSpawn?.(new Error(`[SPAWN_FAILED] opencode acp exited before initialize (exit code: ${exitCode ?? "signal"})`))
      if (this.initialized && exitCode !== 0) this.rejectOnExit?.(new Error(`[PROCESS_EXIT] opencode acp exited unexpectedly (exit code: ${exitCode ?? "signal"})`))
    })
    proc.stdin?.on("error", () => {})
    proc.stdout?.on("error", () => {})
    this.stream = ndJsonStream(
      Writable.toWeb(proc.stdin!) as WritableStream<Uint8Array>,
      Readable.toWeb(proc.stdout!) as ReadableStream<Uint8Array>,
    )
  }

  get processPid() { return this.proc.pid ?? null }
  markInitialized() { this.initialized = true; this.rejectOnSpawn = undefined }
  exitCode() { return this.code }
  async cleanup() {
    await Promise.allSettled([
      this.stream.readable.cancel().catch(() => {}),
      this.stream.writable.abort().catch(() => {}),
    ])
    if (!this.exited) {
      killProcess(this.proc)
      setTimeout(() => {
        try { this.proc.kill("SIGKILL") } catch {}
      }, 5_000).unref?.()
    }
  }
}

function timeout(ms: number): Promise<"timeout"> {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve("timeout"), ms)
    if (ms > 10_000) timer.unref?.()
  })
}

function aborted(signal: AbortSignal): Promise<"aborted"> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve("aborted")
      return
    }
    signal.addEventListener("abort", () => resolve("aborted"), { once: true })
  })
}

function truncateAgentText(text: string) {
  if (text.length <= MAX_AGENT_TEXT_LENGTH) return text
  const keepLength = Math.floor(MAX_AGENT_TEXT_LENGTH / 2)
  const head = text.slice(0, keepLength)
  const tail = text.slice(-keepLength)
  return `${head}\n\n...[truncated ${text.length - MAX_AGENT_TEXT_LENGTH} characters]...\n\n${tail}`
}
