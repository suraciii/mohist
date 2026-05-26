import type { RunnerOptions } from "../core/types.js"
import { ServerConnection } from "../server/connection.js"
import { createDefaultRegistry } from "../actions/registry.js"
import { WorkspaceManager } from "./workspace.js"
import { WorkExecutor } from "./executor.js"

export class RunnerHost {
  private readonly connection: ServerConnection
  private readonly executor: WorkExecutor

  constructor(private readonly options: RunnerOptions) {
    this.connection = new ServerConnection(options)
    this.executor = new WorkExecutor(createDefaultRegistry(), new WorkspaceManager(options.runnerRoot), this.connection)
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
          await this.connection.report(work, await this.executor.execute(work, signal), signal)
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

  private async connectWhenServerIsReady(signal: AbortSignal) {
    while (!signal.aborted) {
      try {
        await this.connection.connect(signal)
        return
      } catch (error) {
        console.error(`runner registration failed; retrying in ${this.options.pollIntervalMs}ms`, error)
        await delay(this.options.pollIntervalMs, signal)
      }
    }
  }
}

async function delay(ms: number, signal: AbortSignal) {
  await new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(resolve, ms)
    signal.addEventListener("abort", () => {
      clearTimeout(timeout)
      reject(signal.reason)
    }, { once: true })
  })
}
