import type { SessionNotification } from "@agentclientprotocol/sdk"
import type { ActionContext, JsonObject } from "../../core/types.js"
import { stringInput } from "../../core/json.js"
import type { SessionTarget } from "../../runtime/acp-connection.js"
import type { CompactionEventPayload, CompactionStrategy } from "./compaction.js"

const SESSION_INPUT_EVENT = "session.input"
const SESSION_LIVENESS_EVENT = "session.liveness"
const MODEL_RESOLVED_EVENT = "model.resolved"
const USAGE_UPDATED_EVENT = "usage.updated"
const COMPACTION_EVENT = "compaction"

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



export function cleanJson(value: Record<string, unknown>): JsonObject {
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined)) as JsonObject
}

export function stringField(value: JsonObject, key: string) {
  return typeof value[key] === "string" ? value[key] : undefined
}

export function objectField(value: JsonObject, key: string): JsonObject | undefined {
  const found = value[key]
  return typeof found === "object" && found !== null && !Array.isArray(found) ? found as JsonObject : undefined
}

export function numberField(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key]
  return typeof value === "number" && Number.isFinite(value) ? value : undefined
}

export function sessionNameFromContext(context: ActionContext) {
  return stringInput(context.with, "session") ?? context.workId
}

export function sessionTargetFromContext(context: ActionContext): SessionTarget | null {
  const projectId = context.projectId
  if (!projectId) return null

  if (context.ownerKind === "agent-job") {
    const agentSessionId = context.agentSessionId?.trim()
    return agentSessionId
      ? { kind: "generic", projectId, sessionId: agentSessionId }
      : null
  }

  const sessionName = sessionNameFromContext(context)
  if (!sessionName) return null
  return { kind: "workflow", projectId, workflowRunId: context.workflowRunId, sessionName }
}

/**
 * Per-process guard so an unresolved agent-job session target is logged
 * once per work item, not once per event. Holds even when the same
 * `ActionContext` is shared across many `emitSessionEvent` calls in a
 * prompt loop, so a noisy drop never becomes a log flood. The closure
 * is keyed by identity of the context object so different work items
 * (each with its own context) get their own latch.
 */
const unresolvedAgentJobTargetWarned = new WeakSet<ActionContext>()
const missingServerConnectionWarned = new WeakSet<ActionContext>()

export async function emitSessionEvent(
  context: ActionContext,
  type: string,
  payload: JsonObject,
  runtimeSessionId: string | null = stringField(payload, "runtimeSessionId") ?? null,
) {
  const target = sessionTargetFromContext(context)
  if (!target) {
    if (context.ownerKind === "agent-job" && !unresolvedAgentJobTargetWarned.has(context)) {
      unresolvedAgentJobTargetWarned.add(context)
      const agentJobId = context.agentJobId ?? "unknown"
      context.log?.write(
        "action:session-events",
        `unresolved generic session target — events dropped workId=${context.workId} agentJobId=${agentJobId} type=${type}`,
      )
    }
    return
  }

  if (!context.serverConnection) {
    if (context.ownerKind === "agent-job" && !missingServerConnectionWarned.has(context)) {
      missingServerConnectionWarned.add(context)
      const agentJobId = context.agentJobId ?? "unknown"
      context.log?.write(
        "action:session-events",
        `missing server connection — session events dropped workId=${context.workId} agentJobId=${agentJobId} type=${type}`,
      )
    }
    return
  }

  if (!runtimeSessionId) return

  const body = { workId: context.workId, workType: context.workType, stage: context.stage, runtimeSessionId, runtimeEvents: [{ type, payload }] }
  if (target.kind === "workflow") {
    await context.serverConnection.workflowAgentSessionRuntimeEvents(
      target.projectId,
      target.workflowRunId,
      target.sessionName,
      body,
      context.signal)
    return
  }
  await context.serverConnection.agentSessionRuntimeEvents(
    target.projectId,
    target.sessionId,
    body,
    context.signal)
}

export function buildResolvedModelEventPayload(context: ActionContext, runtimeSessionId: string, resolvedModel: string, source: "newSession" | "resumeSession" | "config_option_update"): JsonObject {
  return cleanJson({
    sessionName: sessionNameFromContext(context),
    runtimeSessionId,
    workId: context.workId,
    workType: context.workType,
    stage: context.stage ?? null,
    resolvedModel,
    source,
  })
}

