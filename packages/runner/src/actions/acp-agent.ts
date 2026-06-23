import { ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse } from "@agentclientprotocol/sdk"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput, objectInput, stringInput } from "../core/json.js"
import { resolvePrompt, type PromptLoaderContext } from "../core/prompt.js"
import { runCommand } from "../system/process.js"
import { verifyExpectations, type TaskArtifactExpectation } from "./expectations.js"
import { appendOpencodeDiagnostic, findOpencodeProviderErrorDiagnostic, type OpencodeProviderErrorDiagnostic } from "../runtime/opencode-log-diagnostics.js"
import {
  AcpProcessHandle,
  getAcpProcessFactory,
  setAcpProcessFactoryForTest,
} from "./acp/process.js"
import {
  attachSessionToServer,
  buildPromptEvent,
  buildUsageUpdatePayload,
  classifyAcpLivenessActivity,
  CompactionStrategy,
  createAcpSessionUpdateHandler,
  createObservabilityAwareEmitter,
  emitLivenessStatusEvent,
  emitResolvedModelEvent,
  emitSessionEvent,
  emitSessionStarted,
  hasUsageUpdateContent,
  recordLivenessActivity,
  sessionNameFromContext,
  ToolCallIdGenerator,
} from "./acp/session-events.js"

export { AcpProcessHandle, setAcpProcessFactoryForTest }
export type { AcpProcessFactory } from "./acp/process.js"
export type { CompactionStrategy } from "./acp/session-events.js"

const DEFAULT_TIMEOUT_MS = 30 * 60 * 1000
const DEFAULT_SESSION_START_TIMEOUT_MS = 30 * 1000
const DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000
const DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000
const DEFAULT_EXPECTATION_REPAIR_LIMIT = 1
const CANCEL_TIMEOUT_MS = 5_000
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024
const PROBE_PROMPT = "If this session is still alive, briefly report the current step and continue from existing context. Do not restart completed work."

const DEFAULT_COMPACTION_THRESHOLD = 0.8
const DEFAULT_COMPACTION_STRATEGY = "summary"
const COMPACTION_META_KEY = "opencode.compaction"

export type CompactionConfig = {
  threshold: number
  strategy: CompactionStrategy
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

type LivenessFailureReason = "probe_timeout" | "probe_send_failed" | "protocol_disconnect" | "process_exit" | "prompt_timeout"

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

interface AcpSessionResult {
  text: string
  success: boolean
  error?: string
  acpSessionId?: string
  exitCode?: number | null
  activityCount?: number
  providerError?: OpencodeProviderErrorDiagnostic
  failureCategory?: LivenessFailureReason
  expectation?: TaskArtifactExpectation
}

interface AcpPromptRunResult {
  completed: boolean
  error?: string
  providerError?: OpencodeProviderErrorDiagnostic
  failureCategory?: LivenessFailureReason
  activityCount: number
  workActivityCount: number
  usageText: string
}

type AcpPromptRunner = (prompt: string) => Promise<AcpPromptRunResult>

export async function acpAgentAction(context: ActionContext): Promise<ActionResult> {
  let prompt: string | undefined
  try {
    prompt = await resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))
  } catch (error) {
    return { status: "failure", message: error instanceof Error ? error.message : String(error) }
  }
  if (!prompt?.trim()) return { status: "failure", message: "ACP agent requires 'prompt'" }

  const result = await runAcpWorkflowAgentSession(context, prompt)
  await restoreAgentToolNoise(context)
  const verification = result.expectation ?? await verifyExpectations(context)
  const ok = result.success && verification.satisfied
  const agentConfig = resolveAgentConfig(context.with)
  const failureCategory = ok ? null : result.failureCategory ?? null
  await emitSessionEvent(context, "session.closed", { status: ok ? "completed" : "failed", failureReason: ok ? null : result.error ?? verification.message, failureCategory, exitCode: result.exitCode ?? (ok ? 0 : 1) })
  return {
    status: ok ? "success" : "failure",
    message: ok ? "ACP agent task completed" : result.error ?? verification.message,
    output: JSON.stringify({ kind: "acp-agent", status: ok ? "success" : "failure", acpSessionId: result.acpSessionId, model: agentConfig?.model, text: result.text, error: result.error, providerError: result.providerError, expectation: verification }),
    exitCode: result.exitCode ?? (ok ? 0 : 1),
  }
}

