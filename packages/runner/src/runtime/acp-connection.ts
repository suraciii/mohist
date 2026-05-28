import { spawn, type ChildProcess } from "node:child_process"
import { Readable, Writable } from "node:stream"
import { ClientSideConnection, ndJsonStream, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { killProcess, sanitizedEnvironment } from "../system/process.js"
import { acpArgs, acpCommand } from "./acp-command.js"

export interface SharedAcpConnection {
  readonly connection: ClientSideConnection
  readonly processPid: number | null
  shutdown(): Promise<void>
}

export interface SessionEntry {
  sessionId: string
  workDir: string
  model?: string
}

export class AcpSessionManager {
  private sessions = new Map<string, SessionEntry>()

  key(workflowRunId: string, sessionName: string): string {
    return `${workflowRunId}:${sessionName}`
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

let activeSessionUpdateHandler: SessionUpdateHandler = async () => {}
let activePermissionHandler: PermissionHandler = async () => ({ outcome: { outcome: "cancelled" } })

export function setActiveHandlers(sessionUpdate: SessionUpdateHandler, permission: PermissionHandler) {
  activeSessionUpdateHandler = sessionUpdate
  activePermissionHandler = permission
}

export function clearActiveHandlers() {
  activeSessionUpdateHandler = async () => {}
  activePermissionHandler = async () => ({ outcome: { outcome: "cancelled" } })
}

export async function createSharedAcpConnection(workDir: string): Promise<SharedAcpConnection> {
  const command = acpCommand()
  const args = acpArgs()
  const proc = spawn(command, args, {
    cwd: workDir,
    stdio: ["pipe", "pipe", "inherit"],
    env: sanitizedEnvironment(),
  })

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
  proc.stdin?.on("error", () => {})
  proc.stdout?.on("error", () => {})

  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification: SessionNotification) => {
        await activeSessionUpdateHandler(notification)
      },
      requestPermission: async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        return await activePermissionHandler(params)
      },
    }),
    stream,
  )

  const initResult = await Promise.race([
    connection.initialize({ protocolVersion: PROTOCOL_VERSION, clientInfo: { name: "mohist-runner", version: "0.1.0" } }),
    spawnFailure,
  ])
  initialized = true

  if (!initResult) throw new Error("ACP initialize returned null")

  return {
    connection,
    processPid: proc.pid ?? null,
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
    },
  }
}
