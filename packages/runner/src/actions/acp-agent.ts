import { spawn, type ChildProcess } from "node:child_process"
import { Readable, Writable } from "node:stream"
import { ClientSideConnection, ndJsonStream, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput, objectInput, stringInput } from "../core/json.js"
import { killProcess, sanitizedEnvironment } from "../system/process.js"
import { verifyExpectations } from "./expectations.js"

const DEFAULT_TIMEOUT_MS = 30 * 60 * 1000
const DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000
const DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024
const PROBE_PROMPT = "If this session is still alive, briefly report the current step and continue from existing context. Do not restart completed work."

interface AgentConfig {
  model?: string
  timeoutMs?: number
  livenessQuietThresholdMs?: number
  probeTimeoutMs?: number
}

interface AcpSessionResult {
  text: string
  success: boolean
  error?: string
  acpSessionId?: string
  exitCode?: number | null
}

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

export async function acpAgentAction(context: ActionContext): Promise<ActionResult> {
  const prompt = stringInput(context.with, "prompt") ?? buildFallbackPrompt(context)
  if (!prompt?.trim()) return { status: "failure", message: "ACP agent requires 'prompt'" }

  const result = await runAcpSession(context, prompt)
  const verification = await verifyExpectations(context)
  const ok = result.success && verification.satisfied
  const agentConfig = resolveAgentConfig(context.with)
  if (context.session) await emitSessionCompleted(context, ok ? "completed" : "failed", ok ? "Agent completed" : result.error ?? verification.message, result.exitCode ?? (ok ? 0 : 1))
  return {
    status: ok ? "success" : "failure",
    message: ok ? "ACP agent task completed" : result.error ?? verification.message,
    output: JSON.stringify({ kind: "acp-agent", status: ok ? "success" : "failure", acpSessionId: result.acpSessionId, model: agentConfig?.model, text: result.text, error: result.error, expectation: verification }),
    exitCode: result.exitCode ?? (ok ? 0 : 1),
  }
}

function resolveAgentConfig(with_?: JsonObject | null): AgentConfig | undefined {
  if (!with_) return undefined
  const agent = objectInput(with_, "agent")
  if (agent && typeof agent === "object") {
    return {
      model: stringInput(agent as JsonObject, "model") ?? undefined,
      timeoutMs: numberInput(agent as JsonObject, "timeout") ?? undefined,
      livenessQuietThresholdMs: numberInput(agent as JsonObject, "livenessQuietThresholdMs") ?? undefined,
      probeTimeoutMs: numberInput(agent as JsonObject, "probeTimeoutMs") ?? undefined,
    }
  }
  // Fallback: read directly from with (legacy format)
  return {
    model: stringInput(with_, "model") ?? undefined,
    timeoutMs: numberInput(with_, "timeout") ?? undefined,
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

async function runAcpSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const acpProcess = acpProcessFactory(context)
  const agentConfig = resolveAgentConfig(context.with)
  let sessionId = ""
  let agentText = ""
  let agentTextTruncated = false
  let lastDataAt = Date.now()
  let dataVersion = 0
  const dataWaiters = new Set<() => void>()
  const toolIds = new ToolCallIdGenerator()
  const notifyData = () => {
    lastDataAt = Date.now()
    dataVersion += 1
    for (const waiter of dataWaiters) waiter()
    dataWaiters.clear()
  }

  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification: SessionNotification) => {
        notifyData()
        const update = notification.update
        const type = update.sessionUpdate
        if (type === "agent_message_chunk" && "content" in update && update.content && typeof update.content === "object" && "text" in update.content) {
          const text = String(update.content.text)
          if (!agentTextTruncated) {
            agentText += text
            if (agentText.length > MAX_AGENT_TEXT_LENGTH) {
              agentText = truncateAgentText(agentText)
              agentTextTruncated = true
            }
          }
        }
        if (context.session) await emitSessionEvent(context, type, normalizeSessionUpdate(update as unknown as JsonObject, sessionId, toolIds))
      },
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
    notifyData()

    const session = await Promise.race([
      connection.newSession({ cwd: context.workDir, mcpServers: [] }),
      timeout(timeoutMs),
      acpProcess.exitFailure,
    ])
    if (session === "timeout") throw new Error("Timed out during ACP newSession")
    sessionId = session.sessionId
    notifyData()

    const model = agentConfig?.model ?? stringInput(context.with, "model")
    if (model?.trim()) {
      try {
        await connection.setSessionConfigOption({ sessionId, configId: "model", value: model })
      } catch {
        try {
          await connection.unstable_setSessionModel({ sessionId, modelId: model })
        } catch {
          // Older ACP agents may not support model selection; the prompt can still run.
        }
      }
    }

    if (context.session) {
      await emitSessionStarted(context, sessionId, acpProcess.processPid, agentConfig)
      await emitSessionEvent(context, "mohist_prompt", buildPromptEvent(context, prompt, sessionId))
    }

    const promptResult = await monitorPrompt(context, connection, sessionId, prompt, {
      timeoutMs,
      livenessQuietThresholdMs: agentConfig?.livenessQuietThresholdMs ?? numberInput(context.with, "livenessQuietThresholdMs") ?? DEFAULT_LIVENESS_QUIET_THRESHOLD_MS,
      probeTimeoutMs: agentConfig?.probeTimeoutMs ?? numberInput(context.with, "probeTimeoutMs") ?? DEFAULT_PROBE_TIMEOUT_MS,
      lastDataAt: () => lastDataAt,
      dataVersion: () => dataVersion,
      waitForData: (version) => waitForData(dataWaiters, () => dataVersion !== version),
      exitFailure: acpProcess.exitFailure,
    })
    if (promptResult !== "completed") return { text: agentText, success: false, error: promptResult.error, acpSessionId: sessionId, exitCode: acpProcess.exitCode() }

    return { text: agentText, success: true, acpSessionId: sessionId, exitCode: acpProcess.exitCode() ?? 0 }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: sessionId || undefined, exitCode: acpProcess.exitCode() ?? 1 }
  } finally {
    await acpProcess.cleanup()
  }
}