async function satisfyExpectations(context: ActionContext, result: AcpSessionResult, runPrompt: AcpPromptRunner): Promise<TaskArtifactExpectation> {
  let verification = await verifyExpectations(context)
  if (verification.satisfied) return verification

  const repairLimit = expectationRepairLimit(context)
  for (let attempt = 1; attempt <= repairLimit && !verification.satisfied; attempt += 1) {
    const repair = await runPrompt(buildExpectationRepairPrompt(verification, attempt, repairLimit))
    result.activityCount = (result.activityCount ?? 0) + repair.activityCount
    if (repair.usageText) result.text = appendAgentText(result.text, repair.usageText)
    if (!repair.completed) {
      result.success = false
      result.error = repair.error
      result.providerError = repair.providerError
      result.failureCategory = repair.failureCategory
      result.exitCode = result.exitCode ?? 1
      return verification
    }
    if (repair.workActivityCount <= 0) {
      result.success = false
      result.error = "ACP agent prompt completed without any session activity"
      result.exitCode = result.exitCode ?? 1
      return verification
    }
    verification = await verifyExpectations(context)
  }

  return verification
}

function expectationRepairLimit(context: ActionContext): number {
  const configured = numberInput(context.with, "expectationRepairLimit")
  if (configured === undefined) return DEFAULT_EXPECTATION_REPAIR_LIMIT
  return Math.max(0, Math.floor(configured))
}

function buildExpectationRepairPrompt(expectation: TaskArtifactExpectation, attempt: number, limit: number): string {
  const missingFiles = expectation.missingFiles.map((file) => `- Missing file: ${file.path}`)
  const missingMarkers = expectation.missingArtifactMarkers.map((marker) => `- Missing marker in ${marker.path}: ${marker.contains}`)
  return [
    "Your previous response did not satisfy this task's completion requirements.",
    "",
    "Fix the missing required artifact output now. Do not redo unrelated work.",
    "",
    ...missingFiles,
    ...missingMarkers,
    "",
    `This is artifact repair attempt ${attempt} of ${limit}.`,
  ].join("\n")
}

function appendAgentText(existing: string, addition: string): string {
  if (!addition) return existing
  const combined = existing ? `${existing}\n${addition}` : addition
  return combined.length > MAX_AGENT_TEXT_LENGTH ? truncateAgentText(combined) : combined
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

function buildPromptLoaderContext(context: ActionContext): PromptLoaderContext {
  return {
    with: {},
    variables: context.variables ?? {},
    workDir: context.workDir,
    workId: context.workId,
    title: context.title ?? null,
    stage: context.stage ?? null,
  }
}

async function runAcpWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const sessionName = sessionNameFromContext(context)
  const manager = context.acpSessionManager
  const projectId = context.projectId

  if (sessionName && manager && context.serverConnection && projectId) {
    const key = manager.key(context.workflowRunId, sessionName)
    const agentConfig = resolveAgentConfig(context.with)
    const requestedModel = resolveRequestedModel(context, agentConfig).model
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
      const sessionModelMatches = requestedModelMatchesSession(requestedModel, session.model)
      if (cached?.sessionId === session.acpSessionId && cachedModelAllowsReuse(requestedModel, cached.model) && sessionModelMatches) {
        return runPromptOnExistingWorkflowAgentSession(context, prompt, cached)
      }
      const result = sessionModelMatches
        ? await runResumedWorkflowAgentSession(context, prompt, session.acpSessionId, session.workDir ?? context.workDir)
        : await runNewWorkflowAgentSession(context, prompt)
      if (result.success && result.acpSessionId) manager.set(key, { sessionId: result.acpSessionId, workDir: session.workDir ?? context.workDir, model: requestedModel })
      return result
    }

    const result = await runNewWorkflowAgentSession(context, prompt)
    if (result.success && result.acpSessionId) manager.set(key, { sessionId: result.acpSessionId, workDir: context.workDir, model: requestedModel })
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

function requestedModelMatchesSession(requestedModel: string | undefined, sessionModel: string | null | undefined) {
  const requested = requestedModel?.trim()
  if (!requested) return true
  return sessionModel?.trim() === requested
}

function cachedModelAllowsReuse(requestedModel: string | undefined, cachedModel: string | null | undefined) {
  const requested = requestedModel?.trim()
  if (!requested) return true
  const cached = cachedModel?.trim()
  if (!cached) return true
  return cached === requested
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
  let workActivityCount = 0
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
      recordWorkActivity: () => { workActivityCount += 1 },
      appendAssistantText,
      emitUpdate: createObservabilityAwareEmitter(context, () => entry.sessionId, toolIds),
    }),
    async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
      const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
      return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
    },
  )

  await emitSessionStarted(context, entry.sessionId, acp.processPid, agentConfig)
  await applyRequestedModel(connection, context, entry.sessionId, resolveRequestedModel(context, agentConfig), notifyData)

  try {
    const runPrompt = createSharedPromptRunner({
      context,
      connection,
      sessionId: entry.sessionId,
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      liveness,
      dataWaiters,
      getAgentText: () => agentText,
      getActivityCount: () => activityCount,
      getWorkActivityCount: () => workActivityCount,
    })
    const run = await runPrompt(prompt)
    if (!run.completed) {
      return { text: agentText, success: false, error: run.error, acpSessionId: entry.sessionId, exitCode: 1, activityCount, providerError: run.providerError, failureCategory: run.failureCategory }
    }
    const result: AcpSessionResult = { text: agentText, success: true, acpSessionId: entry.sessionId, exitCode: 0, activityCount }
    result.expectation = await satisfyExpectations(context, result, runPrompt)
    return result
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: entry.sessionId, exitCode: 1, activityCount }
  } finally {
    acp.clearSessionHandlers(entry.sessionId)
  }
}

