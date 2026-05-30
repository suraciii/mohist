import type { RunnerOptions, WorkItemResult } from "../core/types.js"
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
  private readonly executor: WorkExecutor
  private readonly sessionManager = new AcpSessionManager()
  private readonly signalR: RunnerSignalRClient
  private acpConnection: SharedAcpConnection | null = null

  constructor(private readonly options: RunnerOptions) {
    this.connection = new ServerConnection(options)
    const workspace = new WorkspaceManager(options.runnerRoot)
    this.executor = new WorkExecutor(createDefaultRegistry(), workspace, this.connection, this.sessionManager, null)
    this.signalR = new RunnerSignalRClient(
      options.serverUrl,
      options.runnerId,
      (issueNumber) => workspace.getExistingWorkDir(issueNumber),
    )
  }

  async run(signal: AbortSignal) {
    while (!signal.aborted) {
      await this.connectWhenServerIsReady(signal)
      try {
        await this.signalR.start()
      } catch (error) {
        console.error("signalr connection failed, will retry:", error)
      }
      const heartbeat = setInterval(() => void this.connection.heartbeat(signal).catch((error) => console.error(error)), this.options.heartbeatIntervalMs)
      try {
        while (!signal.aborted) {
          const work = await this.connection.poll(signal)
          if (!work) {
            await delay(this.options.pollIntervalMs, signal)
            continue
          }
          await this.ensureAcpConnection(work, signal)
          const report = await this.connection.report(work, await this.executor.execute(work, signal), signal)
        }
      } catch (error) {
        if (signal.aborted) break
        console.error(`runner connection lost; reconnecting in ${this.options.pollIntervalMs}ms`, error)
        await delay(this.options.pollIntervalMs, signal)
      } finally {
        clearInterval(heartbeat)
        if (!signal.aborted) {
          await this.connection.disconnect(signal).catch((error) => console.error(error))
        }
      }
    }
  }

  private async ensureAcpConnection(work: WorkItem, signal: AbortSignal) {
    if (this.acpConnection) return
    try {
      const workspacePath = typeof work.variables?.workspace === "object" && work.variables.workspace !== null ? (work.variables.workspace as Record<string, unknown>).path : undefined
      this.acpConnection = await createSharedAcpConnection(typeof workspacePath === "string" ? workspacePath : process.cwd())
      this.executor.updateAcpConnection(this.acpConnection)
    } catch (error) {
      console.error("failed to start shared ACP connection:", error)
    }
  }

  private async connectWhenServerIsReady(signal: AbortSignal) {
    while (!signal.aborted) {
      try {
        const coderModels = await discoverOpencodeModels(signal)
        await this.connection.connect({ capabilities: [], projectId: this.options.projectId, coderModels }, signal)
        return
      } catch (error) {
        console.error(`runner registration failed; retrying in ${this.options.pollIntervalMs}ms`, error)
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
