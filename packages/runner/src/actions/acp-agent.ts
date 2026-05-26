import { spawn } from "node:child_process"
import { Readable, Writable } from "node:stream"
import { ClientSideConnection, ndJsonStream, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification } from "@agentclientprotocol/sdk"
import type { ActionContext, ActionResult, JsonObject } from "../core/types.js"
import { numberInput, stringInput } from "../core/json.js"
import { killProcess, sanitizedEnvironment } from "../system/process.js"
import { verifyExpectations } from "./expectations.js"

const DEFAULT_TIMEOUT_MS = 30 * 60 * 1000
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024

interface AcpSessionResult {
  text: string
  success: boolean
  error?: string
  acpSessionId?: string
  exitCode?: number | null
}

export async function acpAgentAction(context: ActionContext): Promise<ActionResult> {
  const prompt = stringInput(context.with, "prompt")
  if (!prompt?.trim()) return { status: "failure", message: "ACP agent requires 'prompt'" }

  const result = await runAcpSession(context, prompt)
  const verification = await verifyExpectations(context)
  const ok = result.success && verification.satisfied
  if (context.session) await emitSessionCompleted(context, ok ? "completed" : "failed", ok ? "Agent completed" : verification.message, result.exitCode ?? (ok ? 0 : 1))
  return {
    status: ok ? "success" : "failure",
    message: ok ? "ACP agent task completed" : result.error ?? verification.message,
    output: JSON.stringify({ kind: "acp-agent", status: ok ? "success" : "failure", acpSessionId: result.acpSessionId, model: stringInput(context.with, "model"), text: result.text, error: result.error, expectation: verification }),
    exitCode: result.exitCode ?? (ok ? 0 : 1),
  }
}

async function runAcpSession(context: ActionContext, prompt: string): Promise<AcpSessionResult> {
  const command = process.env.MOHIST_AGENT_COMMAND ?? "opencode"
  const args = ["acp"]
  const proc = spawn(command, args, {
    cwd: context.workDir,
    stdio: ["pipe", "pipe", "inherit"],
    env: sanitizedEnvironment(),
  })

  let initialized = false
  let exited = false
  let exitCode: number | null = null
  let sessionId = ""
  let agentText = ""
  let agentTextTruncated = false

  let rejectOnSpawn: ((error: Error) => void) | undefined
  let rejectOnExit: ((error: Error) => void) | undefined
  const spawnFailure = new Promise<never>((_, reject) => {
    rejectOnSpawn = reject
  })
  const exitFailure = new Promise<never>((_, reject) => {
    rejectOnExit = reject
  })

  proc.on("error", (error) => {
    if (!initialized) rejectOnSpawn?.(new Error(`[SPAWN_FAILED] ${error.message}`))
  })
  proc.on("exit", (code) => {
    exited = true
    exitCode = code
    try { proc.stdin.destroy() } catch {}
    try { proc.stdout.destroy() } catch {}
    if (!initialized && code !== 0) rejectOnSpawn?.(new Error(`[SPAWN_FAILED] opencode acp exited before initialize (exit code: ${code ?? "signal"})`))
    if (initialized && code !== 0) rejectOnExit?.(new Error(`[PROCESS_EXIT] opencode acp exited unexpectedly (exit code: ${code ?? "signal"})`))
  })
  proc.stdin.on("error", () => {})
  proc.stdout.on("error", () => {})

  const stream = ndJsonStream(
    Writable.toWeb(proc.stdin) as WritableStream<Uint8Array>,
    Readable.toWeb(proc.stdout) as ReadableStream<Uint8Array>,
  )

  const cleanup = async () => {
    await Promise.allSettled([
      stream.readable.cancel().catch(() => {}),
      stream.writable.abort().catch(() => {}),
    ])
    if (!exited) {
      killProcess(proc)
      setTimeout(() => {
        try { proc.kill("SIGKILL") } catch {}
      }, 5_000)
    }
  }

  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification: SessionNotification) => {
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
        if (context.session) await emitSessionEvent(context, type, update as unknown as JsonObject)
      },
      requestPermission: async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
        return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
      },
    }),
    stream,
  )

  try {
    const timeoutMs = numberInput(context.with, "timeout") ?? DEFAULT_TIMEOUT_MS
    const initialize = await Promise.race([
      connection.initialize({ protocolVersion: PROTOCOL_VERSION, clientInfo: { name: "mohist-runner", version: "0.1.0" } }),
      timeout(timeoutMs),
      spawnFailure,
    ])
    initialized = true
    rejectOnSpawn = undefined
    if (initialize === "timeout") throw new Error("Timed out during ACP initialize")

    const session = await Promise.race([
      connection.newSession({ cwd: context.workDir, mcpServers: [] }),
      timeout(timeoutMs),
    ])
    if (session === "timeout") throw new Error("Timed out during ACP newSession")
    sessionId = session.sessionId
    const model = stringInput(context.with, "model")
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
      await emitSessionStarted(context, sessionId, proc.pid ?? null)
      await emitSessionEvent(context, "mohist_prompt", { text: prompt, sentAt: new Date().toISOString(), kind: "task", issueId: String(context.session.issueNumber), acpSessionId: sessionId })
    }

    const promptResult = await Promise.race([
      connection.prompt({ sessionId, prompt: [{ type: "text", text: prompt }] }),
      timeout(timeoutMs),
      aborted(context.signal),
      exitFailure,
    ])
    if (promptResult === "aborted") {
      try { await connection.cancel({ sessionId }) } catch {}
      return { text: agentText, success: false, error: "Agent stopped by user", acpSessionId: sessionId, exitCode }
    }
    if (promptResult === "timeout") {
      try { await connection.cancel({ sessionId }) } catch {}
      return { text: agentText, success: false, error: `Timed out after ${timeoutMs / 1000}s`, acpSessionId: sessionId, exitCode }
    }

    return { text: agentText, success: true, acpSessionId: sessionId, exitCode: exitCode ?? 0 }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { text: agentText, success: false, error: message, acpSessionId: sessionId || undefined, exitCode: exitCode ?? 1 }
  } finally {
    await cleanup()
  }
}

async function emitSessionStarted(context: ActionContext, externalSessionId: string, processPid: number | null) {
  if (context.telemetry && context.session) await context.telemetry.started(context.session.id, { externalSessionId, workDir: context.workDir, changeDir: null, processPid, model: stringInput(context.with, "model") }, context.signal)
}

async function emitSessionEvent(context: ActionContext, type: string, payload: JsonObject) {
  if (context.telemetry && context.session) await context.telemetry.events(context.session.id, [{ type, payload }], context.signal)
}

async function emitSessionCompleted(context: ActionContext, status: string, message: string, exitCode: number) {
  if (context.telemetry && context.session) await context.telemetry.completed(context.session.id, { status, failureReason: message, exitCode }, context.signal)
}

function timeout(ms: number): Promise<"timeout"> {
  return new Promise((resolve) => setTimeout(() => resolve("timeout"), ms))
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
