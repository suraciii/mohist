import type { RunnerOptions } from "../core/types.js"
import { ServerConnection } from "../server/connection.js"
import { RunnerSignalRClient } from "../server/runner-signalr.js"
import { createDefaultRegistry } from "../actions/registry.js"
import { WorkspaceManager, defaultRunnerRoot } from "./workspace.js"
import { WorkExecutor } from "./executor.js"
import { discoverOpencodeModels } from "./opencode-models.js"
import { AcpSessionManager, createSharedAcpConnection, type SharedAcpConnection } from "./acp-connection.js"
import type { WorkItem } from "../core/types.js"

export interface ReportResult {
  workflowRunId?: string | null
  workflowStatus?: string | null
}

export class RunnerHost {
  private readonly connection: ServerConnection
  private readonly signalR: RunnerSignalRClient
  private readonly workspace: WorkspaceManager
  private readonly maxConcurrentWorkflows: number

  constructor(private readonly options: RunnerOptions) {
    this.maxConcurrentWorkflows = Math.max(1, Math.floor(options.maxConcurrentWorkflows))
    this.connection = new ServerConnection(options)
    this.workspace = new WorkspaceManager(options.runnerRoot)
    this.signalR = new RunnerSignalRClient(
      options.serverUrl,
      options.runnerId,
      (issueNumber) => this.workspace.getExistingWorkDir(issueNumber),
    )
  }

  async run(signal: AbortSignal) {
    while (!signal.aborted) {
      await this.connectRunner(signal)
      const heartbeat = setInterval(() => void this.connection.heartbeat(signal).catch((error) => console.error(error)), this.options.heartbeatIntervalMs)
      try {
        await this.runWorkerPool(signal)
      } catch (error) {
        if (signal.aborted) break
        console.error(`runner connection lost; reconnecting in ${this.options.pollIntervalMs}ms`, error)
        await delay(this.options.pollIntervalMs, signal)
      } finally {
        clearInterval(heartbeat)
        await this.shutdownConnection()
      }
    }
  }

  private async runWorkerPool(signal: AbortSignal) {
    const inFlight = new Map<string, Promise<void>>()

    while (!signal.aborted) {
      while (!signal.aborted && inFlight.size < this.maxConcurrentWorkflows) {
        const work = await this.connection.poll(signal)
        if (!work) break

        const key = `${work.workflowRunId}:${work.workId}`
        const run = this.executeAndReport(work, signal)
          .catch((error) => {
            console.error(`work ${work.workId} failed before report:`, error)
          })
          .finally(() => {
            inFlight.delete(key)
          })
        inFlight.set(key, run)
      }

      if (inFlight.size === 0) {
        await delay(this.options.pollIntervalMs, signal)
        continue
      }

      if (inFlight.size >= this.maxConcurrentWorkflows) {
        await Promise.race(inFlight.values())
      } else {
        await Promise.race([
          delay(this.options.pollIntervalMs, signal),
          ...inFlight.values(),
        ])
      }
    }

    await Promise.allSettled(inFlight.values())
  }

  private async shutdownConnection() {
    const cleanup = new AbortController()
    const timeout = setTimeout(() => cleanup.abort(), 5_000)
    timeout.unref?.()
    try {
      await Promise.allSettled([
        this.connection.disconnect(cleanup.signal),
        this.signalR.stop(),
      ])
    } finally {
      clearTimeout(timeout)
    }
  }

  private async executeAndReport(work: WorkItem, signal: AbortSignal) {
    const sessionManager = new AcpSessionManager()
    let acpConnection: SharedAcpConnection | null = null
    const executor = new WorkExecutor(createDefaultRegistry(), this.workspace, this.connection, sessionManager, null)
    try {
      const workspacePath = typeof work.variables?.workspace === "object" && work.variables.workspace !== null ? (work.variables.workspace as Record<string, unknown>).path : undefined
      acpConnection = await createSharedAcpConnection(typeof workspacePath === "string" ? workspacePath : process.cwd())
      executor.updateAcpConnection(acpConnection)
    } catch (error) {
      console.error("failed to start shared ACP connection:", error)
    }
    try {
      const result = await executor.execute(work, signal)
      await this.connection.report(work, result, signal)
    } finally {
      await acpConnection?.shutdown()
    }
  }

  private async connectRunner(signal: AbortSignal) {
    while (!signal.aborted) {
      try {
        const coderModels = await discoverOpencodeModels(signal)
        await this.connection.connect({
          capabilities: [],
          projectId: this.options.projectId,
          coderModels,
          maxWorkflowSlots: this.maxConcurrentWorkflows,
        }, signal)
        await this.signalR.start()
        return
      } catch (error) {
        console.error(`runner connection failed; retrying in ${this.options.pollIntervalMs}ms`, error)
        await this.shutdownConnection()
        await delay(this.options.pollIntervalMs, signal)
      }
    }
  }
}

async function delay(ms: number, signal: AbortSignal) {
  if (signal.aborted) throw signal.reason
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timer)
      reject(signal.reason)
    }
    signal.addEventListener("abort", onAbort, { once: true })
  })
}
