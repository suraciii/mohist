import { ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse } from "@agentclientprotocol/sdk"
import type { ActionContext, ActionResult } from "../core/types.js"
import { numberInput } from "../core/json.js"
import { resolvePrompt } from "../core/prompt.js"
import { runCommand } from "../system/process.js"
import { verifyExpectations, type TaskArtifactExpectation } from "./expectations.js"
import type { OpencodeProviderErrorDiagnostic } from "../runtime/opencode-log-diagnostics.js"
import {
  AcpProcessHandle,
  getAcpProcessFactory,
  setAcpProcessFactoryForTest,
} from "./acp/process.js"
import type { AcpProcessFactory } from "./acp/process.js"
import {
  attachSessionToServer,
  buildPromptEvent,
  classifyAcpLivenessActivity,
  createAcpSessionUpdateHandler,
  createObservabilityAwareEmitter,
  emitResolvedModelEvent,
  emitSessionEvent,
  emitSessionStarted,
  recordLivenessActivity,
  sessionNameFromContext,
  ToolCallIdGenerator,
} from "./acp/session-events.js"
import type { CompactionConfig, CompactionStrategy } from "./acp/compaction.js"
import {
  buildSessionMeta,
  defaultCompactionConfig,
  resolveCompactionConfig,
  resolveCompactionConfigFromInput,
} from "./acp/compaction.js"
import type { AgentConfig } from "./acp/agent-config.js"
import { buildPromptLoaderContext, resolveAgentConfig } from "./acp/agent-config.js"
import type { RequestedModel } from "./acp/model-resolution.js"
import {
  applyRequestedModel,
  cachedModelAllowsReuse,
  extractResolvedModelId,
  modelDiagnosticContext,
  requestedModelMatchesSession,
  resolveRequestedModel,
} from "./acp/model-resolution.js"
import type { LivenessFailureReason, SessionLivenessState } from "./acp/liveness.js"
import {
  cancelAndReturn,
  createSessionLivenessState,
  hasPostProbeActivity,
  monitorPrompt,
  recordSessionLivenessActivity,
  timeout,
  waitForData,
} from "./acp/liveness.js"

export { AcpProcessHandle, setAcpProcessFactoryForTest }
export type { AcpProcessFactory } from "./acp/process.js"
export { resolveCompactionConfig, defaultCompactionConfig }
export type { CompactionConfig, CompactionStrategy } from "./acp/compaction.js"

const DEFAULT_TIMEOUT_MS = 30 * 60 * 1000
const DEFAULT_SESSION_START_TIMEOUT_MS = 30 * 1000
const DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000
const DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000
const DEFAULT_EXPECTATION_REPAIR_LIMIT = 1
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024

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

function truncateAgentText(text: string) {
  if (text.length <= MAX_AGENT_TEXT_LENGTH) return text
  const keepLength = Math.floor(MAX_AGENT_TEXT_LENGTH / 2)
  const head = text.slice(0, keepLength)
  const tail = text.slice(-keepLength)
  return `${head}\n\n...[truncated ${text.length - MAX_AGENT_TEXT_LENGTH} characters]...\n\n${tail}`
}
