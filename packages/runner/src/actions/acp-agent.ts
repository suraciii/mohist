import { spawn, type ChildProcess } from "node:child_process"
import { Readable, Writable } from "node:stream"
import { ClientSideConnection, ndJsonStream, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput, objectInput, stringInput } from "../core/json.js"
import { resolvePrompt, type PromptLoaderContext } from "../core/prompt.js"
import { killProcess, runCommand, sanitizedEnvironment } from "../system/process.js"
import { verifyExpectations } from "./expectations.js"
import type { AcpSessionManager, SharedAcpConnection } from "../runtime/acp-connection.js"
import { acpArgs, acpCommand } from "../runtime/acp-command.js"
import { appendOpencodeDiagnostic, findOpencodeProviderErrorDiagnostic, type OpencodeProviderErrorDiagnostic } from "../runtime/opencode-log-diagnostics.js"

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

const DEFAULT_COMPACTION_THRESHOLD = 0.8
const DEFAULT_COMPACTION_STRATEGY = "summary"
const COMPACTION_META_KEY = "opencode.compaction"

const QUALIFYING_LIVENESS_NOTIFICATION_TYPES = new Set([
  "agent_message_chunk",
  "agent_thought_chunk",
  "tool_call",
  "tool_call_update",
  "tool_result",
  "tool_result_update",
  "usage_update",
  "compaction",
])

const SESSION_INPUT_EVENT = "session.input"
const SESSION_CLOSED_EVENT = "session.closed"
const SESSION_LIVENESS_EVENT = "session.liveness"
const MODEL_RESOLVED_EVENT = "model.resolved"
const USAGE_UPDATED_EVENT = "usage.updated"
const COMPACTION_EVENT = "compaction"

export type CompactionStrategy = "summary"

export interface CompactionConfig {
  threshold: number
  strategy: CompactionStrategy
}

interface CompactionEventPayload {
  contextWindowUsedBefore?: number
  contextWindowUsedAfter?: number
  contextWindowSize?: number
  strategy?: CompactionStrategy
}

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

function extractResolvedModelId(value: unknown): string | undefined {
  if (typeof value !== "object" || value === null) return undefined
  const models = (value as Record<string, unknown>).models
  if (typeof models !== "object" || models === null) return undefined
  const current = (models as Record<string, unknown>).currentModelId
  return typeof current === "string" && current.trim().length > 0 ? current : undefined
}

function extractResolvedModelFromConfigUpdate(value: unknown): string | undefined {
  if (typeof value !== "object" || value === null) return undefined
  const configOptions = (value as Record<string, unknown>).configOptions
  if (!Array.isArray(configOptions)) return undefined
  for (const entry of configOptions) {
    if (typeof entry !== "object" || entry === null) continue
    const option = entry as Record<string, unknown>
    const category = option.category
    if (category !== "model") continue
    const current = option.currentValue
    if (typeof current === "string" && current.trim().length > 0) return current
  }
  return undefined
}

function buildResolvedModelEventPayload(context: ActionContext, acpSessionId: string, resolvedModel: string, source: "newSession" | "resumeSession" | "config_option_update"): JsonObject {
  return cleanJson({
    sessionName: sessionNameFromContext(context),
    acpSessionId,
    workId: context.workId,
    workType: context.workType,
    stage: context.stage ?? null,
    resolvedModel,
    source,
  })
}

function buildUsageUpdatePayload(context: ActionContext, acpSessionId: string, source: "prompt_response" | "usage_update" | "compaction", usage?: unknown, update?: { cost?: unknown, size?: unknown, used?: unknown, compaction?: CompactionEventPayload }): JsonObject {
  const payload: JsonObject = cleanJson({
    sessionName: sessionNameFromContext(context),
    acpSessionId,
    workId: context.workId,
    workType: context.workType,
    stage: context.stage ?? null,
    source,
  })

  if (usage && typeof usage === "object") {
    const u = usage as Record<string, unknown>
    if (typeof u.inputTokens === "number") payload.inputTokens = u.inputTokens
    if (typeof u.outputTokens === "number") payload.outputTokens = u.outputTokens
    if (typeof u.totalTokens === "number") payload.totalTokens = u.totalTokens
    if (typeof u.cachedReadTokens === "number") payload.cachedReadTokens = u.cachedReadTokens
    if (typeof u.thoughtTokens === "number") payload.thoughtTokens = u.thoughtTokens
  }

  if (update) {
    if (update.cost && typeof update.cost === "object") {
      const c = update.cost as Record<string, unknown>
      if (typeof c.amount === "number") payload.costAmount = c.amount
      if (typeof c.currency === "string") payload.costCurrency = c.currency
    }
    if (typeof update.size === "number") payload.contextWindowSize = update.size
    if (typeof update.used === "number") payload.contextWindowUsed = update.used
    if (update.compaction) {
      const compaction = update.compaction
      if (typeof compaction.contextWindowUsedBefore === "number") payload.contextWindowUsedBefore = compaction.contextWindowUsedBefore
      if (typeof compaction.contextWindowUsedAfter === "number") payload.contextWindowUsedAfter = compaction.contextWindowUsedAfter
      if (typeof compaction.contextWindowSize === "number") payload.contextWindowSize = compaction.contextWindowSize
      if (typeof compaction.strategy === "string") payload.compactionStrategy = compaction.strategy
    }
  }

  return payload
}

