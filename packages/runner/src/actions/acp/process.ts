import { spawn, type ChildProcess } from "node:child_process"
import { Readable, Writable } from "node:stream"
import { ndJsonStream } from "@agentclientprotocol/sdk"
import type { Stream } from "@agentclientprotocol/sdk"
import type { ActionContext } from "../../core/types.js"
import { killProcess, sanitizedEnvironment } from "../../system/process.js"
import { assertExternalProcessAllowed, registerExternalProcess } from "../../system/process-policy.js"
import { acpArgs, acpCommand } from "../../runtime/acp-command.js"

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

export function getAcpProcessFactory() {
  return acpProcessFactory
}

export function createSpawnedAcpProcess(context: ActionContext): AcpProcessHandle {
  const command = acpCommand()
  const args = acpArgs()
  assertExternalProcessAllowed("actions/acp/process.createSpawnedAcpProcess")
  const proc = spawn(command, args, {
    cwd: context.workDir,
    stdio: ["pipe", "pipe", "inherit"],
    env: sanitizedEnvironment(),
  })
  registerExternalProcess(proc)
  return new SpawnedAcpProcess(proc)
}

export class SpawnedAcpProcess implements AcpProcessHandle {
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
