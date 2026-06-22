import type { RunnerOptions, RunnerRegistration } from "../core/types.js"
import { ServerConnection } from "../server/connection.js"
import { RunnerSignalRClient } from "../server/runner-signalr.js"
import { createDefaultRegistry } from "../actions/registry.js"
import "../core/prompt-registry.js"
import { WorkspaceManager, defaultRunnerRoot } from "./workspace.js"
import { WorkExecutor } from "./executor.js"
import { discoverOpencodeModels } from "./opencode-models.js"
import { AcpSessionManager, createSharedAcpConnection, type SharedAcpConnection } from "./acp-connection.js"
import { loadBuildInfo } from "./build-info.js"
import type { WorkItem } from "../core/types.js"
import type { WorkItemResult } from "../core/types.js"

export interface ReportResult {
  workflowRunId?: string | null
  workflowStatus?: string | null
}

/**
 * Resolves the runner's build git hash from the on-disk build manifest.
 * Returns `null` when the manifest is missing or unreadable (treated as
 * unknown-identity, non-fatal).
 */
export function getRunnerBuildGitHash(): string | null {
  return loadBuildInfo().gitHash
}

export class RunnerHost {
  private readonly connection: ServerConnection
  private readonly signalR: RunnerSignalRClient
  private readonly workspace: WorkspaceManager
  private readonly maxConcurrentWorkflows: number
  private readonly buildGitHash: string | null
  private coderModels: string[] = []
  private coderModelVariants: Record<string, string[]> = {}

  // The active outer-run signal. The onReconnected callback fires from
  // outside the run loop, so we capture the signal here to bound the
  // immediate heartbeat it triggers.
  private activeSignal: AbortSignal | null = null

  // Step 10 of design/eventbus.md: AcpSessionManager and
  // SharedAcpConnection are created once per host (not per work item).
  // The previous design recreated them for every executeAndReport call,
  // so AcpSessionManager's cross-task cache was always cold and
  // SharedAcpConnection's session-resume path was never reachable.
  private sessionManager: AcpSessionManager = new AcpSessionManager()
  private sharedAcpConnection: SharedAcpConnection | null = null
  private workExecutor: WorkExecutor | null = null

  constructor(private readonly options: RunnerOptions) {
    this.maxConcurrentWorkflows = Math.max(1, Math.floor(options.maxConcurrentWorkflows))
    const build = loadBuildInfo()
    this.buildGitHash = build.gitHash
    this.connection = new ServerConnection(options, this.buildGitHash)
    this.workspace = new WorkspaceManager(options.runnerRoot)
    this.signalR = new RunnerSignalRClient(
      options.serverUrl,
      options.runnerId,
      options.runnerRoot,
      this.buildGitHash,
      { onReconnected: () => this.onDispatchReconnected() },
    )
  }

  async run(signal: AbortSignal) {
    this.activeSignal = signal
    try {
      while (!signal.aborted) {
        await this.connectRunner(signal)
        await this.initializeSharedConnection(signal)
        const heartbeat = setInterval(() => void this.connection.heartbeat(this.registrationState(), signal).catch((error) => console.error(error)), this.options.heartbeatIntervalMs)
        const selfCheck = setInterval(() => void this.runSelfCheck(signal), this.options.dispatchLivenessProbeIntervalMs)
        try {
          await this.runWorkerPool(signal)
        } catch (error) {
          if (signal.aborted) break
          console.error(`runner connection lost; reconnecting in ${this.options.pollIntervalMs}ms`, error)
          await delay(this.options.pollIntervalMs, signal)
        } finally {
          clearInterval(heartbeat)
          clearInterval(selfCheck)
          await this.shutdownSharedConnection()
          await this.shutdownConnection()
        }
      }
    } finally {
      this.activeSignal = null
    }
  }

  private async runSelfCheck(signal: AbortSignal) {
    if (signal.aborted) return
    const alive = await this.signalR.probeLiveness(signal).catch(() => false)
    if (signal.aborted) return
    if (alive) return
    console.warn("dispatch liveness probe failed; forcing reconnect")
    try {
      await this.signalR.forceReconnect(signal)
    } catch (error) {
      console.error("forceReconnect failed:", error)
    }
  }

  private onDispatchReconnected() {
    void this.sendImmediateHeartbeat()
  }

  private async sendImmediateHeartbeat() {
    const signal = this.activeSignal
    if (!signal || signal.aborted) return
    try {
      await this.connection.heartbeat(this.registrationState(), signal)
    } catch (error) {
      console.error("immediate post-reconnect heartbeat failed:", error)
    }
  }