function hasUsageUpdateContent(payload: JsonObject): boolean {
  return payload.contextWindowSize !== undefined
    || payload.contextWindowUsed !== undefined
    || payload.costAmount !== undefined
    || payload.costCurrency !== undefined
    || payload.inputTokens !== undefined
    || payload.outputTokens !== undefined
    || payload.totalTokens !== undefined
    || payload.cachedReadTokens !== undefined
    || payload.thoughtTokens !== undefined
    || payload.contextWindowUsedBefore !== undefined
    || payload.contextWindowUsedAfter !== undefined
    || payload.compactionStrategy !== undefined
}

function extractCompactionEventFromUpdate(update: unknown): CompactionEventPayload | undefined {
  if (!update || typeof update !== "object") return undefined
  const record = update as Record<string, unknown>
  const candidates: Array<Record<string, unknown>> = []
  if (record.compaction && typeof record.compaction === "object") {
    candidates.push(record.compaction as Record<string, unknown>)
  }
  const meta = record._meta
  if (meta && typeof meta === "object") {
    const metaRecord = meta as Record<string, unknown>
    if (metaRecord.compaction && typeof metaRecord.compaction === "object") {
      candidates.push(metaRecord.compaction as Record<string, unknown>)
    }
    if (metaRecord["opencode.compaction"] && typeof metaRecord["opencode.compaction"] === "object") {
      candidates.push(metaRecord["opencode.compaction"] as Record<string, unknown>)
    }
  }
  let before: number | undefined
  let after: number | undefined
  let size: number | undefined
  let strategyValue: unknown
  for (const source of candidates) {
    before ??= numberField(source, "contextWindowUsedBefore")
    after ??= numberField(source, "contextWindowUsedAfter")
    size ??= numberField(source, "contextWindowSize")
    if (strategyValue === undefined) strategyValue = source.strategy
  }
  const strategy: CompactionStrategy | undefined = strategyValue === "summary" ? "summary" : undefined
  if (before === undefined && after === undefined && size === undefined && strategy === undefined) {
    return undefined
  }
  return {
    contextWindowUsedBefore: before,
    contextWindowUsedAfter: after,
    contextWindowSize: size,
    strategy,
  }
}

function numberField(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key]
  return typeof value === "number" && Number.isFinite(value) ? value : undefined
}

function createObservabilityAwareEmitter(
  context: ActionContext,
  getAcpSessionId: () => string,
  toolIds: ToolCallIdGenerator,
): (type: string, update: SessionNotification["update"]) => Promise<void> {
  return async (type, update) => {
    const acpSessionId = getAcpSessionId()
    const normalized = normalizeSessionUpdate(update as unknown as JsonObject, acpSessionId, toolIds)
    await emitSessionEvent(context, genericSessionEventType(type, normalized), normalized)

    if (type === "config_option_update") {
      const resolvedModel = extractResolvedModelFromConfigUpdate(update as unknown)
      if (resolvedModel) {
        await emitSessionEvent(context, MODEL_RESOLVED_EVENT, buildResolvedModelEventPayload(context, acpSessionId, resolvedModel, "config_option_update"))
      }
    }

    if (type === "usage_update") {
      const u = update as unknown as Record<string, unknown>
      const compaction = extractCompactionEventFromUpdate(update)
      if (compaction && compaction.contextWindowSize === undefined && typeof u.size === "number") {
        compaction.contextWindowSize = u.size
      }
      const payload = buildUsageUpdatePayload(context, acpSessionId, compaction ? "compaction" : "usage_update", undefined, {
        cost: u.cost,
        size: u.size,
        used: u.used,
        compaction,
      })
      if (hasUsageUpdateContent(payload)) {
        await emitSessionEvent(context, USAGE_UPDATED_EVENT, payload)
        if (compaction) {
          await emitSessionEvent(context, COMPACTION_EVENT, buildCompactionEventPayload(context, acpSessionId, compaction))
        }
      }
    }
  }
}

