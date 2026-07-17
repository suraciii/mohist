import type { JsonObject, RunnerOptions, RunnerRegistration } from "../core/types.js"
import { ServerConnection } from "../server/connection.js"
import {
  RunnerSignalRClient,
  type SessionCommandRequest,
  type SessionCommandResult,
} from "../server/runner-signalr.js"
import { createDefaultRegistry } from "../actions/registry.js"
import "../core/prompt-registry.js"
import { WorkspaceManager } from "./workspace.js"
import { WorkspaceRegistry } from "./workspace-registry.js"
import { SessionCommandJournal } from "./session-command-journal.js"
import { FollowupFailureOutbox } from "../server/followup-failure-outbox.js"
import { ConvergenceBackstop, ServerConnectionConvergenceAdapter } from "./cleanup-convergence.js"
import { CleanupLoop, DefaultCleanupRunner } from "./cleanup-loop.js"
import { WorkExecutor } from "./executor.js"
import { TaskLogCollector } from "./task-log.js"
import { discoverOpencodeModels } from "./opencode-models.js"
import { AcpSessionManager, createSharedAcpConnection, type SessionTarget, type SharedAcpConnection } from "./acp-connection.js"
import { loadBuildInfo } from "./build-info.js"
import type { RenderedWorkItem } from "../core/types.js"
import type { WorkItemResult } from "../core/types.js"
import { ToolCallIdGenerator, genericSessionEventType, normalizeSessionUpdate } from "../actions/acp/session-events.js"
import {
  FOLLOWUP_TARGET_UNAVAILABLE,
  type FollowupTargetResolution,
} from "../server/session-target.js"
import type { RequestPermissionRequest, RequestPermissionResponse, SessionNotification } from "@agentclientprotocol/sdk"

export interface ReportResult {
  workflowRunId?: string | null
  workflowStatus?: string | null
}

/**
 * The runner-process reported set is PROCESS-LIFETIME state, not per-poll.
 * It tracks works the process is executing (`inFlight`) and works whose
 * result has not yet been acked (`awaitingAck`). Both survive poll
 * exceptions and connection resets: a poll that throws must not discard
 * works still executing or awaiting ack, or the next poll's report will
 * drop them and the server will re-dispatch — a rollback storm that
 * duplicates execution and eventually fails works as runner-lost.
 * (design/workflow/scheduling.md §Poll Reconciliation — Implementation
 * constraint.)
 */
interface InFlightEntry {
  /** The execution promise; resolves when the work settles (success or failure). */
  done: Promise<void>
}

interface AwaitingAckEntry {
  /** The result to (re-)report until the owner acks (Accepted or Stale). */
  result: WorkItemResult
  /** Monotonic attempt count for diagnostics. */
  attempts: number
  /** Earliest wall-clock time for the next bounded report attempt. */
  retryAt: number | null
}

type RestoredSessionTarget = { sessionId: string; workDir: string }
type RestoreResult = RestoredSessionTarget | null | "unavailable"

/**
 * Builds the work key used to dedupe in-flight / awaiting-ack tracking.
 * `ownerKind:ownerId:workId`. The ownerId is the agentJobId for agent-job
 * work, the workflowRunId for workflow work. Matches the server-side
 * `workKey` convention (design/workflow/scheduling.md §Interfaces).
 */