function createSharedPromptRunner(options: {
  context: ActionContext
  connection: ClientSideConnection
  sessionId: string
  timeoutMs: number
  livenessQuietThresholdMs: number
  probeTimeoutMs: number
  liveness: SessionLivenessState
  dataWaiters: Set<() => void>
  getAgentText(): string
  getActivityCount(): number
  getWorkActivityCount(): number
  exitFailure?: Promise<never>
  acpProcess?: AcpProcessHandle
}): AcpPromptRunner {
  return async (prompt) => {
    const beforeText = options.getAgentText()
    const beforeActivity = options.getActivityCount()
    const beforeWorkActivity = options.getWorkActivityCount()
    await emitSessionEvent(options.context, "session.input", buildPromptEvent(options.context, prompt, options.sessionId))
    const promptResult = await monitorPrompt(options.context, options.connection, options.sessionId, prompt, {
      timeoutMs: options.timeoutMs,
      livenessQuietThresholdMs: options.livenessQuietThresholdMs,
      probeTimeoutMs: options.probeTimeoutMs,
      livenessState: options.liveness,
      waitForData: (version) => waitForData(options.dataWaiters, () => options.liveness.dataVersion !== version),
      exitFailure: options.exitFailure,
      acpProcess: options.acpProcess,
    })
    const activityCount = options.getActivityCount() - beforeActivity
    const workActivityCount = options.getWorkActivityCount() - beforeWorkActivity
    const usageText = options.getAgentText().slice(beforeText.length)
    if (promptResult !== "completed") {
      return { completed: false, error: promptResult.error, providerError: promptResult.providerError, failureCategory: promptResult.failureReason, activityCount, workActivityCount, usageText }
    }
    const activityFailure = validatePromptActivity(activityCount)
    if (activityFailure) return { completed: false, error: activityFailure, activityCount, workActivityCount, usageText }
    return { completed: true, activityCount, workActivityCount, usageText }
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
  let workActivityCount = 0
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
        recordWorkActivity: () => { workActivityCount += 1 },
        appendAssistantText,
        emitUpdate: createObservabilityAwareEmitter(context, () => acpSessionId, toolIds),
      }),
      async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
        return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
      },
    )

    await emitSessionStarted(context, acpSessionId, acp.processPid, agentConfig, resolvedModel, "resumeSession")

    const runPrompt = createSharedPromptRunner({
      context,
      connection,
      sessionId: acpSessionId,
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      liveness,
      dataWaiters,
      getAgentText: () => agentText,
      getActivityCount: () => activityCount,
      getWorkActivityCount: () => workActivityCount,
    })
    const run = await runPrompt(prompt)
    if (!run.completed) {
      return { text: agentText, success: false, error: run.error, acpSessionId, exitCode: 1, activityCount, providerError: run.providerError, failureCategory: run.failureCategory }
    }

    const result: AcpSessionResult = { text: agentText, success: true, acpSessionId, exitCode: 0, activityCount }
    result.expectation = await satisfyExpectations(context, result, runPrompt)
    return result
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
  let workActivityCount = 0
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
        recordWorkActivity: () => { workActivityCount += 1 },
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

    const runPrompt = createSharedPromptRunner({
      context,
      connection,
      sessionId,
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      liveness,
      dataWaiters,
      getAgentText: () => agentText,
      getActivityCount: () => activityCount,
      getWorkActivityCount: () => workActivityCount,
    })
    const run = await runPrompt(prompt)
    if (!run.completed) {
      try { await connection.closeSession?.({ sessionId }) } catch {}
      return { text: agentText, success: false, error: run.error, acpSessionId: sessionId, exitCode: 1, activityCount, providerError: run.providerError, failureCategory: run.failureCategory }
    }

    const result: AcpSessionResult = { text: agentText, success: true, acpSessionId: sessionId, exitCode: 0, activityCount }
    result.expectation = await satisfyExpectations(context, result, runPrompt)
    return result
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
  let workActivityCount = 0
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
        recordWorkActivity: () => { workActivityCount += 1 },
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

    const runPrompt = createSharedPromptRunner({
      context,
      connection,
      sessionId,
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      liveness,
      dataWaiters,
      getAgentText: () => agentText,
      getActivityCount: () => activityCount,
      getWorkActivityCount: () => workActivityCount,
      exitFailure: acpProcess.exitFailure,
      acpProcess,
    })
    const run = await runPrompt(prompt)
    if (!run.completed) {
      return { text: agentText, success: false, error: run.error, acpSessionId: sessionId, exitCode: acpProcess.exitCode(), activityCount, providerError: run.providerError, failureCategory: run.failureCategory }
    }

    const result: AcpSessionResult = { text: agentText, success: true, acpSessionId: sessionId, exitCode: acpProcess.exitCode() ?? 0, activityCount }
    result.expectation = await satisfyExpectations(context, result, runPrompt)
    return result
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