function buildCompactionEventPayload(context: ActionContext, acpSessionId: string, compaction: CompactionEventPayload): JsonObject {
  return cleanJson({
    sessionName: sessionNameFromContext(context),
    acpSessionId,
    workId: context.workId,
    workType: context.workType,
    stage: context.stage ?? null,
    contextWindowUsedBefore: compaction.contextWindowUsedBefore,
    contextWindowUsedAfter: compaction.contextWindowUsedAfter,
    contextWindowSize: compaction.contextWindowSize,
    strategy: compaction.strategy,
  })
}

function createAcpSessionUpdateHandler(options: {
  notifyData(activityType?: string): void
  recordActivity?(): void
  appendAssistantText(text: string): void
  emitUpdate(type: string, update: SessionNotification["update"]): Promise<void>
}) {
  return async (notification: SessionNotification) => {
    const update = notification.update
    const type = update.sessionUpdate
    const activity = classifyAcpLivenessActivity({ kind: "session_update", update })
    if (activity.isActivity) {
      options.recordActivity?.()
      options.notifyData(activity.activityType)
    }

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
  compaction?: CompactionConfig
}

interface RequestedModel {
  model?: string
  source: "agent.model" | "with.model" | "none"
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
  providerError?: OpencodeProviderErrorDiagnostic
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
    providerError: extras?.providerError as JsonObject | undefined,
  })
}

async function emitLivenessStatusEvent(context: ActionContext, state: SessionLivenessState, status: "probing" | "running" | "failed", extras?: {
  acpSessionId?: string
  activeProbeVersion?: number
  satisfiedProbeVersion?: number
  failureReason?: LivenessFailureReason
  providerError?: OpencodeProviderErrorDiagnostic
  postProbeActivity?: boolean
}) {
  await emitSessionEvent(context, SESSION_LIVENESS_EVENT, buildLivenessEventPayload(context, state, status, extras))
}

interface AcpSessionResult {
  text: string
  success: boolean
  error?: string
  acpSessionId?: string
  exitCode?: number | null
  activityCount?: number
  providerError?: OpencodeProviderErrorDiagnostic
  failureCategory?: LivenessFailureReason
}

export async function acpAgentAction(context: ActionContext): Promise<ActionResult> {
  const resolved = await resolveActionPrompt(context)
  if (resolved.error) return { status: "failure", message: resolved.error }
  const prompt = buildPromptWithMohistContext(context, resolved.prompt)
  if (!prompt?.trim()) return { status: "failure", message: "ACP agent requires 'prompt'" }

  const result = await runAcpWorkflowAgentSession(context, prompt)
  await restoreAgentToolNoise(context)
  const verification = await verifyExpectations(context)
  const ok = result.success && verification.satisfied
  const agentConfig = resolveAgentConfig(context.with)
  const failureCategory = ok ? null : result.failureCategory ?? null
  await emitSessionEvent(context, SESSION_CLOSED_EVENT, { status: ok ? "completed" : "failed", failureReason: ok ? null : result.error ?? verification.message, failureCategory, exitCode: result.exitCode ?? (ok ? 0 : 1) })
  return {
    status: ok ? "success" : "failure",
    message: ok ? "ACP agent task completed" : result.error ?? verification.message,
    output: JSON.stringify({ kind: "acp-agent", status: ok ? "success" : "failure", acpSessionId: result.acpSessionId, model: agentConfig?.model, text: result.text, error: result.error, providerError: result.providerError, expectation: verification }),
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
      compaction: resolveCompactionConfigFromInput(agent as JsonObject),
    }
  }
  return {
    model: stringInput(with_, "model") ?? undefined,
    timeoutMs: numberInput(with_, "timeout") ?? undefined,
    sessionStartTimeoutMs: numberInput(with_, "sessionStartTimeout") ?? undefined,
    livenessQuietThresholdMs: numberInput(with_, "livenessQuietThresholdMs") ?? undefined,
    probeTimeoutMs: numberInput(with_, "probeTimeoutMs") ?? undefined,
    compaction: resolveCompactionConfigFromInput(with_),
  }
}

