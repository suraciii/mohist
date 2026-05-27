import type { RunnerOptions, WorkItemResult } from "../core/types.js"
import { ServerConnection } from "../server/connection.js"
import { createDefaultRegistry } from "../actions/registry.js"
import { WorkspaceManager } from "./workspace.js"
import { WorkExecutor } from "./executor.js"
import { discoverOpencodeModels } from "./opencode-models.js"
import { AcpSessionPool } from "./session-pool.js"
import type { WorkItem } from "../core/types.js"

export interface ReportResult {
  workflowRunId?: string | null
  workflowStatus?: string | null
}

export class RunnerHost {
  private readonly connection: ServerConnection
  private readonly executor: WorkExecutor
  private readonly pool = new AcpSessionPool()

  constructor(private readonly options: RunnerOptions) {
    this.connection = new ServerConnection(options)
    this.executor = new WorkExecutor(createDefaultRegistry(), new WorkspaceManager(options.runnerRoot), this.connection, this.pool)
  }

  async run(signal: AbortSignal) {
    while (!signal.aborted) {
      await this.connectWhenServerIsReady(signal)
      const heartbeat = setInterval(() => void this.connection.heartbeat(signal).catch((error) => console.error(error)), this.options.heartbeatIntervalMs)
      try {
        while (!signal.aborted) {
          const work = await this.connection.poll(signal)
          if (!work) {
            await delay(this.options.pollIntervalMs, signal)
            continue
          }
          const report = await this.connection.report(work, await this.executor.execute(work, signal), signal)
          this.handleReport(work, report)
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

  private handleReport(_work: WorkItem, _report: ReportResult) {}

  private async connectWhenServerIsReady(signal: AbortSignal) {
    while (!signal.aborted) {
      try {
        const coderModels = await discoverOpencodeModels(signal)
        await this.connection.connect({ capabilities: [], coderModels }, signal)
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
    const timeout = setTimeout(() => {
      signal.removeEventListener("abort", onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timeout)
      reject(signal.reason)
    }
    signal.addEventListener("abort", onAbort, { once: true })
  })
}
