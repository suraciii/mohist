import type { RunnerOptions, RunnerRegistration } from "../core/types.js"
import { ServerConnection } from "../server/connection.js"
import { RunnerSignalRClient } from "../server/runner-signalr.js"
import { createDefaultRegistry } from "../actions/registry.js"
import "../core/prompt-registry.js"
import { WorkspaceManager } from "./workspace.js"
import { WorkspaceRegistry } from "./workspace-registry.js"
import { ConvergenceBackstop, ServerConnectionConvergenceAdapter } from "./cleanup-convergence.js"
import { CleanupLoop, DefaultCleanupRunner } from "./cleanup-loop.js"
import { WorkExecutor } from "./executor.js"
import { TaskLogCollector } from "./task-log.js"
import { discoverOpencodeModels } from "./opencode-models.js"
import { AcpSessionManager, createSharedAcpConnection, type SessionTarget, type SharedAcpConnection } from "./acp-connection.js"
import { loadBuildInfo } from "./build-info.js"
import type { RenderedWorkItem } from "../core/types.js"
import type { WorkItemResult } from "../core/types.js"
import type { ClientSideConnection } from "@agentclientprotocol/sdk"

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
  private readonly workspaceRegistry: WorkspaceRegistry
  private readonly convergence: ConvergenceBackstop
  private readonly cleanupLoop: CleanupLoop
  private readonly cleanupConvergenceIntervalMs: number
  private readonly cleanupLoopIntervalMs: number
  private readonly buildGitHash: string | null
  private coderModels: string[] = []
  private coderModelVariants: Record<string, string[]> = {}

  // The active outer-run signal. The onReconnected callback fires from
  // outside the run loop, so we capture the signal here to bound the
  // immediate heartbeat it triggers.
  private activeSignal: AbortSignal | null = null

  /**
   * Per-work collector reference for the incremental flush trigger.
   * Set when `executeWithLog` returns the collector, cleared in the
   * `finally` so a subsequent work item cannot see a stale collector
   * after the trigger has been stopped (design D1). The field lives on
   * the host (not the closure) because the trigger's `setInterval`
   * callback runs after `executeAndReport` has yielded its synchronous
   * portion and the closure would otherwise be out of scope.
   */
  private collectorRef: import("./task-log.js").TaskLogCollector | null = null

  // Step 10 of design/eventbus.md: AcpSessionManager and
  // SharedAcpConnection are created once per host (not per work item).
  // The previous design recreated them for every executeAndReport call,
  // so AcpSessionManager's cross-task cache was always cold and
  // SharedAcpConnection's session-resume path was never reachable.
  private sessionManager: AcpSessionManager = new AcpSessionManager()
  private sharedAcpConnection: SharedAcpConnection | null = null
  private workExecutor: WorkExecutor | null = null

  constructor(private readonly options: RunnerOptions) {
    this.cleanupConvergenceIntervalMs = Math.max(1000, Math.floor(options.cleanupConvergenceIntervalMs ?? 5 * 60_000))
    this.cleanupLoopIntervalMs = Math.max(1000, Math.floor(options.cleanupLoopIntervalMs ?? 2 * 60_000))
    const build = loadBuildInfo()
    this.buildGitHash = build.gitHash
    this.connection = new ServerConnection(options, this.buildGitHash)
    // Runner-local registry of workspaces this host has materialized.
    // Loaded eagerly at startup so the in-memory cache is hot before the
    // first dispatch or SignalR RPC (per T-002 acceptance criteria:
    // "Registry is persisted and reloaded on runner restart; active
    // entries remain active until a terminal transition is observed").
    // The registry is shared with WorkspaceManager (for materialize /
    // verify registration hooks) and RunnerSignalRClient (for the
    // RemoveWorkspace entry-removal hook).
    this.workspaceRegistry = new WorkspaceRegistry(options.runnerRoot)
    this.convergence = new ConvergenceBackstop(
      this.workspaceRegistry,
      new ServerConnectionConvergenceAdapter(this.connection),
    )
    this.cleanupLoop = new CleanupLoop(
      this.workspaceRegistry,
      new DefaultCleanupRunner(),
      options.runnerRoot,
    )
    this.workspace = new WorkspaceManager(options.runnerRoot, this.workspaceRegistry)
    this.signalR = new RunnerSignalRClient(
      options.serverUrl,
      options.runnerId,
      options.runnerRoot,
      this.buildGitHash,
      {
        onReconnected: () => this.onDispatchReconnected(),
        serverConnection: this.connection,
        followupTargetResolver: (target) => this.resolveFollowupTarget(target),
        registry: this.workspaceRegistry,
      },
    )
  }

  // Issue-129 T-004: branches on `target.kind` so the same resolver
  // services both the issue-scoped followup route (workflow-shaped) and
  // the new generic AgentSession followup route. Workflow keys use the
  // `workflow:` prefix; generic keys use the `generic:` prefix (T-002).
  // Both branches silently return null when no matching ACP session
  // entry exists — the runner SignalR handler drops unknown-session
  // followups without throwing, matching the existing "runner offline
  // / unknown session" contract.
  private resolveFollowupTarget(target: SessionTarget): { connection: ClientSideConnection; sessionId: string; projectId: string } | null {
    if (!this.sharedAcpConnection) return null
    const key = target.kind === "workflow"
      ? this.sessionManager.workflowKey(target.workflowRunId, target.sessionName)
      : this.sessionManager.genericKey(target.sessionId)
    const entry = this.sessionManager.get(key)
    if (!entry) return null
    if (this.options.projectId && this.options.projectId !== target.projectId) return null
    return { connection: this.sharedAcpConnection.connection, sessionId: entry.sessionId, projectId: target.projectId }
  }

  async run(signal: AbortSignal) {
    this.activeSignal = signal
    try {
      // Load the runner-local workspace registry before any dispatch /
      // SignalR RPC can fire. A missing file is treated as an empty
      // registry; corrupt JSON is similarly tolerated (see
      // WorkspaceRegistry.loadFromDisk). The load is best-effort — a
      // failed read does not block startup.
      try {
        await this.workspaceRegistry.load()
      } catch (error) {
        console.error("failed to load workspace registry; starting empty:", error)
      }
      while (!signal.aborted) {
        await this.connectRunner(signal)
        await this.initializeSharedConnection(signal)
        // Startup convergence: pick up any terminal events the runner
        // missed while it was offline (e.g. completed while the previous
        // process was down). Runs once per connect cycle, immediately
        // after SignalR is up so the push channel is available in
        // parallel.
        await this.runConvergenceOnce(signal)
        const heartbeat = setInterval(() => void this.connection.heartbeat(this.registrationState(), signal).catch((error) => console.error(error)), this.options.heartbeatIntervalMs)
        const selfCheck = setInterval(() => void this.runSelfCheck(signal), this.options.dispatchLivenessProbeIntervalMs)
        const convergenceTimer = setInterval(() => void this.runConvergenceOnce(signal), this.cleanupConvergenceIntervalMs)
        const cleanupTimer = setInterval(() => void this.runCleanupOnce(signal), this.cleanupLoopIntervalMs)
        try {
          await this.runWorkerPool(signal)
        } catch (error) {
          if (signal.aborted) break
          console.error(`runner connection lost; reconnecting in ${this.options.pollIntervalMs}ms`, error)
          await delay(this.options.pollIntervalMs, signal)
        } finally {
          clearInterval(heartbeat)
          clearInterval(selfCheck)
          clearInterval(convergenceTimer)
          clearInterval(cleanupTimer)
          await this.shutdownSharedConnection()
          await this.shutdownConnection()
        }
      }
    } finally {
      this.activeSignal = null
    }
  }

  private async runConvergenceOnce(signal: AbortSignal): Promise<void> {
    try {
      await this.convergence.runOnce(signal)
    } catch (error) {
      // Convergence is best-effort; the next tick or reconnect retries.
      console.error("workspace cleanup convergence pass failed:", error)
    }
  }

  private async runCleanupOnce(signal: AbortSignal): Promise<void> {
    try {
      const policy = this.connection.getLastCleanupPolicy()
      const result = await this.cleanupLoop.runOnce(policy, signal)
      if (result.retentionRemoved > 0 || result.budgetRemoved > 0 || result.guardAborted > 0) {
        console.log(
          `workspace cleanup: retention=${result.retentionRemoved} budget=${result.budgetRemoved} guardAborted=${result.guardAborted} usage=${result.workspaceUsageBytes ?? "unknown"}`,
        )
      }
    } catch (error) {
      // Cleanup is best-effort; the next tick retries.
      console.error("workspace cleanup loop failed:", error)
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
    // Convergence on every reconnect: the SignalR transport just
    // recovered, which is the cheapest moment to ask the server for the
    // truth about every active registry entry. Push may also have queued
    // events during the disconnect window; this catch-all reconciles
    // whatever push did not cover (design D2 backstop).
    const signal = this.activeSignal
    if (signal) {
      void this.runConvergenceOnce(signal)
      void this.runCleanupOnce(signal)
    }
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
      while (!signal.aborted) {
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

      await Promise.race([
        delay(this.options.pollIntervalMs, signal),
        ...inFlight.values(),
      ])
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

  private async executeAndReport(work: RenderedWorkItem, signal: AbortSignal) {
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

    // Owner-id mirrors `artifact-side-effects.ts:107`: agent-job
    // dispatches upload under `work.agentJobId`, workflow dispatches
    // under `work.workflowRunId`. Routing through a single uploadTaskLog
    // call keeps the task-log channel symmetric with artifact uploads
    // (design D7).
    const ownerKind = work.ownerKind === "agent-job" ? "agent-job" : "workflow"
    const ownerId = ownerKind === "agent-job" ? (work.agentJobId ?? "") : work.workflowRunId

    /**
     * Upload a pre-built task-log batch via the independent task-log
     * channel. Best-effort: a failed upload is logged and swallowed; the
     * report (which carries the verdict) is NEVER blocked or failed by
     * a flush failure (design D6 / D1).
     *
     * The terminal batch always uploads; incremental batches skip the
     * network round-trip when there is nothing to send (drain returned
     * `null`).
     */
    const uploadTaskLogBatch = async (
      batch: import("./task-log.js").TaskLogBatch,
      timeoutMs: number,
      label: "terminal" | "incremental",
    ) => {
      const uploadController = new AbortController()
      let timeout: ReturnType<typeof setTimeout> | null = null
      try {
        await Promise.race([
          this.connection.uploadTaskLog(
            ownerId,
            work.workId,
            batch,
            uploadController.signal,
            ownerKind,
          ),
          new Promise<never>((_resolve, reject) => {
            timeout = setTimeout(() => {
              uploadController.abort()
              reject(new Error(`task-log ${label} upload timed out after ${timeoutMs}ms`))
            }, timeoutMs)
            timeout.unref?.()
          }),
        ])
      } catch (flushError) {
        console.error(`task-log ${label} upload failed for work`, work.workId, flushError)
      } finally {
        if (timeout) clearTimeout(timeout)
      }
    }

    /**
     * Terminal reconciliation batch. Phase 1 retained behaviour: the
     * full snapshot is uploaded via the terminal-timeout constant and
     * the server dedups by `seq` so a failed incremental upload is
     * recovered (design D1 / spec
     * `a-failed-incremental-upload-is-reconciled-by-the-terminal-batch`).
     * The collector may be `null` when work failed before any phase
     * ran; even an empty batch is allowed to flow through (T-001
     * spec: "A task with no captured lines returns { lines: [] } and
     * never an error").
     */
    const flushTaskLog = async (collector: import("./task-log.js").TaskLogCollector | null) => {
      if (!collector) return
      const batch = collector.flush()
      await uploadTaskLogBatch(batch, TASK_LOG_UPLOAD_TIMEOUT_MS, "terminal")
    }

    /**
     * Incremental batch primitive. Drains the collector (entries with
     * `seq > watermark`), and when there is something new, uploads it
     * under the larger incremental-timeout constant. An empty drain
     * short-circuits — no network round-trip is issued (design D1 /
     * spec `an-empty-increment-produces-no-upload`).
     */
    const flushIncrementalTaskLog = async (collector: import("./task-log.js").TaskLogCollector | null) => {
      if (!collector) return
      const batch = collector.drain()
      if (batch === null) return
      await uploadTaskLogBatch(
        batch,
        this.options.taskLogIncrementalUploadTimeoutMs ?? TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS,
        "incremental",
      )
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
          // Ephemeral executor is created after the work item has been
          // dispatched; there is no collector to flush incrementally,
          // so we go straight to the terminal reconciliation batch.
          const execution = await executor.executeWithLog(work, signal, null)
          lastResult = execution.result
          await flushTaskLog(execution.collector)
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

    // Start the incremental flush trigger alongside executeWithLog and
    // stop it BEFORE the terminal flush so a final drain cannot race
    // the terminal batch (design D1 / spec
    // `executeAndReport-starts-stops-the-trigger-around-the-work-lifecycle`).
    // The trigger fires on either an elapsed interval since the last
    // fire or a reached line-count threshold of NEW (un-drained) lines
    // — the latter is checked via `noteAppend`, which the collector
    // calls synchronously from inside `append`. `flushIncrementalTaskLog`
    // short-circuits an empty drain so no upload is issued when there
    // is nothing new.
    const flushTrigger = startTaskLogFlushTrigger(
      () => flushIncrementalTaskLog(this.collectorRef),
      this.options.taskLogFlushIntervalMs ?? TASK_LOG_FLUSH_INTERVAL_MS,
      this.options.taskLogFlushLineThreshold ?? TASK_LOG_FLUSH_LINE_THRESHOLD,
    )
    // Pre-create the collector so the trigger can be wired into its
    // `appendListener` BEFORE the executor starts emitting appends.
    // Passing `null` to `executeWithLog` would let the executor mint a
    // new collector without our listener — defeats the eager line-count
    // firing and leaves the trigger with no append notifications.
    const collector = new TaskLogCollector()
    collector.setAppendListener(() => flushTrigger.noteAppend())
    // Set `collectorRef` BEFORE calling `executeWithLog` so a trigger
    // tick that fires during execution has a live collector to drain.
    this.collectorRef = collector
    try {
      const execution = await this.workExecutor.executeWithLog(work, signal, collector)
      lastResult = execution.result
      // Detach the listener before stopping the timer so a stale
      // tick can never re-fire against a collector that the executor
      // has handed back to us for terminal flushing.
      execution.collector.setAppendListener(null)
      // Stop the trigger before the terminal flush so the trigger
      // cannot fire after flush() has already snapshotted the buffer.
      flushTrigger.stop()
      if (signal.aborted) return
      // Flush BEFORE the report so the report carries the verdict while
      // the (best-effort) upload runs in parallel with the verdict
      // round-trip. Errors are logged and swallowed — they never block
      // or fail the report (design D6).
      await flushTaskLog(execution.collector)
      await this.connection.report(work, lastResult, signal)
    } catch (error) {
      flushTrigger.stop()
      lastError = error
      if (signal.aborted) return
      console.error("executor failed for work", work.workId, error)
      await this.connection.report(work, { status: "failed", message: String(error) }, signal).catch(async () => {
        await reportDrain("failed", String(error))
      })
    } finally {
      // Always clear the ref so a subsequent work item does not see
      // a stale collector after the flush trigger has been stopped.
      this.collectorRef = null
    }
  }

  private registrationState(): RunnerRegistration {
    return {
      capabilities: [],
      projectId: this.options.projectId,
      coderModels: this.coderModels,
      coderModelVariants: this.coderModelVariants,
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

const TASK_LOG_UPLOAD_TIMEOUT_MS = 250

/**
 * Maximum time an incremental task-log upload is allowed to take.
 * Distinct from the terminal-batch timeout because incremental batches
 * are smaller but the rail tolerates more slack (design D1). Larger
 * than the terminal timeout because we accept second-level latency for
 * the live channel.
 */
const TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS = 5_000

/**
 * Wall-clock interval between incremental flush trigger fires. The
 * trigger fires regardless of whether new lines have arrived — an
 * empty drain then short-circuits without an upload (design D1).
 */
const TASK_LOG_FLUSH_INTERVAL_MS = 1_500

/**
 * Threshold on the count of new (un-drained) lines buffered past the
 * sent-seq watermark. Crossing this threshold on a write fires the
 * trigger eagerly, so a chatty command does not have to wait for the
 * interval to see its tail in the web view (design D1).
 */
const TASK_LOG_FLUSH_LINE_THRESHOLD = 200

/**
 * Create an incremental flush trigger. The returned handle exposes
 * `stop()` to clear the interval and a `noteAppend()` method to
 * register a newly-captured line against the line-count threshold.
 * Callers MUST invoke `stop()` before the terminal flush so a final
 * drain cannot race the terminal snapshot.
 *
 * `setInterval` is used (rather than a custom timer abstraction) so
 * the trigger is driven by the global JS timer clock and is therefore
 * deterministically controllable by `vi.useFakeTimers` (no real
 * wall-clock, per the project's testing convention).
 *
 * The trigger fires on EITHER:
 *   - an elapsed interval since the last fire (regardless of new
 *     lines — `flush` short-circuits an empty drain), OR
 *   - the line-count threshold being reached between two interval
 *     ticks. `noteAppend` is called once per captured line; when the
 *     running count since the last fire meets or exceeds the threshold,
 *     the trigger fires eagerly.
 *
 * `flush` is the single short-circuit point that skips the network
 * round-trip when the collector's `drain` is empty — the trigger
 * itself always invokes `flush` on a fire.
 *
 * Exported (not just module-private) so the test suite can drive the
 * exact same code path without reimplementing the `setInterval`
 * dance; the host keeps the trigger implementation here as the
 * single source of truth.
 */
export function startTaskLogFlushTrigger(
  flush: () => Promise<void> | void,
  intervalMs: number,
  lineThreshold: number,
): { stop: () => void; noteAppend: () => void } {
  // Defensive: a zero/negative interval would create a tight loop.
  // Clamp to a minimum positive value to keep the trigger harmless
  // under accidental misconfiguration. Tests that need finer control
  // can pass an explicit positive `intervalMs`.
  const safeInterval = Math.max(50, Math.floor(intervalMs))
  const safeThreshold = Math.max(1, Math.floor(lineThreshold))
  let pending = 0
  const tick = () => {
    pending = 0
    void flush()
  }
  const handle = setInterval(tick, safeInterval)
  handle.unref?.()
  return {
    stop: () => clearInterval(handle),
    noteAppend: () => {
      pending += 1
      if (pending >= safeThreshold) tick()
    },
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