function resolveCompactionConfigFromInput(input: JsonObject | null | undefined): CompactionConfig | undefined {
  if (!input || typeof input !== "object") return undefined
  const raw = objectInput(input, "compaction")
  if (!raw || typeof raw !== "object") return undefined
  const thresholdValue = numberInput(raw as JsonObject, "threshold")
  const strategyValue = stringInput(raw as JsonObject, "strategy")
  if (thresholdValue === undefined && strategyValue === undefined) return undefined
  return {
    threshold: thresholdValue !== undefined && Number.isFinite(thresholdValue) && thresholdValue >= 0 && thresholdValue <= 1
      ? thresholdValue
      : DEFAULT_COMPACTION_THRESHOLD,
    strategy: strategyValue === "summary" ? "summary" : DEFAULT_COMPACTION_STRATEGY,
  }
}

export function resolveCompactionConfig(agentConfig?: AgentConfig): CompactionConfig {
  if (!agentConfig?.compaction) return defaultCompactionConfig()
  return {
    threshold: agentConfig.compaction.threshold,
    strategy: agentConfig.compaction.strategy,
  }
}

export function defaultCompactionConfig(): CompactionConfig {
  return {
    threshold: DEFAULT_COMPACTION_THRESHOLD,
    strategy: DEFAULT_COMPACTION_STRATEGY,
  }
}

function buildSessionMeta(compaction: CompactionConfig): { [key: string]: unknown } {
  return {
    [COMPACTION_META_KEY]: {
      threshold: compaction.threshold,
      strategy: compaction.strategy,
    },
  }
}

function resolveRequestedModel(context: ActionContext, agentConfig?: AgentConfig): RequestedModel {
  const agentModel = agentConfig?.model
  if (agentModel?.trim()) return { model: agentModel, source: "agent.model" }
  const withModel = stringInput(context.with, "model")
  if (withModel?.trim()) return { model: withModel, source: "with.model" }
  return { source: "none" }
}

