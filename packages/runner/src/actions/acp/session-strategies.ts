import { ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse } from "@agentclientprotocol/sdk"
import type { ActionContext } from "../../core/types.js"
import { numberInput } from "../../core/json.js"
import type { OpencodeProviderErrorDiagnostic } from "../../runtime/opencode-log-diagnostics.js"
import { getAcpProcessFactory } from "./process.js"
import type { AcpProcessHandle } from "./process.js"
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
} from "./session-events.js"
import {
  buildSessionMeta,
  resolveCompactionConfig,
} from "./compaction.js"
import { resolveAgentConfig } from "./agent-config.js"
import {
  applyRequestedModel,
  extractResolvedModelId,
  resolveRequestedModel,
} from "./model-resolution.js"
import type { PromptFailureReason, SessionLivenessState } from "./liveness.js"
import {
  createSessionLivenessState,
  monitorPrompt,
  recordSessionLivenessActivity,
  timeout,
  waitForData,
} from "./liveness.js"

const DEFAULT_TIMEOUT_MS = 60 * 60 * 1000
const DEFAULT_SESSION_START_TIMEOUT_MS = 30 * 1000
const DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000
const DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024
const SUPPORTED_RUNTIME = "opencode"

export interface AcpSessionResult {
  text: string
  success: boolean
  error?: string
  acpSessionId?: string
  exitCode?: number | null
  activityCount?: number
  providerError?: OpencodeProviderErrorDiagnostic
  failureCategory?: PromptFailureReason
}

export interface AcpPromptRunResult {
  completed: boolean
  error?: string
  providerError?: OpencodeProviderErrorDiagnostic
  failureCategory?: PromptFailureReason
  activityCount: number
  workActivityCount: number
  usageText: string
}

export type AcpPromptRunner = (prompt: string) => Promise<AcpPromptRunResult>

export async function runAcpWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  if (context.ownerKind === "agent-job") {
    return context.agentSessionId
      ? runAcpGenericAgentSession(context, prompt)
      : runEphemeralWorkflowAgentSession(context, prompt)
  }

  const sessionName = sessionNameFromContext(context)
  const manager = context.acpSessionManager
  const projectId = context.projectId

  if (sessionName && manager && context.serverConnection && projectId) {
    const key = manager.workflowKey(context.workflowRunId, sessionName)
    const session = await context.serverConnection.openWorkflowAgentSession(projectId, context.workflowRunId, sessionName, {
      workId: context.workId,
      workType: context.workType,
      stage: context.stage,
      title: context.title,
      issueNumber: context.issueNumber,
      workDir: context.workDir,
      epicNumber: context.epicNumber,
    }, context.signal)

    if (session.runtimeSessionId) {
      if (!isSupportedRuntimeBinding(session)) return missingRuntimeBindingResult()
      const cached = manager.get(key)
      if (cached?.sessionId === session.runtimeSessionId) {
        return runPromptOnExistingWorkflowAgentSession(context, prompt, cached)
      }
      const result = await runResumedWorkflowAgentSession(context, prompt, session.runtimeSessionId, session.workDir ?? context.workDir)
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
      epicNumber: context.epicNumber,
    }, context.signal)
  }

  return runEphemeralWorkflowAgentSession(context, prompt)
}

export async function runAcpGenericAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const sessionId = context.agentSessionId
  const manager = context.acpSessionManager
  const projectId = context.projectId
  const serverConnection = context.serverConnection

  if (!sessionId || !manager || !serverConnection || !projectId) {
    return runEphemeralWorkflowAgentSession(context, prompt)
  }

  const key = manager.genericKey(sessionId)
  const existing = await serverConnection.getAgentSession(projectId, sessionId, context.signal)
  const openBody = {
    workId: context.workId,
    workType: context.workType,
    stage: context.stage,
    title: context.title,
    issueNumber: context.issueNumber,
    workDir: context.workDir,
  }
  const session = existing?.runtimeSessionId
    ? existing
    : await serverConnection.openAgentSession(projectId, sessionId, openBody, context.signal)

  if (session.runtimeSessionId) {
    if (!isSupportedRuntimeBinding(session)) return missingRuntimeBindingResult()
    const cached = manager.get(key)
    if (cached?.sessionId === session.runtimeSessionId) {
      return runPromptOnExistingWorkflowAgentSession(context, prompt, cached)
    }
    const result = await runResumedWorkflowAgentSession(context, prompt, session.runtimeSessionId, session.workDir ?? context.workDir)
    if (result.success && result.acpSessionId) manager.set(key, { sessionId: result.acpSessionId, workDir: session.workDir ?? context.workDir })
    return result
  }

  const result = await runNewWorkflowAgentSession(context, prompt)
  if (result.success && result.acpSessionId) manager.set(key, { sessionId: result.acpSessionId, workDir: context.workDir })
  return result
}

function isSupportedRuntimeBinding(session: { runtime?: string | null }): boolean {
  return session.runtime?.toLowerCase() === SUPPORTED_RUNTIME
}

function missingRuntimeBindingResult(): AcpSessionResult {
  return {
    text: "",
    success: false,
    error: "Runtime session is unavailable. Reset the AgentSession to establish a new binding.",
    exitCode: 1,
  }
}

export async function runPromptOnExistingWorkflowAgentSession(context: ActionContext, prompt: string, entry: { sessionId: string; workDir: string }): Promise<AcpSessionResult> {
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
    return result
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: entry.sessionId, exitCode: 1, activityCount }
  } finally {
    acp.clearSessionHandlers(entry.sessionId)
  }
}

export function createSharedPromptRunner(options: {
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
    await emitSessionEvent(options.context, "session.input", buildPromptEvent(options.context, prompt, options.sessionId), options.sessionId)
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
    const activityFailure = validatePromptActivity(workActivityCount)
    if (activityFailure) return { completed: false, error: activityFailure, activityCount, workActivityCount, usageText }
    return { completed: true, activityCount, workActivityCount, usageText }
  }
}

export async function runResumedWorkflowAgentSession(context: ActionContext, prompt: string, acpSessionId: string, workDir: string): Promise<AcpSessionResult> {
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
    return result
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId, exitCode: 1, activityCount }
  } finally {
    acp.clearSessionHandlers(acpSessionId)
  }
}

export async function runNewWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
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
    return result
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, exitCode: 1, activityCount }
  } finally {
    if (sessionId) acp.clearSessionHandlers(sessionId)
  }
}

export async function runEphemeralWorkflowAgentSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
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
    return result
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: sessionId || undefined, exitCode: acpProcess.exitCode() ?? 1, activityCount }
  } finally {
    await acpProcess.cleanup()
  }
}

export function validatePromptActivity(workActivityCount: number) {
  return workActivityCount > 0 ? undefined : "ACP agent prompt completed without any prompt work activity"
}

export function truncateAgentText(text: string) {
  if (text.length <= MAX_AGENT_TEXT_LENGTH) return text
  const keepLength = Math.floor(MAX_AGENT_TEXT_LENGTH / 2)
  const head = text.slice(0, keepLength)
  const tail = text.slice(-keepLength)
  return `${head}\n\n...[truncated ${text.length - MAX_AGENT_TEXT_LENGTH} characters]...\n\n${tail}`
}