export function buildUsageUpdatePayload(context: ActionContext, runtimeSessionId: string, source: "prompt_response" | "usage_update" | "compaction", usage?: unknown, update?: { cost?: unknown, size?: unknown, used?: unknown, compaction?: CompactionEventPayload }): JsonObject {
  const payload: JsonObject = cleanJson({
    sessionName: sessionNameFromContext(context),
    runtimeSessionId,
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

export function hasUsageUpdateContent(payload: JsonObject): boolean {
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

export function buildCompactionEventPayload(context: ActionContext, runtimeSessionId: string, compaction: CompactionEventPayload): JsonObject {
  return cleanJson({
    sessionName: sessionNameFromContext(context),
    runtimeSessionId,
    workId: context.workId,
    workType: context.workType,
    stage: context.stage ?? null,
    contextWindowUsedBefore: compaction.contextWindowUsedBefore,
    contextWindowUsedAfter: compaction.contextWindowUsedAfter,
    contextWindowSize: compaction.contextWindowSize,
    strategy: compaction.strategy,
  })
}

export function buildLivenessEventPayload(context: ActionContext, state: { lastDataAt: number; lastActivityType?: string; probeSentAt?: string; probeDeadlineAt?: string; probeVersion?: number; dataVersion: number }, status: "probing" | "running" | "failed", extras?: {
  runtimeSessionId?: string
  activeProbeVersion?: number
  satisfiedProbeVersion?: number
  failureReason?: string
  providerError?: unknown
  postProbeActivity?: boolean
}): JsonObject {
  return cleanJson({
    sessionName: sessionNameFromContext(context),
    runtimeSessionId: extras?.runtimeSessionId ?? null,
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

export async function emitLivenessStatusEvent(context: ActionContext, state: { lastDataAt: number; lastActivityType?: string; probeSentAt?: string; probeDeadlineAt?: string; probeVersion?: number; dataVersion: number }, status: "probing" | "running" | "failed", extras?: {
  runtimeSessionId?: string
  activeProbeVersion?: number
  satisfiedProbeVersion?: number
  failureReason?: string
  providerError?: unknown
  postProbeActivity?: boolean
}) {
  await emitSessionEvent(context, SESSION_LIVENESS_EVENT, buildLivenessEventPayload(context, state, status, extras), extras?.runtimeSessionId ?? null)
}

export function buildPromptEvent(context: ActionContext, prompt: string, sessionId: string): JsonObject {
  return { role: "mohist", text: prompt, kind: "task", sentAt: new Date().toISOString(), executionId: context.workId, stage: context.stage ?? null, title: context.title ?? null, issueId: context.issueNumber != null ? String(context.issueNumber) : null, runtimeSessionId: sessionId, outputPath: extractOutputPath(prompt) ?? null, contextFiles: extractContextFiles(prompt) ?? null }
}

export function extractOutputPath(prompt: string) {
  const match = prompt.match(/<contract>([\s\S]*?)<\/contract>/i)
  return match ? match[1].trim().split("\n")[0]?.trim() : undefined
}

export function extractContextFiles(prompt: string) {
  const match = prompt.match(/<context[-_]files>([\s\S]*?)<\/context[-_]files>/i)
  if (!match) return undefined
  const files = match[1].trim().split("\n").map((line) => line.trim()).filter((line) => line && !line.startsWith("<!--")).map((line) => line.match(/^@(\S+)/)?.[1] ?? line.match(/<file\s+path="([^"]+)"/i)?.[1] ?? line)
  return files.length > 0 ? files.slice(0, 5) : undefined
}

type AcpLivenessActivity =
  | { isActivity: false }
  | { isActivity: true; activityType: string }

export function classifyAcpLivenessActivity(source:
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

export function recordLivenessActivity(notify: (activityType?: string) => void, activity: AcpLivenessActivity) {
  if (activity.isActivity) notify(activity.activityType)
}

export function isPromptWorkActivity(activityType: string): boolean {
  return activityType !== "usage_update"
}

export function assistantMessageChunkText(update: SessionNotification["update"]): string | undefined {
  if (update.sessionUpdate !== "agent_message_chunk") return undefined
  if (!("content" in update) || !update.content || typeof update.content !== "object") return undefined
  return "text" in update.content ? String(update.content.text) : undefined
}

export function hasMessageGrowth(update: SessionNotification["update"]): boolean {
  const candidate = update as Record<string, unknown>
  for (const key of ["messages", "message", "messageCount", "messageDelta"]) {
    const value = candidate[key]
    if (Array.isArray(value) && value.length > 0) return true
    if (typeof value === "string" && value.trim().length > 0) return true
    if (typeof value === "number" && value > 0) return true
  }
  return false
}

export class ToolCallIdGenerator {
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

export function normalizeSessionUpdate(update: JsonObject, sessionId: string, ids: ToolCallIdGenerator): JsonObject {
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

export function genericSessionEventType(type: string, payload: JsonObject): string {
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

export function inferToolName(payload: unknown): string | undefined {
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

export function createObservabilityAwareEmitter(
  context: ActionContext,
  getRuntimeSessionId: () => string,
  toolIds: ToolCallIdGenerator,
): (type: string, update: SessionNotification["update"]) => Promise<void> {
  return async (type, update) => {
    const runtimeSessionId = getRuntimeSessionId()
    const normalized = normalizeSessionUpdate(update as unknown as JsonObject, runtimeSessionId, toolIds)
    await emitSessionEvent(context, genericSessionEventType(type, normalized), normalized, runtimeSessionId)

    if (type === "config_option_update") {
      const resolvedModel = extractResolvedModelFromConfigUpdateLocal(update as unknown)
      if (resolvedModel) {
        await emitSessionEvent(context, MODEL_RESOLVED_EVENT, buildResolvedModelEventPayload(context, runtimeSessionId, resolvedModel, "config_option_update"), runtimeSessionId)
      }
    }

    if (type === "usage_update") {
      const u = update as unknown as Record<string, unknown>
      const compaction = extractCompactionEventFromUpdateLocal(update)
      if (compaction && compaction.contextWindowSize === undefined && typeof u.size === "number") {
        compaction.contextWindowSize = u.size
      }
      const payload = buildUsageUpdatePayload(context, runtimeSessionId, compaction ? "compaction" : "usage_update", undefined, {
        cost: u.cost,
        size: u.size,
        used: u.used,
        compaction,
      })
      if (hasUsageUpdateContent(payload)) {
        await emitSessionEvent(context, USAGE_UPDATED_EVENT, payload, runtimeSessionId)
        if (compaction) {
          await emitSessionEvent(context, COMPACTION_EVENT, buildCompactionEventPayload(context, runtimeSessionId, compaction), runtimeSessionId)
        }
      }
    }
  }
}

function extractResolvedModelFromConfigUpdateLocal(value: unknown): string | undefined {
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

function extractCompactionEventFromUpdateLocal(update: unknown): CompactionEventPayload | undefined {
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

export function createAcpSessionUpdateHandler(options: {
  notifyData(activityType?: string): void
  recordActivity?(): void
  recordWorkActivity?(): void
  appendAssistantText(text: string): void
  emitUpdate(type: string, update: SessionNotification["update"]): Promise<void>
}) {
  return async (notification: SessionNotification) => {
    const update = notification.update
    const type = update.sessionUpdate
    const activity = classifyAcpLivenessActivity({ kind: "session_update", update })
    if (activity.isActivity) {
      options.recordActivity?.()
      if (isPromptWorkActivity(activity.activityType)) options.recordWorkActivity?.()
      options.notifyData(activity.activityType)
    }

    const chunkText = assistantMessageChunkText(update)
    if (chunkText !== undefined) options.appendAssistantText(chunkText)

    await options.emitUpdate(type, update)
  }
}

export async function attachSessionToServer(context: ActionContext, runtimeSessionId: string, processPid: number | null, agentConfig: { model?: string } | undefined, resolvedModel?: string) {
  const target = sessionTargetFromContext(context)
  if (!target || !context.serverConnection) return

  const body = {
    runtimeSessionId,
    workDir: context.workDir,
    processPid,
    model: agentConfig?.model ?? stringInput(context.with, "model"),
    workId: context.workId,
    agentJobId: context.ownerKind === "agent-job" ? context.agentJobId ?? null : null,
    ...(resolvedModel ? { resolvedModel } : {}),
  }
  if (target.kind === "workflow") {
    await context.serverConnection.attachWorkflowAgentSession(
      target.projectId,
      target.workflowRunId,
      target.sessionName,
      body,
      context.signal)
    return
  }
  await context.serverConnection.attachAgentSession(
    target.projectId,
    target.sessionId,
    body,
    context.signal)
}

export async function emitResolvedModelEvent(context: ActionContext, agentSessionId: string, resolvedModel: string | undefined, resolvedModelSource: "newSession" | "resumeSession" | "config_option_update") {
  const sessionName = sessionNameFromContext(context)
  if (resolvedModel && sessionName) {
    await emitSessionEvent(context, MODEL_RESOLVED_EVENT, buildResolvedModelEventPayload(context, agentSessionId, resolvedModel, resolvedModelSource))
  }
}

export async function emitSessionStarted(context: ActionContext, agentSessionId: string, processPid: number | null, agentConfig: { model?: string } | undefined, resolvedModel?: string, resolvedModelSource: "newSession" | "resumeSession" = "newSession") {
  await attachSessionToServer(context, agentSessionId, processPid, agentConfig, resolvedModel)
  await emitResolvedModelEvent(context, agentSessionId, resolvedModel, resolvedModelSource)
}