async function applyRequestedModel(connection: ClientSideConnection, context: ActionContext, sessionId: string, requested: RequestedModel, notify: (activityType?: string) => void) {
  if (!requested.model?.trim()) {
    console.warn("mohist acp model not configured; using provider default", modelDiagnosticContext(context, requested))
    return
  }

  console.info("mohist acp setting requested model", modelDiagnosticContext(context, requested))
  try {
    await connection.setSessionConfigOption({ sessionId, configId: "model", value: requested.model })
    recordLivenessActivity(notify, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_config" }))
    console.info("mohist acp set model via config option", modelDiagnosticContext(context, requested))
  } catch (configError) {
    console.warn("mohist acp set model via config option failed; trying set_session_model", { ...modelDiagnosticContext(context, requested), error: errorMessage(configError) })
    try {
      await connection.unstable_setSessionModel({ sessionId, modelId: requested.model })
      recordLivenessActivity(notify, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_model" }))
      console.info("mohist acp set model via set_session_model", modelDiagnosticContext(context, requested))
    } catch (modelError) {
      console.warn("mohist acp set requested model failed; provider default may be used", { ...modelDiagnosticContext(context, requested), error: errorMessage(modelError) })
    }
  }
}

function modelDiagnosticContext(context: ActionContext, requested: RequestedModel) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: sessionNameFromContext(context),
    requestedModel: requested.model ?? null,
    requestedModelSource: requested.source,
  }
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : String(error)
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

async function resolveActionPrompt(context: ActionContext): Promise<{ prompt?: string; error?: string }> {
  const promptSpec = context.with?.["prompt"]
  if (promptSpec === undefined || promptSpec === null) {
    return { prompt: buildFallbackPrompt(context) }
  }
  try {
    return { prompt: await resolvePrompt(promptSpec, buildPromptLoaderContext(context)) }
  } catch (error) {
    return { error: error instanceof Error ? error.message : String(error) }
  }
}

function buildPromptLoaderContext(context: ActionContext): PromptLoaderContext {
  return {
    with: {},
    variables: context.variables ?? {},
    workDir: context.workDir,
    workId: context.workId,
    title: context.title ?? null,
    stage: context.stage ?? null,
    issueNumber: context.issueNumber ?? null,
  }
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

  if (sessionName && manager && context.serverConnection && projectId) {
    const key = manager.key(context.workflowRunId, sessionName)
    const existing = await context.serverConnection.getWorkflowAgentSession(projectId, context.workflowRunId, sessionName, context.signal)
    const session = existing ?? await context.serverConnection.openWorkflowAgentSession(projectId, context.workflowRunId, sessionName, {
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

  if (sessionName && context.serverConnection && projectId) {
    await context.serverConnection.openWorkflowAgentSession(projectId, context.workflowRunId, sessionName, {
      workId: context.workId,
      workType: context.workType,
      stage: context.stage,
      title: context.title,
      issueNumber: context.issueNumber,
    }, context.signal)
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
  let activityCount = 0
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

  acp.setSessionHandlers(
    entry.sessionId,
    createAcpSessionUpdateHandler({
      notifyData,
      recordActivity: () => { activityCount += 1 },
      appendAssistantText,
      emitUpdate: createObservabilityAwareEmitter(context, () => entry.sessionId, toolIds),
    }),
    async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
      const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
      return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
    },
  )

  await emitSessionStarted(context, entry.sessionId, acp.processPid, agentConfig)
  await emitSessionEvent(context, SESSION_INPUT_EVENT, buildPromptEvent(context, prompt, entry.sessionId))

  try {
    const promptResult = await monitorPrompt(context, connection, entry.sessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
    })
    if (promptResult !== "completed") {
      return { text: agentText, success: false, error: promptResult.error, acpSessionId: entry.sessionId, exitCode: 1, activityCount, providerError: promptResult.providerError, failureCategory: promptResult.failureReason }
    }
    const activityFailure = validatePromptActivity(activityCount)
    if (activityFailure) return { text: agentText, success: false, error: activityFailure, acpSessionId: entry.sessionId, exitCode: 1, activityCount }
    return { text: agentText, success: true, acpSessionId: entry.sessionId, exitCode: 0, activityCount }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: entry.sessionId, exitCode: 1, activityCount }
  } finally {
    acp.clearSessionHandlers(entry.sessionId)
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
  let activityCount = 0
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

  try {
    const resumeResult = await Promise.race([
      connection.resumeSession({ sessionId: acpSessionId, cwd: workDir, mcpServers: [], _meta: buildSessionMeta(resolveCompactionConfig(agentConfig)) }),
      timeout(sessionStartTimeoutMs),
    ])
    if (resumeResult === "timeout") throw new Error("Timed out during ACP resumeSession")
    recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "resume_session" }))

    const resolvedModel = extractResolvedModelId(resumeResult)

    await applyRequestedModel(connection, context, acpSessionId, resolveRequestedModel(context, agentConfig), notifyData)

    acp.setSessionHandlers(
      acpSessionId,
      createAcpSessionUpdateHandler({
        notifyData,
        recordActivity: () => { activityCount += 1 },
        appendAssistantText,
        emitUpdate: createObservabilityAwareEmitter(context, () => acpSessionId, toolIds),
      }),
      async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
        return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
      },
    )

    await emitSessionStarted(context, acpSessionId, acp.processPid, agentConfig, resolvedModel, "resumeSession")
    await emitSessionEvent(context, SESSION_INPUT_EVENT, buildPromptEvent(context, prompt, acpSessionId))

    const promptResult = await monitorPrompt(context, connection, acpSessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
    })
    if (promptResult !== "completed") {
      return { text: agentText, success: false, error: promptResult.error, acpSessionId, exitCode: 1, activityCount, providerError: promptResult.providerError, failureCategory: promptResult.failureReason }
    }

    const activityFailure = validatePromptActivity(activityCount)
    if (activityFailure) return { text: agentText, success: false, error: activityFailure, acpSessionId, exitCode: 1, activityCount }

    return { text: agentText, success: true, acpSessionId, exitCode: 0, activityCount }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId, exitCode: 1, activityCount }
  } finally {
    acp.clearSessionHandlers(acpSessionId)
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
  let activityCount = 0
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

  try {
    const session = await Promise.race([
      connection.newSession({ cwd: context.workDir, mcpServers: [], _meta: buildSessionMeta(resolveCompactionConfig(agentConfig)) }),
      timeout(sessionStartTimeoutMs),
    ])
    if (session === "timeout") throw new Error("Timed out during ACP newSession")
    sessionId = session.sessionId
    recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "new_session" }))
    const resolvedModel = extractResolvedModelId(session)
    await attachSessionToServer(context, sessionId, acp.processPid, agentConfig, resolvedModel)

    acp.setSessionHandlers(
      sessionId,
      createAcpSessionUpdateHandler({
        notifyData,
        recordActivity: () => { activityCount += 1 },
        appendAssistantText,
        emitUpdate: createObservabilityAwareEmitter(context, () => sessionId, toolIds),
      }),
      async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
        return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
      },
    )

    await applyRequestedModel(connection, context, sessionId, resolveRequestedModel(context, agentConfig), notifyData)

    await emitResolvedModelEvent(context, sessionId, resolvedModel, "newSession")
    await emitSessionEvent(context, SESSION_INPUT_EVENT, buildPromptEvent(context, prompt, sessionId))

    const promptResult = await monitorPrompt(context, connection, sessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
    })
    if (promptResult !== "completed") {
      try { await connection.closeSession?.({ sessionId }) } catch {}
      return { text: agentText, success: false, error: promptResult.error, acpSessionId: sessionId, exitCode: 1, activityCount, providerError: promptResult.providerError, failureCategory: promptResult.failureReason }
    }

    const activityFailure = validatePromptActivity(activityCount)
    if (activityFailure) {
      try { await connection.closeSession?.({ sessionId }) } catch {}
      return { text: agentText, success: false, error: activityFailure, acpSessionId: sessionId, exitCode: 1, activityCount }
    }

    return { text: agentText, success: true, acpSessionId: sessionId, exitCode: 0, activityCount }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, exitCode: 1, activityCount }
  } finally {
    if (sessionId) acp.clearSessionHandlers(sessionId)
  }
}