function createSpawnedAcpProcess(context: ActionContext): AcpProcessHandle {
  const command = process.env.MOHIST_AGENT_COMMAND ?? "opencode"
  const args = process.env.MOHIST_AGENT_ARGS ? JSON.parse(process.env.MOHIST_AGENT_ARGS) as string[] : ["acp"]
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
    proc.on("exit", (code) => {
      this.exited = true
      this.code = code
      try { proc.stdin?.destroy() } catch {}
      try { proc.stdout?.destroy() } catch {}
      if (!this.initialized && code !== 0) this.rejectOnSpawn?.(new Error(`[SPAWN_FAILED] opencode acp exited before initialize (exit code: ${code ?? "signal"})`))
      if (this.initialized && code !== 0) this.rejectOnExit?.(new Error(`[PROCESS_EXIT] opencode acp exited unexpectedly (exit code: ${code ?? "signal"})`))
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

async function monitorPrompt(context: ActionContext, connection: ClientSideConnection, sessionId: string, prompt: string, options: { timeoutMs: number; livenessQuietThresholdMs: number; probeTimeoutMs: number; lastDataAt(): number; dataVersion(): number; waitForData(version: number): Promise<"data">; exitFailure: Promise<never> }): Promise<"completed" | { error: string }> {
  const startedAt = Date.now()
  const promptPromise = connection.prompt({ sessionId, prompt: [{ type: "text", text: prompt }] })

  while (true) {
    const timeoutRemaining = startedAt + options.timeoutMs - Date.now()
    if (timeoutRemaining <= 0) return await cancelAndReturn(connection, sessionId, `Timed out after ${options.timeoutMs / 1000}s`)
    const quietRemaining = Math.max(0, options.lastDataAt() + options.livenessQuietThresholdMs - Date.now())
    const result = await Promise.race([
      promptPromise.then(() => "completed" as const),
      timeout(Math.min(timeoutRemaining, Math.max(quietRemaining, 1))),
      aborted(context.signal),
      options.exitFailure,
    ])
    if (result === "completed") return "completed"
    if (result === "aborted") return await cancelAndReturn(connection, sessionId, "Agent stopped by user")
    if (Date.now() - options.lastDataAt() < options.livenessQuietThresholdMs) continue

    const probeSentAt = new Date()
    const probeDeadlineAt = new Date(probeSentAt.getTime() + options.probeTimeoutMs)
    await emitSessionStatus(context, "probing", { probeSentAt, probeDeadlineAt })
    const beforeProbeVersion = options.dataVersion()
    connection.prompt({ sessionId, prompt: [{ type: "text", text: PROBE_PROMPT }] }).catch(() => {})
    const probeResult = await Promise.race([
      promptPromise.then(() => "completed" as const),
      options.waitForData(beforeProbeVersion),
      timeout(options.probeTimeoutMs),
      aborted(context.signal),
      options.exitFailure,
    ])
    if (probeResult === "completed") return "completed"
    if (probeResult === "data") {
      await emitSessionStatus(context, "running", { lastDataAt: new Date(options.lastDataAt()) })
      continue
    }
    if (probeResult === "aborted") return await cancelAndReturn(connection, sessionId, "Agent stopped by user")
    return { error: "Session liveness probe timed out" }
  }
}

async function cancelAndReturn(connection: ClientSideConnection, sessionId: string, error: string) {
  try { await connection.cancel({ sessionId }) } catch {}
  return { error }
}

function waitForData(waiters: Set<() => void>, done: () => boolean): Promise<"data"> {
  if (done()) return Promise.resolve("data")
  return new Promise((resolve) => waiters.add(() => resolve("data")))
}

async function emitSessionStarted(context: ActionContext, externalSessionId: string, processPid: number | null, agentConfig: AgentConfig | undefined) {
  if (context.telemetry && context.session) await context.telemetry.started(context.session.id, { externalSessionId, workDir: context.workDir, changeDir: null, processPid, model: agentConfig?.model ?? stringInput(context.with, "model") }, context.signal)
}

async function emitSessionEvent(context: ActionContext, type: string, payload: JsonObject) {
  if (context.telemetry && context.session) await context.telemetry.events(context.session.id, [{ type, payload }], context.signal)
}

async function emitSessionCompleted(context: ActionContext, status: string, message: string, exitCode: number) {
  if (context.telemetry && context.session) await context.telemetry.completed(context.session.id, { status, failureReason: message, exitCode }, context.signal)
}

async function emitSessionStatus(context: ActionContext, status: string, input: { lastDataAt?: Date; probeSentAt?: Date; probeDeadlineAt?: Date; failureReason?: string }) {
  if (!context.telemetry?.status || !context.session) return
  await context.telemetry.status(context.session.id, { status, lastDataAt: input.lastDataAt?.toISOString(), probeSentAt: input.probeSentAt?.toISOString(), probeDeadlineAt: input.probeDeadlineAt?.toISOString(), failureReason: input.failureReason }, context.signal)
}

function buildPromptEvent(context: ActionContext, prompt: string, sessionId: string): JsonObject {
  return { role: "mohist", text: prompt, kind: "task", sentAt: new Date().toISOString(), executionId: context.workId, stage: context.stage ?? null, title: context.title ?? null, issueId: context.session ? String(context.session.issueNumber) : null, acpSessionId: sessionId, outputPath: extractOutputPath(prompt) ?? null, contextFiles: extractContextFiles(prompt) ?? null }
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
