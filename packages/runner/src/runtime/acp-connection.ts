import { spawn, type ChildProcess } from "node:child_process"
import { Readable, Writable } from "node:stream"
import { ClientSideConnection, ndJsonStream, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { killProcess, sanitizedEnvironment } from "../system/process.js"
import { assertExternalProcessAllowed, registerExternalProcess } from "../system/process-policy.js"
import { acpArgs, acpCommand } from "./acp-command.js"

export interface RuntimeSessionBinding {
  runtime: string
  runtimeSessionId: string
  runnerId: string
  workDir: string | null
}

export type SessionTarget =
  | { kind: "workflow"; projectId: string; workflowRunId: string; sessionName: string; binding?: RuntimeSessionBinding }
  | { kind: "generic"; projectId: string; sessionId: string; binding?: RuntimeSessionBinding }

export interface SharedAcpConnection {
  readonly connection: ClientSideConnection
  readonly processPid: number | null
  setSessionHandlers(sessionId: string, sessionUpdate: SessionUpdateHandler, permission: PermissionHandler): void
  clearSessionHandlers(sessionId: string): void
  shutdown(): Promise<void>
}

export interface SessionEntry {
  sessionId: string
  workDir: string
}

export class AcpSessionManager {
  private sessions = new Map<string, SessionEntry>()

  key(target: SessionTarget): string {
    if (target.kind === "workflow") {
      return `workflow:${target.workflowRunId}:${target.sessionName}`
    }
    return `generic:${target.sessionId}`
  }

  workflowKey(workflowRunId: string, sessionName: string): string {
    return `workflow:${workflowRunId}:${sessionName}`
  }

  genericKey(sessionId: string): string {
    return `generic:${sessionId}`
  }

  get(key: string): SessionEntry | undefined {
    return this.sessions.get(key)
  }

  set(key: string, entry: SessionEntry): void {
    this.sessions.set(key, entry)
  }

  has(key: string): boolean {
    return this.sessions.has(key)
  }

  delete(key: string): void {
    this.sessions.delete(key)
  }
}

type SessionUpdateHandler = (notification: SessionNotification) => Promise<void>
type PermissionHandler = (params: RequestPermissionRequest) => Promise<RequestPermissionResponse>

export async function createSharedAcpConnection(workDir: string): Promise<SharedAcpConnection> {
  const command = acpCommand()
  const args = acpArgs()
  assertExternalProcessAllowed("runtime/acp-connection.createSharedAcpConnection")
  const proc = spawn(command, args, {
    cwd: workDir,
    stdio: ["pipe", "pipe", "inherit"],
    env: sanitizedEnvironment(),
  })
  registerExternalProcess(proc)

  const stream = ndJsonStream(
    Writable.toWeb(proc.stdin!) as WritableStream<Uint8Array>,
    Readable.toWeb(proc.stdout!) as ReadableStream<Uint8Array>,
  )

  let initialized = false
  let exited = false
  const spawnFailure = new Promise<never>((_, reject) => {
    proc.on("error", (error) => {
      if (!initialized) reject(new Error(`[SPAWN_FAILED] ${error.message}`))
    })
  })
  const exitFailure = new Promise<never>((_, reject) => {
    proc.on("exit", (code) => {
      exited = true
      try { proc.stdin?.destroy() } catch {}
      try { proc.stdout?.destroy() } catch {}
      if (!initialized && code !== 0) reject(new Error(`[SPAWN_FAILED] opencode acp exited before initialize (exit code: ${code ?? "signal"})`))
      if (initialized && code !== 0) reject(new Error(`[PROCESS_EXIT] opencode acp exited unexpectedly (exit code: ${code ?? "signal"})`))
    })
  })
  exitFailure.catch(() => {})
  proc.stdin?.on("error", () => {})
  proc.stdout?.on("error", () => {})
  const sessionUpdateHandlers = new Map<string, SessionUpdateHandler>()
  const permissionHandlers = new Map<string, PermissionHandler>()

  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification: SessionNotification) => {
        await (sessionUpdateHandlers.get(notification.sessionId) ?? noopSessionUpdateHandler)(notification)
      },
      requestPermission: async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        return await (permissionHandlers.get(params.sessionId) ?? cancelPermissionHandler)(params)
      },
    }),
    stream,
  )

  const initResult = await Promise.race([
    connection.initialize({ protocolVersion: PROTOCOL_VERSION, clientInfo: { name: "mohist-runner", version: "0.1.0" } }),
    spawnFailure,
    timeout(30_000, "Timed out during shared ACP initialize"),
  ])
  initialized = true

  if (!initResult) throw new Error("ACP initialize returned null")

  return {
    connection,
    processPid: proc.pid ?? null,
    setSessionHandlers(sessionId: string, sessionUpdate: SessionUpdateHandler, permission: PermissionHandler) {
      sessionUpdateHandlers.set(sessionId, sessionUpdate)
      permissionHandlers.set(sessionId, permission)
    },
    clearSessionHandlers(sessionId: string) {
      sessionUpdateHandlers.delete(sessionId)
      permissionHandlers.delete(sessionId)
    },
    async shutdown() {
      await Promise.allSettled([
        stream.readable.cancel().catch(() => {}),
        stream.writable.abort().catch(() => {}),
      ])
      if (!exited) {
        killProcess(proc)
        setTimeout(() => {
          try { proc.kill("SIGKILL") } catch {}
        }, 5_000).unref?.()
      }
      await exitFailure.catch(() => {})
    },
  }
}

const noopSessionUpdateHandler: SessionUpdateHandler = async () => {}
const cancelPermissionHandler: PermissionHandler = async () => ({ outcome: { outcome: "cancelled" } })

function timeout(ms: number, message: string): Promise<never> {
  return new Promise((_, reject) => {
    const timer = setTimeout(() => reject(new Error(message)), ms)
    timer.unref?.()
  })
}