async function runEphemeralWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const acpProcess = getAcpProcessFactory()(context)
  const agentConfig = resolveAgentConfig(context.with)
  let sessionId = ""
  let agentText = ""
  let agentTextTruncated = false
  let activityCount = 0
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
        recordActivity: () => { activityCount += 1 },
        appendAssistantText,
        emitUpdate: createObservabilityAwareEmitter(context, () => sessionId, toolIds),
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
      connection.newSession({ cwd: context.workDir, mcpServers: [], _meta: buildSessionMeta(resolveCompactionConfig(agentConfig)) }),
      timeout(timeoutMs),
      acpProcess.exitFailure,
    ])
    if (session === "timeout") throw new Error("Timed out during ACP newSession")
    sessionId = session.sessionId
    recordLivenessActivity(notifyData, classifyAcpLivenessActivity({ kind: "protocol_response", response: "new_session" }))
    const resolvedModel = extractResolvedModelId(session)
    await attachSessionToServer(context, sessionId, acpProcess.processPid, agentConfig, resolvedModel)

    await applyRequestedModel(connection, context, sessionId, resolveRequestedModel(context, agentConfig), notifyData)

    await emitResolvedModelEvent(context, sessionId, resolvedModel, "newSession")
    await emitSessionEvent(context, SESSION_INPUT_EVENT, buildPromptEvent(context, prompt, sessionId))

    const promptResult = await monitorPrompt(context, connection, sessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      livenessState: liveness,
      waitForData: (version) => waitForData(dataWaiters, () => liveness.dataVersion !== version),
      exitFailure: acpProcess.exitFailure,
    })
    if (promptResult !== "completed") {
      return { text: agentText, success: false, error: promptResult.error, acpSessionId: sessionId, exitCode: acpProcess.exitCode(), activityCount, providerError: promptResult.providerError, failureCategory: promptResult.failureReason }
    }

    const activityFailure = validatePromptActivity(activityCount)
    if (activityFailure) return { text: agentText, success: false, error: activityFailure, acpSessionId: sessionId, exitCode: acpProcess.exitCode() ?? 1, activityCount }

    return { text: agentText, success: true, acpSessionId: sessionId, exitCode: acpProcess.exitCode() ?? 0, activityCount }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: sessionId || undefined, exitCode: acpProcess.exitCode() ?? 1, activityCount }
  } finally {
    await acpProcess.cleanup()
  }
}

function validatePromptActivity(activityCount: number) {
  return activityCount > 0 ? undefined : "ACP agent prompt completed without any session activity"
}