async function monitorPrompt(context: ActionContext, connection: ClientSideConnection, sessionId: string, prompt: string, options: { timeoutMs: number; livenessQuietThresholdMs: number; probeTimeoutMs: number; livenessState: SessionLivenessState; waitForData(version: number): Promise<"data">; exitFailure?: Promise<never>; acpProcess?: AcpProcessHandle }): Promise<"completed" | { error: string; providerError?: OpencodeProviderErrorDiagnostic; failureReason?: LivenessFailureReason }> {
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
    await emitSessionEvent(context, "usage.updated", payload)
  }

  while (true) {
    const now = Date.now()
    const timeoutRemaining = startedAt + options.timeoutMs - now
    if (timeoutRemaining <= 0) {
      const diagnostic = await findOpencodeProviderErrorDiagnostic(sessionId)
      await emitLivenessStatusEvent(context, options.livenessState, "failed", {
        acpSessionId: sessionId,
        failureReason: "prompt_timeout",
        providerError: diagnostic,
        postProbeActivity: hasPostProbeActivity(options.livenessState),
      })
      await cancelAndReturn(options.acpProcess, connection, sessionId, `Timed out after ${options.timeoutMs / 1000}s`)
      return {
        error: appendOpencodeDiagnostic(`Timed out after ${options.timeoutMs / 1000}s`, diagnostic),
        providerError: diagnostic,
        failureReason: "prompt_timeout",
      }
    }
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
    if (result === "aborted") return await cancelAndReturn(options.acpProcess, connection, sessionId, "Agent stopped by user")
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
      return { error: appendOpencodeDiagnostic(`Session liveness probe timed out ${JSON.stringify(probeState)}`, diagnostic), providerError: diagnostic, failureReason: "probe_timeout" }
    }
    if (probeResult === "data" && probeWasSatisfied(options.livenessState)) {
      await emitLivenessStatusEvent(context, options.livenessState, "running", { acpSessionId: sessionId, satisfiedProbeVersion: activeProbe.probeVersion })
      clearLivenessProbe(options.livenessState)
      continue
    }
    if (probeResult === "aborted") return await cancelAndReturn(options.acpProcess, connection, sessionId, "Agent stopped by user")
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
    return { error: appendOpencodeDiagnostic(`Session liveness probe timed out ${JSON.stringify(probeState)}`, diagnostic), providerError: diagnostic, failureReason: "probe_timeout" }
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

async function cancelAndReturn(acpProcess: AcpProcessHandle | undefined, connection: ClientSideConnection, sessionId: string, error: string) {
  let cancelled = false
  try {
    await Promise.race([
      connection.cancel({ sessionId }).then(() => { cancelled = true }),
      timeout(CANCEL_TIMEOUT_MS),
    ])
  } catch {}
  if (!cancelled && acpProcess) {
    await acpProcess.cleanup()
  }
  return { error }
}

function waitForData(waiters: Set<() => void>, done: () => boolean): Promise<"data"> {
  if (done()) return Promise.resolve("data")
  return new Promise((resolve) => waiters.add(() => resolve("data")))
}

function extractResolvedModelId(value: unknown): string | undefined {
  if (typeof value !== "object" || value === null) return undefined
  const models = (value as Record<string, unknown>).models
  if (typeof models !== "object" || models === null) return undefined
  const current = (models as Record<string, unknown>).currentModelId
  return typeof current === "string" && current.trim().length > 0 ? current : undefined
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