  private async initializeSharedConnection(signal: AbortSignal) {
    if (this.sharedAcpConnection !== null) return;
    try {
      this.sharedAcpConnection = await createSharedAcpConnection(process.cwd())
      this.workExecutor = new WorkExecutor(
        createDefaultRegistry(),
        this.workspace,
        this.connection,
        this.sessionManager,
        this.sharedAcpConnection,
      )
      console.log("runner ACP connection established (per-host, shared across work items)")
    } catch (error) {
      console.error("failed to start shared ACP connection:", error)
    }
  }

  private async shutdownSharedConnection() {
    if (this.sharedAcpConnection === null) return;
    try {
      await this.sharedAcpConnection.shutdown()
    } catch { /* best effort */ }
    this.sharedAcpConnection = null
    this.workExecutor = null
    // Reset the session manager so the next initialize starts clean.
    this.sessionManager = new AcpSessionManager()
  }

  private async runWorkerPool(signal: AbortSignal) {
    const inFlight = new Map<string, Promise<void>>()

    while (!signal.aborted) {
      while (!signal.aborted && inFlight.size < this.maxConcurrentWorkflows) {
        const work = await this.connection.poll(signal)
        if (!work) break

        // In-flight key uses ownerKind + owner identity + workId so that
        // an agent-job (workflowRunId = "") and a workflow can never
        // collide on the same key, even if their workIds happened to match.
        const ownerId = work.ownerKind === "agent-job"
          ? (work.agentJobId ?? "")
          : work.workflowRunId
        const key = `${work.ownerKind ?? "workflow"}:${ownerId}:${work.workId}`
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
    // Capture the last known result for a best-effort drain report when the
    // host is being torn down (SIGINT). Using a fresh AbortSignal (with a
    // short timeout) gives the workflow an explicit runner report instead of
    // leaving it waiting for a result which may never arrive.
    let lastResult: WorkItemResult | undefined
    let lastError: unknown
    const reportDrain = async (status: "failed", message: string) => {
      const drainController = new AbortController()
      const drainTimeout = setTimeout(() => drainController.abort(), 2000)
      try {
        await this.connection.report(
          work,
          { status, message },
          drainController.signal,
        )
      } catch (drainError) {
        console.error("drain report failed for work", work.workId, drainError)
      } finally {
        clearTimeout(drainTimeout)
      }
    }

    if (this.workExecutor === null) {
      // ACP connection failed to initialize at startup; fall back to the
      // per-work-item ephemeral path so the work still attempts to run.
      const sessionManager = new AcpSessionManager()
      const executor = new WorkExecutor(
        createDefaultRegistry(),
        this.workspace,
        this.connection,
        sessionManager,
        null,
      )
      try {
        const workspacePath = typeof work.variables?.workspace === "object" && work.variables.workspace !== null ? (work.variables.workspace as Record<string, unknown>).path : undefined
        const fallback = await createSharedAcpConnection(typeof workspacePath === "string" ? workspacePath : process.cwd())
        executor.updateAcpConnection(fallback)
        try {
          lastResult = await executor.execute(work, signal)
          await this.connection.report(work, lastResult, signal)
        } finally {
          await fallback.shutdown()
        }
      } catch (error) {
        lastError = error
        if (signal.aborted) return
        console.error("ephemeral ACP path failed for work", work.workId, error)
        await this.connection.report(work, { status: "failed", message: String(error) }, signal).catch(async () => {
          await reportDrain("failed", String(error))
        })
      }
      return
    }

    try {
      lastResult = await this.workExecutor.execute(work, signal)
      if (signal.aborted) return
      await this.connection.report(work, lastResult, signal)
    } catch (error) {
      lastError = error
      if (signal.aborted) return
      console.error("executor failed for work", work.workId, error)
      await this.connection.report(work, { status: "failed", message: String(error) }, signal).catch(async () => {
        await reportDrain("failed", String(error))
      })
    }
  }

  private registrationState(): RunnerRegistration {
    return {
      capabilities: [],
      projectId: this.options.projectId,
      coderModels: this.coderModels,
      coderModelVariants: this.coderModelVariants,
      maxWorkflowSlots: this.maxConcurrentWorkflows,
      connectionId: this.signalR.getConnectionId(),
    }
  }

  private async connectRunner(signal: AbortSignal) {
    while (!signal.aborted) {
      try {
        const discovered = await discoverOpencodeModels(signal)
        this.coderModels = discovered.models
        this.coderModelVariants = discovered.variants
        await this.connection.connect({
          ...this.registrationState(),
          buildGitHash: this.buildGitHash,
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