async function monitorPrompt(context: ActionContext, connection: ClientSideConnection, sessionId: string, prompt: string, options: { timeoutMs: number; livenessQuietThresholdMs: number; probeTimeoutMs: number; livenessState: SessionLivenessState; waitForData(version: number): Promise<"data">; exitFailure?: Promise<never> }): Promise<"completed" | { error: string; providerError?: OpencodeProviderErrorDiagnostic; failureReason?: LivenessFailureReason }> {
  const startedAt = Date.now()
  const promptPromise = connection.prompt({ sessionId, prompt: [{ type: "text", text: prompt }] })
  let promptUsage: unknown
  promptPromise.then(
    (response) => { promptUsage = response.usage },
    () => {},
  )
  const promptOutcome = promptPromise.then(() => "completed" as const, (error: unknown) => toError(error))
  const exitFailure = options.exitFailure ?? new Promise<never>(() => {})

  const emitPromptUsageIfAppropriate = async () => {
    if (!promptUsage || typeof promptUsage !== "object") return
    const payload = buildUsageUpdatePayload(context, sessionId, "prompt_response", promptUsage)
    if (!hasUsageUpdateContent(payload)) return
    await emitSessionEvent(context, USAGE_UPDATED_EVENT, payload)
  }

  while (true) {
    const now = Date.now()
    const timeoutRemaining = startedAt + options.timeoutMs - now
    if (timeoutRemaining <= 0) return await cancelAndReturn(connection, sessionId, `Timed out after ${options.timeoutMs / 1000}s`)
    const quietRemaining = Math.max(0, options.livenessState.lastDataAt + options.livenessQuietThresholdMs - now)
    const waitMs = quietRemaining
    const result = await Promise.race([
      promptOutcome,
      timeout(Math.min(timeoutRemaining, Math.max(waitMs, 1))),
      aborted(context.signal),
      exitFailure.catch((error) => error),
    ])
    if (result === "completed") {
      await emitPromptUsageIfAppropriate()
      return "completed"
    }
    if (result === "aborted") return await cancelAndReturn(connection, sessionId, "Agent stopped by user")
    if (result instanceof Error) {
      const failureReason: LivenessFailureReason = result.message.includes("[PROCESS_EXIT]") ? "process_exit" : "protocol_disconnect"
      const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason, providerError: diagnostic, postProbeActivity: hasPostProbeActivity(options.livenessState) })
      return { error: appendOpencodeDiagnostic(result.message, diagnostic), providerError: diagnostic, failureReason }
    }
    if (Date.now() - options.livenessState.lastDataAt < options.livenessQuietThresholdMs) continue

    const activeProbe = beginLivenessProbe(options.livenessState, options.probeTimeoutMs)
    await emitLivenessStatusEvent(context, options.livenessState, "probing", { acpSessionId: sessionId, activeProbeVersion: activeProbe.probeVersion })
    try {
      await ensurePromptAcceptedOrPending(connection.prompt({ sessionId, prompt: [{ type: "text", text: PROBE_PROMPT }] }))
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason: "probe_send_failed", providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: hasPostProbeActivity(options.livenessState) })
      return { error: appendOpencodeDiagnostic(`Failed to send liveness probe: ${message}`, diagnostic), providerError: diagnostic, failureReason: "probe_send_failed" }
    }
    const probeResult = await Promise.race([
      promptOutcome,
      options.waitForData(activeProbe.probeVersion),
      timeout(options.probeTimeoutMs),
      aborted(context.signal),
      exitFailure.catch((error) => error),
    ])
    if (probeResult === "completed" && hasPostProbeActivity(options.livenessState)) {
      await emitPromptUsageIfAppropriate()
      return "completed"
    }
    if (probeResult === "completed") {
      const probeState: LivenessProbeState = {
        probeSentAt: options.livenessState.probeSentAt,
        probeDeadlineAt: options.livenessState.probeDeadlineAt,
        probeVersion: options.livenessState.probeVersion,
        lastDataAt: options.livenessState.lastDataAt,
        ...(options.livenessState.lastActivityType ? { lastActivityType: options.livenessState.lastActivityType } : {}),
        dataVersion: options.livenessState.dataVersion,
        postProbeActivity: hasPostProbeActivity(options.livenessState),
      }
      const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason: "probe_timeout", providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: probeState.postProbeActivity })
      return { error: diagnostic ? diagnostic.summary : `Session liveness probe timed out ${JSON.stringify(probeState)}`, providerError: diagnostic, failureReason: "probe_timeout" }
    }
    if (probeResult === "data" && probeWasSatisfied(options.livenessState)) {
      await emitLivenessStatusEvent(context, options.livenessState, "running", { acpSessionId: sessionId, satisfiedProbeVersion: activeProbe.probeVersion })
      clearLivenessProbe(options.livenessState)
      continue
    }
    if (probeResult === "aborted") return await cancelAndReturn(connection, sessionId, "Agent stopped by user")
    if (probeResult instanceof Error) {
      const failureReason: LivenessFailureReason = probeResult.message.includes("[PROCESS_EXIT]") ? "process_exit" : "protocol_disconnect"
      const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
      await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason, providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: hasPostProbeActivity(options.livenessState) })
      return { error: appendOpencodeDiagnostic(probeResult.message, diagnostic), providerError: diagnostic, failureReason }
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
    const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
    await emitLivenessStatusEvent(context, options.livenessState, "failed", { acpSessionId: sessionId, failureReason: "probe_timeout", providerError: diagnostic, activeProbeVersion: activeProbe.probeVersion, postProbeActivity: probeState.postProbeActivity })
    return { error: diagnostic ? diagnostic.summary : `Session liveness probe timed out ${JSON.stringify(probeState)}`, providerError: diagnostic, failureReason: "probe_timeout" }
  }
}