function workKey(work: RenderedWorkItem): string {
  const ownerKind = work.ownerKind === "agent-job" ? "agent-job" : "workflow"
  const ownerId = ownerKind === "agent-job" ? (work.agentJobId ?? "") : work.workflowRunId
  return `${ownerKind}:${ownerId}:${work.workId}`
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
  private readonly sessionCommandJournal: SessionCommandJournal
  private readonly followupFailureOutbox: FollowupFailureOutbox
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

  // Step 10 of design/eventbus.md: AcpSessionManager and
  // SharedAcpConnection are created once per host (not per work item).
  // The previous design recreated them for every executeAndReport call,
  // so AcpSessionManager's cross-task cache was always cold and
  // SharedAcpConnection's session-resume path was never reachable.
  private sessionManager: AcpSessionManager = new AcpSessionManager()
  private sharedAcpConnection: SharedAcpConnection | null = null
  private workExecutor: WorkExecutor | null = null
  private readonly sessionRestorations = new Map<string, Promise<RestoreResult>>()

  // Process-lifetime reported set (see workKey/InFlightEntry doc above).
  // These Maps outlive poll exceptions and reconnects: a work enters
  // inFlight on dispatch, moves to awaitingAck when its result is ready,
  // and leaves awaitingAck only when the owner acks (Accepted or Stale).
  // The keys of both Maps together form the process's full poll report.
  private readonly inFlight = new Map<string, InFlightEntry>()
  private readonly awaitingAck = new Map<string, { work: RenderedWorkItem; entry: AwaitingAckEntry }>()

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
    this.sessionCommandJournal = new SessionCommandJournal(options.runnerRoot)
    this.followupFailureOutbox = new FollowupFailureOutbox(options.runnerRoot)
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
        followupFailureOutbox: this.followupFailureOutbox,
        sessionCommandHandler: (request) => this.handleSessionCommand(request),
        sessionCommandJournal: this.sessionCommandJournal,
        reconcileStartedSessionCommand: (request) => this.reconcileStartedSessionCommand(request),
        registry: this.workspaceRegistry,
      },
    )
  }

  private handleSessionCommand(request: SessionCommandRequest): SessionCommandResult {
    if (request.runtime.toLowerCase() !== "opencode") return { ok: false, error: "missing" }
    if (request.command === "reset" && request.expectedRuntimeSessionId !== request.runtimeSessionId) {
      return { ok: false, error: "conflict" }
    }
    return { ok: false, error: "unavailable" }
  }

  private reconcileStartedSessionCommand(_request: SessionCommandRequest) {
    return { state: "indeterminate" } as const
  }

  // Issue-129 T-004: branches on `target.kind` so the same resolver
  // services both the issue-scoped followup route (workflow-shaped) and
  // the new generic AgentSession followup route. Workflow keys use the
  // `workflow:` prefix; generic keys use the `generic:` prefix (T-002).
  // On a cache miss, a current persisted binding lets the runner restore
  // the ACP session before delivering a followup or cancellation.
  private resolveFollowupTarget(target: SessionTarget): FollowupTargetResolution | Promise<FollowupTargetResolution> {
    const sharedConnection = this.sharedAcpConnection
    if (!sharedConnection) return FOLLOWUP_TARGET_UNAVAILABLE
    const key = target.kind === "workflow"
      ? this.sessionManager.workflowKey(target.workflowRunId, target.sessionName)
      : this.sessionManager.genericKey(target.sessionId)
    if (this.options.projectId && this.options.projectId !== target.projectId) return null
    if (target.binding?.runnerId !== undefined && target.binding.runnerId !== this.options.runnerId) return null

    const cached = this.sessionManager.get(key)
    if (cached && (!target.binding || cached.sessionId === target.binding.runtimeSessionId)) {
      return { connection: sharedConnection.connection, sessionId: cached.sessionId, projectId: target.projectId }
    }
    if (!target.binding) return null

    return this.restoreSessionTarget(key, target, target.binding, sharedConnection)
      .then((entry) => entry === "unavailable"
        ? FOLLOWUP_TARGET_UNAVAILABLE
        : entry
          ? { connection: sharedConnection.connection, sessionId: entry.sessionId, projectId: target.projectId }
          : null)
  }

  private async restoreSessionTarget(
    key: string,
    target: SessionTarget,
    binding: NonNullable<SessionTarget["binding"]>,
    sharedConnection: SharedAcpConnection,
  ): Promise<RestoreResult> {
    if (binding.runtime.toLowerCase() !== "opencode" || binding.runnerId !== this.options.runnerId) return null

    const existing = this.sessionRestorations.get(key)
    if (existing) {
      const entry = await existing
      return entry === "unavailable" || entry?.sessionId === binding.runtimeSessionId ? entry : null
    }

    const restoration = this.resumeSessionTarget(key, target, binding, sharedConnection)
    this.sessionRestorations.set(key, restoration)
    try {
      return await restoration
    } finally {
      this.sessionRestorations.delete(key)
    }
  }

  private async resumeSessionTarget(
    key: string,
    target: SessionTarget,
    binding: NonNullable<SessionTarget["binding"]>,
    sharedConnection: SharedAcpConnection,
  ): Promise<RestoreResult> {
    if (binding.workDir === null) return null
    const previous = this.sessionManager.get(key)
    if (previous && previous.sessionId !== binding.runtimeSessionId) {
      this.sessionManager.delete(key)
      sharedConnection.clearSessionHandlers(previous.sessionId)
    }

    try {
      const outcome = await resumeWithin(
        sharedConnection.connection.resumeSession({
        sessionId: binding.runtimeSessionId,
        cwd: binding.workDir,
        mcpServers: [],
        }),
        RESTORED_SESSION_RESUME_TIMEOUT_MS,
      )
      if (outcome === "timeout") return "unavailable"
      if (outcome === "failed") return null

      const entry = { sessionId: binding.runtimeSessionId, workDir: binding.workDir }
      this.installRestoredSessionHandlers(target, binding, sharedConnection)
      this.sessionManager.set(key, entry)
      return entry
    } catch {
      sharedConnection.clearSessionHandlers(binding.runtimeSessionId)
      this.sessionManager.delete(key)
      return null
    }
  }

  private installRestoredSessionHandlers(
    target: SessionTarget,
    binding: NonNullable<SessionTarget["binding"]>,
    sharedConnection: SharedAcpConnection,
  ): void {
    const toolIds = new ToolCallIdGenerator()
    sharedConnection.setSessionHandlers(
      binding.runtimeSessionId,
      async (notification: SessionNotification) => {
        const update = normalizeSessionUpdate(
          notification.update as unknown as JsonObject,
          binding.runtimeSessionId,
          toolIds,
        )
        const body = {
          workId: null,
          workType: null,
          stage: null,
          runtimeSessionId: binding.runtimeSessionId,
          runtimeEvents: [{ type: genericSessionEventType(notification.update.sessionUpdate, update), payload: update }],
        }
        const signal = new AbortController().signal
        if (target.kind === "workflow") {
          await this.connection.workflowAgentSessionRuntimeEvents(
            target.projectId,
            target.workflowRunId,
            target.sessionName,
            body,
            signal,
          )
          return
        }
        await this.connection.agentSessionRuntimeEvents(target.projectId, target.sessionId, body, signal)
      },
      async (params: RequestPermissionRequest): Promise<RequestPermissionResponse> => {
        const allow = params.options.find((option) => option.kind === "allow_once" || option.kind === "allow_always")
        return allow ? { outcome: { outcome: "selected", optionId: allow.optionId } } : { outcome: { outcome: "cancelled" } }
      },
    )
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
      await this.sessionCommandJournal.load()
      await this.connectRunner(signal)
      await this.initializeSharedConnection(signal)
      // Startup convergence: pick up any terminal events the runner
      // missed while it was offline (e.g. completed while the previous
      // process was down). Runs immediately after SignalR is up so the
      // push channel is available in parallel.
      await this.runConvergenceOnce(signal)
      const heartbeat = setInterval(() => void this.connection.heartbeat(this.registrationState(), signal).catch((error) => console.error(error)), this.options.heartbeatIntervalMs)
      const selfCheck = setInterval(() => void this.runSelfCheck(signal), this.options.dispatchLivenessProbeIntervalMs)
      const convergenceTimer = setInterval(() => void this.runConvergenceOnce(signal), this.cleanupConvergenceIntervalMs)
      const cleanupTimer = setInterval(() => void this.runCleanupOnce(signal), this.cleanupLoopIntervalMs)
      try {
        await this.runWorkerPool(signal)
      } finally {
        clearInterval(heartbeat)
        clearInterval(selfCheck)
        clearInterval(convergenceTimer)
        clearInterval(cleanupTimer)
        await this.shutdownSharedConnection()
        await this.shutdownConnection()
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
      const policy = await this.connection.fetchConfig(signal)
      const result = await this.cleanupLoop.runOnce(policy, signal)
      if (result.retentionRemoved > 0 || result.budgetRemoved > 0 || result.guardAborted > 0) {
        console.log(
          `workspace cleanup: retention=${result.retentionRemoved} budget=${result.budgetRemoved} guardAborted=${result.guardAborted} usage=${result.workspaceUsageBytes ?? "unknown"}`,
        )
      }
    } catch (error) {
      // Cleanup is best-effort; the next tick retries. fetchConfig failures
      // (network blip, server restart) flow through this same catch so the
      // loop stays resilient without a stale-policy fallback (issue-359 D4).
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
    // The reported set (inFlight ∪ awaitingAck) is process-lifetime state
    // declared on the host instance, so it survives poll exceptions. Polling
    // and report retries share this one process-critical reconciliation loop;
    // no sibling lifetime task can prevent a failed poll from being retried.
    while (!signal.aborted) {
      await this.retryDueReports()

      let works: RenderedWorkItem[]
      try {
        works = await this.pollOnce(signal)
      } catch (error) {
        if (signal.aborted) break
        console.warn(`runner poll failed; retrying in ${this.options.pollIntervalMs}ms`, error)
        await raceInterval(this.nextReconciliationInterval(), signal, [])
        continue
      }

      // A single poll may return multiple dispatches (repair + new claims).
      // Execute each concurrently, skipping re-deliveries the process
      // already holds.
      for (const work of works) {
        if (signal.aborted) break
        const key = workKey(work)
        // Re-delivery is the normal recovery path under at-least-once:
        // skip a work the process already holds (inFlight or awaitingAck)
        // rather than execute it twice. The server may re-dispatch a
        // Running work it thinks we lost; if we still have it, we know
        // better.
        if (this.inFlight.has(key) || this.awaitingAck.has(key)) continue

        const done = this.executeAndTransition(work, signal, key)
        this.inFlight.set(key, { done })
      }

      if (signal.aborted) break
      // Pace the next round. With nothing in flight, sleep one interval
      // before re-polling; with in-flight work, race the interval against
      // any work settling so a freed slot re-polls promptly. A failed report
      // also bounds the wait: report retries must not inherit a long poll
      // interval.
      await raceInterval(this.nextReconciliationInterval(), signal, [
        ...[...this.inFlight.values()].map((e) => e.done),
      ])
    }

    // Drain in-flight executions on abort so completed work can finish its
    // bounded first report attempt before process shutdown.
    await Promise.allSettled([...this.inFlight.values()].map((e) => e.done))
  }

  private async pollOnce(signal: AbortSignal): Promise<RenderedWorkItem[]> {
    const bounded = boundedSignal(signal, POLL_TIMEOUT_MS)
    try {
      return await this.connection.poll(bounded.signal, this.pollReport())
    } finally {
      bounded.dispose()
    }
  }

  /**
   * The process's full level state, sent in every poll body so the server
   * can reconcile (Batch 2). In Batch 1 the server ignores the body; the
   * value of sending it now is that the reported set is correct the moment
   * the server starts consuming it, with no second runner-side change.
   */
  private pollReport(): { inFlight: string[]; awaitingAck: string[] } {
    return {
      inFlight: [...this.inFlight.keys()],
      awaitingAck: [...this.awaitingAck.keys()],
    }
  }

  /**
   * Executes a work item to completion and transitions it through the
   * reported-set lifecycle: inFlight (executing) → awaitingAck (result
   * ready, not yet acked). The first report attempt is made here; a
   * transport failure leaves the entry in awaitingAck for the reconciliation
   * loop to retry.
   * `signal` is the run-lifetime signal; reporting uses a fresh signal so
   * a host teardown (SIGINT) still reaches the owner instead of aborting.
   */
  private async executeAndTransition(
    work: RenderedWorkItem,
    signal: AbortSignal,
    key: string,
  ): Promise<void> {
    let result: WorkItemResult
    try {
      result = await this.executeWork(work, signal)
    } catch (error) {
      if (signal.aborted) return
      console.error(`work ${work.workId} failed before report:`, error)
      result = { status: "failed", message: String(error) }
    }

    // Move to awaitingAck regardless of outcome. A transport failure on
    // the first attempt is retried by the reconciliation loop; the result is the
    // final verdict (success or the failure captured above).
    this.inFlight.delete(key)
    this.awaitingAck.set(key, { work, entry: { result, attempts: 0, retryAt: null } })

    try {
      await this.reportOnce(key)
    } catch (error) {
      this.scheduleReportRetry(key)
      console.warn(`first report for work ${work.workId} failed; will retry`, error)
    }
  }

  /**
   * Reports a single awaitingAck entry. On ack (any non-throwing response
   * from the owner — Accepted or Stale are both acks), removes the entry.
   * Throws on transport failure so the caller can schedule
   * the next attempt.
   */
  private async reportOnce(key: string): Promise<void> {
    const held = this.awaitingAck.get(key)
    if (!held) return
    const controller = new AbortController()
    const timeout = setTimeout(() => controller.abort(), REPORT_TIMEOUT_MS)
    timeout.unref?.()
    held.entry.attempts += 1
    try {
      await this.connection.report(held.work, held.entry.result, controller.signal)
    } finally {
      clearTimeout(timeout)
    }
    // Accepted or Stale both terminate the retry (the owner acked). Any
    // other response shape from the legacy compat endpoint is also
    // treated as an ack: the result is delivered, do not re-report.
    this.awaitingAck.delete(key)
  }

  private scheduleReportRetry(key: string): void {
    const held = this.awaitingAck.get(key)
    if (held) held.entry.retryAt = Date.now() + AWAITING_ACK_RETRY_INTERVAL_MS
  }

  private async retryDueReports(): Promise<void> {
    const now = Date.now()
    const due = [...this.awaitingAck.entries()]
      .filter(([, held]) => held.entry.retryAt !== null && held.entry.retryAt <= now)

    await Promise.all(due.map(async ([key, held]) => {
      held.entry.retryAt = null
      try {
        await this.reportOnce(key)
      } catch (error) {
        this.scheduleReportRetry(key)
        console.warn(`retry report for work ${held.work.workId} failed (attempt ${held.entry.attempts})`, error)
      }
    }))
  }

  private nextReconciliationInterval(): number {
    let earliestRetryAt: number | null = null
    for (const { entry } of this.awaitingAck.values()) {
      if (entry.retryAt !== null && (earliestRetryAt === null || entry.retryAt < earliestRetryAt)) {
        earliestRetryAt = entry.retryAt
      }
    }
    if (earliestRetryAt === null) return this.options.pollIntervalMs
    return Math.min(this.options.pollIntervalMs, Math.max(0, earliestRetryAt - Date.now()))
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

  /**
   * Executes a single work item to completion, flushing its task log, and
   * returns the resulting {@link WorkItemResult}. Does NOT report — the
   * caller ({@link executeAndTransition}) owns the report lifecycle and
   * the awaitingAck transition so a transport failure is retried rather
   * than lost. Throws on execution failure (including abort); the caller
   * synthesises a `{ status: "failed" }` result from the thrown error.
   *
   * `signal` is the run-lifetime signal; on abort the work is abandoned
   * (re-thrown) without a synthesized result — the caller checks
   * `signal.aborted` before recording a failure.
   */
  private async executeWork(work: RenderedWorkItem, signal: AbortSignal): Promise<WorkItemResult> {
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
            label === "terminal",
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

    const startIncrementalFlushForCollector = (collector: import("./task-log.js").TaskLogCollector) => {
      const flushTrigger = startTaskLogFlushTrigger(
        () => flushIncrementalTaskLog(collector),
        this.options.taskLogFlushIntervalMs ?? TASK_LOG_FLUSH_INTERVAL_MS,
        this.options.taskLogFlushLineThreshold ?? TASK_LOG_FLUSH_LINE_THRESHOLD,
      )
      collector.setAppendListener(() => flushTrigger.noteAppend())
      return flushTrigger
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
      const fallback = await createSharedAcpConnection(process.cwd())
      try {
        executor.updateAcpConnection(fallback)
        const collector = new TaskLogCollector()
        const flushTrigger = startIncrementalFlushForCollector(collector)
        try {
          const execution = await executor.executeWithLog(work, signal, collector)
          execution.collector.setAppendListener(null)
          await flushTrigger.stop()
          await flushTaskLog(execution.collector)
          return execution.result
        } finally {
          collector.setAppendListener(null)
          await flushTrigger.stop()
        }
      } finally {
        await fallback.shutdown()
      }
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
    // Pre-create the collector so the trigger can be wired into its
    // `appendListener` BEFORE the executor starts emitting appends.
    // Passing `null` to `executeWithLog` would let the executor mint a
    // new collector without our listener — defeats the eager line-count
    // firing and leaves the trigger with no append notifications.
    const collector = new TaskLogCollector()
    const flushTrigger = startIncrementalFlushForCollector(collector)
    try {
      const execution = await this.workExecutor.executeWithLog(work, signal, collector)
      // Detach the listener before stopping the timer so a stale
      // tick can never re-fire against a collector that the executor
      // has handed back to us for terminal flushing.
      execution.collector.setAppendListener(null)
      // Stop the trigger before the terminal flush and wait for any
      // in-flight incremental upload to settle so terminal
      // reconciliation cannot overlap it.
      await flushTrigger.stop()
      if (signal.aborted) return execution.result
      // Flush BEFORE the caller reports so the report carries the verdict
      // while the (best-effort) upload runs in parallel with the verdict
      // round-trip. Errors are logged and swallowed — they never block
      // or fail the result (design D6).
      await flushTaskLog(execution.collector)
      return execution.result
    } finally {
      collector.setAppendListener(null)
      await flushTrigger.stop()
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

/**
 * Timeout for a single report HTTP attempt. A report that does not
 * complete within this window is aborted and retried by the reconciliation
 * loop. Long enough to absorb a slow owner under load, short enough that
 * a wedged connection is retried rather than hung.
 */
const REPORT_TIMEOUT_MS = 10_000

const RESTORED_SESSION_RESUME_TIMEOUT_MS = 30_000

/** Maximum time a single poll request may wait before the loop retries. */
const POLL_TIMEOUT_MS = 10_000

/**
 * Minimum delay before the reconciliation loop re-attempts an awaitingAck
 * entry whose report transport previously failed.
 */
const AWAITING_ACK_RETRY_INTERVAL_MS = 5_000

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

function resumeWithin(promise: Promise<unknown>, timeoutMs: number): Promise<"resumed" | "failed" | "timeout"> {
  return new Promise((resolve) => {
    let settled = false
    const complete = (outcome: "resumed" | "failed" | "timeout") => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      resolve(outcome)
    }
    const timer = setTimeout(() => complete("timeout"), timeoutMs)
    timer.unref?.()
    promise.then(() => complete("resumed"), () => complete("failed"))
  })
}

/**
 * Create an incremental flush trigger. The returned handle exposes
 * `stop()` to clear the interval and wait for any in-flight flush,
 * plus a `noteAppend()` method to register a newly-captured line
 * against the line-count threshold. Callers MUST await `stop()` before
 * the terminal flush so a final drain/upload cannot race the terminal
 * snapshot.
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
 * itself always invokes `flush` on a fire. Flushes are serialized per
 * trigger: if a timer/threshold fire happens while an upload is still
 * in flight, one follow-up flush is queued and run after the current
 * one settles.
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
): { stop: () => Promise<void>; noteAppend: () => void } {
  // Defensive: a zero/negative interval would create a tight loop.
  // Clamp to a minimum positive value to keep the trigger harmless
  // under accidental misconfiguration. Tests that need finer control
  // can pass an explicit positive `intervalMs`.
  const safeInterval = Math.max(50, Math.floor(intervalMs))
  const safeThreshold = Math.max(1, Math.floor(lineThreshold))
  let pending = 0
  let inFlight: Promise<void> | null = null
  let rerunAfterInFlight = false

  const runFlush = (): Promise<void> => {
    if (inFlight) {
      rerunAfterInFlight = true
      return inFlight
    }

    try {
      const result = flush()
      inFlight = Promise.resolve(result)
        .catch((error) => {
          console.error("task-log incremental flush failed", error)
        })
        .finally(() => {
          inFlight = null
          if (rerunAfterInFlight) {
            rerunAfterInFlight = false
            void runFlush()
          }
        })
    } catch (error) {
      console.error("task-log incremental flush failed", error)
      inFlight = null
    }

    return inFlight ?? Promise.resolve()
  }

  const waitForIdle = async () => {
    while (inFlight) await inFlight
  }

  const tick = () => {
    pending = 0
    void runFlush()
  }
  const handle = setInterval(tick, safeInterval)
  handle.unref?.()
  return {
    stop: async () => {
      clearInterval(handle)
      await waitForIdle()
    },
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

/**
 * Race a poll-interval timer against in-flight work promises. Unlike
 * {@link delay} wrapped in `Promise.race`, the interval timer is owned here:
 * whichever racer settles first, the timer is cleared and its promise
 * resolved, so no pending promise lingers to reject on a later abort and
 * surface as an unhandled rejection. The `signal` aborts the wait promptly
 * (resolving, since every caller re-checks `signal.aborted` afterwards).
 */
function raceInterval(ms: number, signal: AbortSignal, racers: Promise<unknown>[]): Promise<void> {
  return new Promise((resolve) => {
    let timer: ReturnType<typeof setTimeout> | null = null
    let settled = false
    const done = () => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      signal.removeEventListener("abort", onAbort)
      resolve()
    }
    const onAbort = done
    if (signal.aborted) { done(); return }
    timer = setTimeout(done, ms)
    timer.unref?.()
    signal.addEventListener("abort", onAbort, { once: true })
    for (const r of racers) r.then(done, done)
  })
}

function boundedSignal(parent: AbortSignal, timeoutMs: number): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController()
  const abortFromParent = () => controller.abort(parent.reason)
  if (parent.aborted) abortFromParent()
  else parent.addEventListener("abort", abortFromParent, { once: true })

  const timeout = setTimeout(() => controller.abort(new Error(`request timed out after ${timeoutMs}ms`)), timeoutMs)
  timeout.unref?.()

  return {
    signal: controller.signal,
    dispose: () => {
      clearTimeout(timeout)
      parent.removeEventListener("abort", abortFromParent)
    },
  }
}