function toError(error: unknown) {
  return error instanceof Error ? error : new Error(String(error))
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

async function emitSessionStarted(context: ActionContext, agentSessionId: string, processPid: number | null, agentConfig: AgentConfig | undefined, resolvedModel?: string, resolvedModelSource: "newSession" | "resumeSession" = "newSession") {
  await attachSessionToServer(context, agentSessionId, processPid, agentConfig, resolvedModel)
  await emitResolvedModelEvent(context, agentSessionId, resolvedModel, resolvedModelSource)
}

async function attachSessionToServer(context: ActionContext, agentSessionId: string, processPid: number | null, agentConfig: AgentConfig | undefined, resolvedModel?: string) {
  const target = sessionTargetFromContext(context)
  if (!target || !context.serverConnection) return

  await context.serverConnection.attachWorkflowAgentSession(
    target.projectId,
    target.workflowRunId,
    target.sessionName,
    { agentSessionId, workDir: context.workDir, processPid, model: agentConfig?.model ?? stringInput(context.with, "model"), ...(resolvedModel ? { resolvedModel } : {}) },
    context.signal)
}

async function emitResolvedModelEvent(context: ActionContext, agentSessionId: string, resolvedModel: string | undefined, resolvedModelSource: "newSession" | "resumeSession" | "config_option_update") {
  const sessionName = sessionNameFromContext(context)
  if (resolvedModel && sessionName) {
    await emitSessionEvent(context, MODEL_RESOLVED_EVENT, buildResolvedModelEventPayload(context, agentSessionId, resolvedModel, resolvedModelSource))
  }
}

async function emitSessionEvent(context: ActionContext, type: string, payload: JsonObject) {
  const target = sessionTargetFromContext(context)
  if (!target || !context.serverConnection) return

  await context.serverConnection.workflowAgentSessionRuntimeEvents(
    target.projectId,
    target.workflowRunId,
    target.sessionName,
    { workId: context.workId, workType: context.workType, stage: context.stage, runtimeEvents: [{ type, payload }] },
    context.signal)
}

function sessionTargetFromContext(context: ActionContext): { projectId: string; workflowRunId: string; sessionName: string } | null {
  const sessionName = sessionNameFromContext(context)
  const projectId = context.projectId
  if (!sessionName || !projectId) return null
  return { projectId, workflowRunId: context.workflowRunId, sessionName }
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

function genericSessionEventType(type: string, payload: JsonObject): string {
  switch (type) {
    case "agent_message_chunk":
    case "agent_output_chunk":
      return "message.delta"
    case "agent_thought_chunk":
      return "reasoning.delta"
    case "tool_call":
      return "tool_call.started"
    case "tool_call_update": {
      const nested = objectField(payload, "toolCall") ?? {}
      const status = stringField(nested, "status") ?? stringField(payload, "status")
      return status && ["completed", "failed", "cancelled", "timeout"].includes(status)
        ? "tool_call.completed"
        : "tool_call.updated"
    }
    default:
      return type
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
