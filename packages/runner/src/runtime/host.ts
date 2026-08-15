import type { RunnerOptions, RunnerRegistration, RuntimeReadinessWitness } from '../core/types.js'
import { ServerConnection } from '../server/connection.js'
import { RunnerSignalRClient } from '../server/runner-signalr.js'
import { reportAndRequireDurableAck } from './work-report.js'
import { buildRegistrationState } from './registration-state.js'
import { ActionRegistry, createDefaultRegistry } from '../actions/registry.js'
import '../core/prompt-registry.js'
import { WorkspaceManager } from './workspace.js'
import { WorkspaceRegistry, NamedWorkspaceRegistry } from './workspace-registry.js'
import { NamedWorkspaceManager } from './workspace-entity.js'
import { createNamedWorkspaceCleanupLoop, NamedWorkspaceReclaimProbe } from './named-workspace-cleanup.js'
import {
  createAgentSessionRuntimeEventOutbox,
  RUNTIME_EVENT_OUTBOX_FILE,
  type AgentSessionRuntimeEventOutbox,
} from '../server/runtime-event-outbox.js'
import { createServerRuntimeEventDelivery } from '../server/runtime-event-delivery.js'
import { ConvergenceBackstop, ServerConnectionConvergenceAdapter } from './cleanup-convergence.js'
import { BindingConvergence } from './binding-convergence.js'
import {
  BindingRecoveryCoordinator,
  probeRuntimeBinding,
  reattachRuntimeTurn,
  resolvePersistedWorkBinding,
} from './binding-recovery.js'
import { CleanupLoop, DefaultCleanupRunner } from './cleanup-loop.js'
import { WorkExecutor } from './executor.js'
import { AgentJobExecutor } from './agent-job-executor.js'
import { TaskLogCollector } from './task-log.js'
import { createHostCleanup } from './host-cleanup.js'
import { executeWork, retryPendingTerminalTaskLogs, type HostTaskLogDeps } from './host-task-log.js'
import { AWAITING_ACK_RETRY_INTERVAL_MS, boundedSignal, delay, POLL_TIMEOUT_MS, raceInterval } from './host-timing.js'
import { TerminalTaskLogDeliveryStoreImpl, type TerminalTaskLogDeliveryStore } from './terminal-task-log-delivery.js'
import { getOpenCodeRuntimeFactory, type OpenCodeRuntime } from './opencode/index.js'
import { getPiRuntimeFactory, parseProviderErrorPolicy, type PiRuntime } from './pi/index.js'
import { SessionCommandJournal } from './session-command-journal.js'
import { FollowupOperationJournal } from './followup-operation-journal.js'
import { CancelOperationJournal } from './cancel-operation-journal.js'
import { WorkResultJournal, workKey as journalWorkKey } from './work-result-journal.js'
import { RecoveredStartedWork } from './recovered-started-work.js'
import { runnerRestartedResult } from './work-report.js'
import { loadBuildInfo } from './build-info.js'
import type { DispatchWorkItem } from '../core/types.js'
import type { WorkItemResult } from '../core/types.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { WorkflowSessionTurnCoordinator } from './workflow-session-turn-coordinator.js'
import { SkillResolver } from './skill-resolver.js'
import { runnerLogger } from '../system/logger.js'
import { probePrlimit } from '../system/process.js'
import { normalizeWorkResourceLimits, type ResolvedWorkResourceLimits } from './resource-containment.js'
import { type FollowupTarget, type FollowupTargetResolution, type SessionTarget } from '../server/session-target.js'
import {
  boundedSignal,
  delay,
  projectReattachedRuntimeResult,
  raceInterval,
  runtimeForKind,
  runtimeKindForWork,
  type RuntimeKind,
  usesOpenCode,
} from './host-helpers.js'

export { startTaskLogFlushTrigger } from './host-task-log.js'

const log = runnerLogger.child('host')

export interface ReportResult {
  workflowRunId?: string | null
  workflowStatus?: string | null
}

export interface RunnerHostDependencies {
  terminalTaskLogDelivery?: TerminalTaskLogDeliveryStore
  waitForConnectionRetry?: (delayMs: number, signal: AbortSignal) => Promise<void>
}

/**
 * The runner-process reported set is PROCESS-LIFETIME state, not per-poll.
 * It tracks works the process is executing (`inFlight`) and works whose
 * result has not yet been acked (`awaitingAck`). Both survive poll
 * exceptions and connection resets: a poll that throws must not discard
 * works still executing or awaiting ack, or the next poll's report will
 * drop them and the server will re-dispatch — a rollback storm that
 * duplicates execution and eventually fails works as runner-lost.
 */
interface InFlightEntry {
  /** The execution promise; resolves when the work settles (success or failure). */
  done: Promise<void>
  readonly work: DispatchWorkItem
  /** A settled result held only in memory must not turn the loop into a busy poll. */
  awaitingResultPersistence: boolean
}

interface AwaitingAckEntry {
  /** The result to (re-)report until the owner acks (Accepted or Stale). */
  result: WorkItemResult
  /** Monotonic attempt count for diagnostics. */
  attempts: number
  /** Earliest wall-clock time for the next bounded report attempt. */
  retryAt: number | null
}

/**
 * Builds the work key used to dedupe in-flight / awaiting-ack tracking.
 * `ownerKind:ownerId:workId`. The ownerId is the agentJobId for agent-job
 * work, the workflowRunId for workflow work. Matches the server-side
 * `workKey` convention.
 */
const workKey = journalWorkKey

/**
 * True when the dispatch is an unresolved-agent recovery probe: the
 * server recorded a runtime binding and re-delivered the work to the
 * runner that owns it. Such a dispatch is reconciled against the
 * recorded binding, never executed as a fresh prompt.
 */
function isAgentRecoveryDispatch(work: DispatchWorkItem): boolean {
  const recovery = work.agentRecovery
  return Boolean(recovery && recovery.runtime.trim() && recovery.runtimeSessionId.trim())
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
  private readonly namedWorkspaceRegistry: NamedWorkspaceRegistry
  private readonly namedWorkspaceManager: NamedWorkspaceManager
  private readonly namedWorkspaceReclaimProbe: NamedWorkspaceReclaimProbe
  private readonly agentSessionRuntimeEventOutbox: AgentSessionRuntimeEventOutbox
  private readonly bindingConvergence: BindingConvergence
  private readonly bindingRecoveryCoordinator = new BindingRecoveryCoordinator()
  private readonly convergence: ConvergenceBackstop
  private readonly cleanupLoop: CleanupLoop
  private readonly namedCleanupLoop: ReturnType<typeof createNamedWorkspaceCleanupLoop>
  private readonly cleanup: ReturnType<typeof createHostCleanup>
  private readonly cleanupConvergenceIntervalMs: number
  private readonly cleanupLoopIntervalMs: number
  private readonly workflowSessionTurnCoordinator = new WorkflowSessionTurnCoordinator()
  private readonly buildGitHash: string | null
  private readonly buildInfo: ReturnType<typeof loadBuildInfo>

  /**
   * Shared OpenCode runtime handle. Constructed in
   * {@link initializeSharedConnection} via the factory seam; rebuilt in
   * the background after a server exit. `pollOnce` gates work claiming
   * on `ready()` — when the runtime is not ready, claiming is paused
   * and the runner emits the runtime's actionable diagnostic.
   */
  private openCodeRuntime: OpenCodeRuntime | null = null
  private piRuntime: PiRuntime | null = null
  private piRuntimeGeneration = 0
  private providerPolicyDiagnostic: string | null = null
  private lastProviderPolicyDiagnosticLogged: string | null = null
  /**
   * Per-runner journal for SessionCommand dedup/recovery.
   * Shared with `RunnerSignalRClient`
   * so its `registerSessionCommandHandler` reuses the in-flight
   * dedup + on-disk recovery the host owns.
   */
  private readonly sessionCommandJournal: SessionCommandJournal
  private readonly followupOperationJournal: FollowupOperationJournal
  private readonly cancelOperationJournal: CancelOperationJournal
  private readonly workResultJournal: WorkResultJournal
  private readonly recoveredStartedWork: RecoveredStartedWork
  private readonly terminalTaskLogDelivery: TerminalTaskLogDeliveryStore
  private readonly waitForConnectionRetry: (delayMs: number, signal: AbortSignal) => Promise<void>
  private readonly skillResolver = new SkillResolver()
  private readonly workResourceLimits: ResolvedWorkResourceLimits

  // Lets an out-of-loop reconnect callback bound its immediate heartbeat.
  private activeSignal: AbortSignal | null = null

  // WorkExecutor is created once per host
  // (not per work item): recreating it for every
  // executeAndReport call would leave shared lifecycle state always cold.
  private workExecutor: WorkExecutor | null = null

  // Process-lifetime reported set (see workKey/InFlightEntry doc above).
  // These Maps outlive poll exceptions and reconnects: a work enters
  // inFlight on dispatch, moves to awaitingAck when its result is ready,
  // and leaves awaitingAck only when the owner acks (Accepted or Stale).
  // The keys of both Maps together form the process's full poll report.
  private readonly inFlight = new Map<string, InFlightEntry>()
  private readonly awaitingAck = new Map<string, { work: DispatchWorkItem; entry: AwaitingAckEntry }>()
  private readonly terminalTaskLogDeliveryInFlight = new Set<string>()

  constructor(
    private readonly options: RunnerOptions,
    private readonly actions: ActionRegistry = createDefaultRegistry(),
    dependencies: RunnerHostDependencies = {},
  ) {
    this.cleanupConvergenceIntervalMs = Math.max(1000, Math.floor(options.cleanupConvergenceIntervalMs ?? 5 * 60_000))
    this.cleanupLoopIntervalMs = Math.max(1000, Math.floor(options.cleanupLoopIntervalMs ?? 2 * 60_000))
    this.workResourceLimits = normalizeWorkResourceLimits(options.workResourceLimits)
    const build = loadBuildInfo()
    this.buildInfo = build
    this.buildGitHash = build.gitHash
    this.connection = new ServerConnection(options, this.buildGitHash, build)
    // Runner-local registry of workspaces this host has materialized.
    // Loaded eagerly at startup so the in-memory cache is hot before the
    // first dispatch or SignalR RPC: active
    // entries remain active until a terminal transition is observed.
    // The registry is shared with WorkspaceManager (for materialize /
    // verify registration hooks) and RunnerSignalRClient (for the
    // RemoveWorkspace entry-removal hook).
    this.workspaceRegistry = new WorkspaceRegistry(options.runnerRoot, { runnerId: options.runnerId })
    this.namedWorkspaceRegistry = new NamedWorkspaceRegistry(options.runnerRoot)
    this.agentSessionRuntimeEventOutbox = createAgentSessionRuntimeEventOutbox({
      filePath: `${options.runnerRoot}/${RUNTIME_EVENT_OUTBOX_FILE}`,
      deliver: createServerRuntimeEventDelivery({ connection: this.connection }),
    })
    this.bindingConvergence = new BindingConvergence({
      runnerId: options.runnerId,
      connection: this.connection,
      outbox: this.agentSessionRuntimeEventOutbox,
      openCodeRuntime: () => this.openCodeRuntime,
      piRuntime: () => this.piRuntime,
      recoveryCoordinator: this.bindingRecoveryCoordinator,
    })
    this.convergence = new ConvergenceBackstop(
      this.workspaceRegistry,
      new ServerConnectionConvergenceAdapter(this.connection),
    )
    this.namedWorkspaceManager = new NamedWorkspaceManager(
      options.runnerRoot,
      this.namedWorkspaceRegistry,
      this.connection,
    )
    this.namedWorkspaceReclaimProbe = new NamedWorkspaceReclaimProbe(this.namedWorkspaceRegistry, this.connection)
    this.namedCleanupLoop = createNamedWorkspaceCleanupLoop(
      this.namedWorkspaceRegistry,
      options.runnerRoot,
      () => this.openCodeRuntime,
    )
    this.cleanupLoop = new CleanupLoop(
      this.workspaceRegistry,
      new DefaultCleanupRunner(options.runnerRoot),
      options.runnerRoot,
      () => this.openCodeRuntime,
    )
    this.workspace = new WorkspaceManager(options.runnerRoot, this.workspaceRegistry, options.runnerId)
    this.sessionCommandJournal = new SessionCommandJournal(options.runnerRoot)
    this.followupOperationJournal = new FollowupOperationJournal(options.runnerRoot)
    this.cancelOperationJournal = new CancelOperationJournal(options.runnerRoot)
    this.workResultJournal = new WorkResultJournal(options.runnerRoot)
    this.recoveredStartedWork = new RecoveredStartedWork(this.workResultJournal, this.connection)
    this.terminalTaskLogDelivery =
      dependencies.terminalTaskLogDelivery ?? new TerminalTaskLogDeliveryStoreImpl(options.runnerRoot)
    this.waitForConnectionRetry = dependencies.waitForConnectionRetry ?? delay
    this.signalR = new RunnerSignalRClient(
      options.serverUrl,
      options.runnerId,
      options.runnerRoot,
      this.buildGitHash,
      {
        onReconnected: () => this.onDispatchReconnected(),
        credential: options.credential ?? null,
        followupTargetResolver: (target) => this.resolveFollowupTarget(target),
        agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
        registry: this.workspaceRegistry,
        openCodeRuntime: () => this.openCodeRuntime,
        piRuntime: () => this.piRuntime,
        serverConnection: this.connection,
        sessionCommandJournal: this.sessionCommandJournal,
        followupOperationJournal: this.followupOperationJournal,
        cancelOperationJournal: this.cancelOperationJournal,
        bindingRecoveryCoordinator: this.bindingRecoveryCoordinator,
        skillResolver: this.skillResolver,
      },
      this.buildInfo,
    )
    this.cleanup = createHostCleanup({
      runnerRoot: options.runnerRoot,
      connection: this.connection,
      signalR: this.signalR,
      workspaceRegistry: this.workspaceRegistry,
      namedWorkspaceRegistry: this.namedWorkspaceRegistry,
      namedWorkspaceReclaimProbe: this.namedWorkspaceReclaimProbe,
      namedCleanupLoop: this.namedCleanupLoop,
      cleanupLoop: this.cleanupLoop,
      convergence: this.convergence,
      bindingConvergence: this.bindingConvergence,
      openCodeRuntime: () => this.openCodeRuntime,
    })
  }

  private resolveFollowupTarget(target: SessionTarget): FollowupTargetResolution {
    if (this.options.projectId && this.options.projectId !== target.projectId) return null
    const binding = target.binding ?? null
    if (!binding) return null
    const runtime = binding.runtime.toLowerCase()
    if (runtime !== 'opencode' && runtime !== 'pi') return null
    if (binding.runnerId !== this.options.runnerId) return null
    if (!binding.runtimeSessionId) return null
    if (!binding.workDir) return null
    const resolved: FollowupTarget = {
      runtimeSessionId: binding.runtimeSessionId,
      workDir: binding.workDir,
      projectId: target.projectId,
      ...(target.kind === 'generic' && target.definition ? { definition: target.definition } : {}),
    }
    return resolved
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
        log.error('failed to load workspace registry; starting empty', { exception: error })
      }
      // Named workspace registry: same rebuildable-index rules as the
      // workflow registry — a missing or corrupt file starts empty.
      try {
        await this.namedWorkspaceRegistry.load()
      } catch (error) {
        log.error('failed to load named workspace registry; starting empty', { exception: error })
      }
      try {
        await this.terminalTaskLogDelivery.load()
      } catch (error) {
        log.error('failed to load terminal task-log delivery store', { exception: error })
      }
      if (!this.terminalTaskLogDelivery.ready()) {
        log.warn('terminal task-log delivery store unavailable; runner admission gated')
      }
      await this.loadWorkResultJournal()
      // Probe once before any work can be admitted. Hosts without util-linux
      // remain protected by the aggregate-RSS and wall-clock watchdog.
      await probePrlimit()
      // Load the AgentSession runtime-event outbox BEFORE accepting
      // SignalR commands or claiming work. An unreadable snapshot is
      // never replaced with empty state — the outbox loads itself once
      // a successful read happens and stays unhealthy otherwise.
      await this.loadAgentSessionRuntimeEventOutbox(signal)
      await this.initializeSharedConnection(signal)
      await this.connectRunner(signal)
      // Kick a non-blocking drain: an unavailable server does not gate
      // startup; records stay durable and re-drain on reconnect.
      if (this.agentSessionRuntimeEventOutbox.ready()) {
        void this.agentSessionRuntimeEventOutbox.kick().catch(() => undefined)
      }
      // Startup convergence: pick up any terminal events the runner
      // missed while it was offline (e.g. completed while the previous
      // process was down). Runs immediately after SignalR is up so the
      // push channel is available in parallel.
      await this.cleanup.runConvergenceOnce(signal)
      await this.cleanup.runBindingConvergenceOnce(signal)
      const heartbeat = setInterval(
        () =>
          void this.connection
            .heartbeat(this.registrationState(), signal)
            .catch((error) => log.error('runner heartbeat failed', { exception: error })),
        this.options.heartbeatIntervalMs,
      )
      const selfCheck = setInterval(
        () => void this.cleanup.runSelfCheck(signal),
        this.options.dispatchLivenessProbeIntervalMs,
      )
      const convergenceTimer = setInterval(
        () => void this.cleanup.runConvergenceOnce(signal),
        this.cleanupConvergenceIntervalMs,
      )
      const cleanupTimer = setInterval(() => void this.cleanup.runCleanupOnce(signal), this.cleanupLoopIntervalMs)
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

  private onDispatchReconnected() {
    void this.sendImmediateHeartbeat()
    const signal = this.activeSignal
    if (signal) void retryPendingTerminalTaskLogs(this.taskLogDeps(), this.terminalTaskLogDeliveryInFlight, signal)
    // Convergence on every reconnect: the SignalR transport just
    // recovered, which is the cheapest moment to ask the server for the
    // truth about every active registry entry. Push may also have queued
    // events during the disconnect window; this catch-all reconciles
    // whatever push did not cover.
    if (signal) {
      void this.cleanup.runConvergenceOnce(signal)
      void this.cleanup.runBindingConvergenceOnce(signal)
      void this.cleanup.runCleanupOnce(signal)
    }
  }

  private async sendImmediateHeartbeat() {
    const signal = this.activeSignal
    if (!signal || signal.aborted) return
    try {
      await this.connection.heartbeat(this.registrationState(), signal)
    } catch (error) {
      log.error('immediate runner heartbeat failed', { exception: error })
    }
  }

  private async initializeSharedConnection(signal: AbortSignal) {
    if (this.workExecutor !== null) return
    // Construct the shared runtimes. The factory seam returns real
    // runtimes in production or fakes in tests. Readiness is limited to
    // runtime health; model validity is decided by the requested work.
    const environment = currentRunnerResources()?.environment ?? process.env
    const policy = parseProviderErrorPolicy(environment)
    if (!policy.ok) {
      this.providerPolicyDiagnostic = `provider error policy invalid (${policy.error.code}): ${policy.error.message}`
      log.error('provider error policy invalid', { reason: this.providerPolicyDiagnostic })
    } else {
      this.providerPolicyDiagnostic = null
    }
    const factory = getOpenCodeRuntimeFactory()
    this.openCodeRuntime = factory({
      directory: process.cwd(),
      ...(this.options.runtimeIdleGraceMs !== undefined ? { idleGraceMs: this.options.runtimeIdleGraceMs } : {}),
      ...(this.options.quarantineDrainTimeoutMs !== undefined
        ? { quarantineDrainTimeoutMs: this.options.quarantineDrainTimeoutMs }
        : {}),
      ...(this.options.runtimeShutdownTimeoutMs !== undefined
        ? { runtimeShutdownTimeoutMs: this.options.runtimeShutdownTimeoutMs }
        : {}),
      ...(policy.ok ? { providerErrorPolicy: policy.value } : {}),
    })
    const startResult = await this.openCodeRuntime.start(signal)
    if (!startResult.ok) {
      log.error('opencode runtime not ready at startup; claiming gated until it recovers', {
        reason: startResult.error.message,
      })
    }
    this.syncOpenCodeWorkOwners()
    this.piRuntime = getPiRuntimeFactory()({
      agentDir: this.options.runnerRoot,
      ...(this.options.runtimeShutdownTimeoutMs !== undefined
        ? { runtimeShutdownTimeoutMs: this.options.runtimeShutdownTimeoutMs }
        : {}),
      ...(policy.ok ? { providerErrorPolicy: policy.value } : {}),
    })
    const piStart = await this.piRuntime.start()
    if (this.piRuntime.ready()) this.piRuntimeGeneration += 1
    if (!piStart.ok) {
      log.error('pi runtime not ready at startup; claiming gated until it recovers', { reason: piStart.error.message })
    }
    this.workExecutor = new WorkExecutor(
      this.actions,
      this.workspace,
      this.connection,
      undefined,
      undefined,
      this.openCodeRuntime,
      new AgentJobExecutor(
        this.connection,
        {
          openCode: () => this.openCodeRuntime,
          pi: () => this.piRuntime,
        },
        this.bindingRecoveryCoordinator,
        process.cwd(),
        this.skillResolver,
        this.namedWorkspaceManager,
        { workResourceLimits: this.workResourceLimits },
      ),
      this.agentSessionRuntimeEventOutbox,
      undefined,
      this.piRuntime,
      this.bindingRecoveryCoordinator,
      this.skillResolver,
      this.namedWorkspaceManager,
      this.workResourceLimits,
    )
  }

  private async loadAgentSessionRuntimeEventOutbox(signal: AbortSignal): Promise<void> {
    const outbox = this.agentSessionRuntimeEventOutbox
    try {
      await outbox.load()
    } catch (error) {
      // Loading itself is best effort — `outbox.ready()` reflects the
      // actual durable state and gates the follow-up handler and claim
      // loop.
      log.error('agent-session runtime event outbox failed to load', { exception: error, session: 'outbox' })
    }
    if (signal.aborted) return
    if (!outbox.ready()) {
      log.warn('agent-session runtime event outbox unhealthy at startup; runner admission gated until it recovers', {
        session: 'outbox',
      })
    }
  }

  private async loadWorkResultJournal(): Promise<void> {
    try {
      await this.workResultJournal.load()
    } catch (error) {
      log.error('work result journal failed to load', { exception: error })
    }
    if (!this.workResultJournal.ready()) {
      log.warn('work result journal unavailable; runner admission gated')
      return
    }
    this.promoteDurableJournalResults(0)
    this.recoveredStartedWork.recover()
  }

  private async shutdownSharedConnection() {
    this.workExecutor = null
    if (this.openCodeRuntime !== null) {
      try {
        await this.openCodeRuntime.shutdown()
      } catch {
        /* best effort */
      }
      this.openCodeRuntime = null
    }
    if (this.piRuntime !== null) {
      try {
        await this.piRuntime.shutdown()
      } catch {
        /* best effort */
      }
      this.piRuntime = null
    }
  }

  private async runWorkerPool(signal: AbortSignal) {
    // The reported set (inFlight ∪ awaitingAck) is process-lifetime state
    // declared on the host instance, so it survives poll exceptions. Polling
    // and report retries share this one process-critical reconciliation loop;
    // no sibling lifetime task can prevent a failed poll from being retried.
    while (!signal.aborted) {
      void retryPendingTerminalTaskLogs(this.taskLogDeps(), this.terminalTaskLogDeliveryInFlight, signal)
      await this.retryDueReports()

      // Runtime readiness is sent as a claim-time witness. Polling must stay
      // alive while a runtime is unhealthy so held work can be reconciled and
      // terminal receipts can be redelivered after a restart.
      if (this.providerPolicyDiagnostic !== null) {
        if (this.providerPolicyDiagnostic !== this.lastProviderPolicyDiagnosticLogged) {
          log.warn('runner not ready; skipping poll', { reason: this.providerPolicyDiagnostic })
          this.lastProviderPolicyDiagnosticLogged = this.providerPolicyDiagnostic
        }
        await raceInterval(this.nextReconciliationInterval(), signal, [])
        continue
      }
      this.lastProviderPolicyDiagnosticLogged = null
      this.syncOpenCodeWorkOwners()
      if (this.piRuntime && !this.piRuntime.ready()) {
        const piStart = await this.piRuntime.start().catch(() => null)
        if (piStart?.ok && this.piRuntime.ready()) this.piRuntimeGeneration += 1
      }

      let works: DispatchWorkItem[]
      try {
        works = await this.pollOnce(signal)
      } catch (error) {
        if (signal.aborted) break
        log.warn('runner poll failed; retrying', { reason: `in ${this.options.pollIntervalMs}ms`, exception: error })
        await raceInterval(this.nextReconciliationInterval(), signal, [])
        continue
      }

      // A successful control-plane round is the recovery boundary for a
      // result held in memory after a local journal write failure. No result
      // becomes reportable until this retry restores its durable receipt.
      await this.retryPendingWorkResultPersistence()

      await this.prepareOpenCodeWork(works, signal)

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

        const recovery = isAgentRecoveryDispatch(work)
        let admission: Awaited<ReturnType<WorkResultJournal['begin']>>
        try {
          admission = recovery
            ? await this.workResultJournal.beginRecovery(work)
            : await this.workResultJournal.begin(work)
        } catch (error) {
          log.error('work result journal could not fence dispatch; skipping work', {
            work: work.workId,
            exception: error,
          })
          continue
        }
        if (admission !== 'new') {
          if (admission === 'started') {
            // An explicit redelivery takes precedence over the startup
            // unknown observation. It remains a hard physical-execution
            // fence: reconciliation can adopt a live bound turn or surface
            // the runner-restarted fact, but it can never call executeWork.
            this.recoveredStartedWork.drop(key)
            const done = this.reconcileStartedDispatch(work, signal, key)
            this.inFlight.set(key, { done, work, awaitingResultPersistence: false })
            this.syncOpenCodeWorkOwners()
          } else {
            log.warn('work dispatch has a durable unfinished result; retaining fence', {
              work: work.workId,
              state: admission,
            })
          }
          continue
        }
        if (recovery) {
          // The delivery-driven reconciliation supersedes the startup
          // unknown-report sweep for this identity.
          this.recoveredStartedWork.drop(key)
          if (work.agentRecovery!.runtime.trim().toLowerCase() !== 'pi') {
            // No turn-adoption API: the execution context is provably
            // gone, so surface unknown without executing anything.
            this.recoveredStartedWork.enqueue(work)
            log.warn('recovery dispatch runtime has no turn adoption; reporting unknown', {
              work: work.workId,
              runtime: work.agentRecovery!.runtime,
            })
            continue
          }
          log.info('recovery dispatch admitted for reconciliation', { work: work.workId })
        }

        const done = this.executeAndTransition(work, signal, key)
        this.inFlight.set(key, { done, work, awaitingResultPersistence: false })
        this.syncOpenCodeWorkOwners()
      }

      if (signal.aborted) break
      // Pace the next round. With nothing in flight, sleep one interval
      // before re-polling; with in-flight work, race the interval against
      // any work settling so a freed slot re-polls promptly. A failed report
      // also bounds the wait: report retries must not inherit a long poll
      // interval.
      await raceInterval(
        this.nextReconciliationInterval(),
        signal,
        [...this.inFlight.values()].filter((entry) => !entry.awaitingResultPersistence).map((entry) => entry.done),
      )
    }

    // Drain in-flight executions on abort so completed work can finish its
    // bounded first report attempt before process shutdown.
    await Promise.allSettled([...this.inFlight.values()].map((e) => e.done))
  }

  private syncOpenCodeWorkOwners(): void {
    const runtime = this.openCodeRuntime
    if (!runtime) return
    const owners = [
      ...[...this.inFlight.values()].filter((entry) => usesOpenCode(entry.work)).map((entry) => workKey(entry.work)),
      ...[...this.awaitingAck.values()].filter((entry) => usesOpenCode(entry.work)).map((entry) => workKey(entry.work)),
    ]
    runtime.setWorkOwners(owners)
  }

  private async prepareOpenCodeWork(works: readonly DispatchWorkItem[], signal: AbortSignal): Promise<void> {
    const runtime = this.openCodeRuntime
    const owners = works.filter((work) => usesOpenCode(work) && !isAgentRecoveryDispatch(work)).map(workKey)
    if (!runtime || owners.length === 0) return
    runtime.setWorkOwners([...this.openCodeOwners(), ...owners])
    if (!runtime.ready()) {
      const started = await runtime.start(signal)
      if (!started.ok) log.error('opencode runtime could not be recreated for work', { reason: started.error.message })
    }
  }

  private openCodeOwners(): string[] {
    return [
      ...[...this.inFlight.values()].filter((entry) => usesOpenCode(entry.work)).map((entry) => workKey(entry.work)),
      ...[...this.awaitingAck.values()].filter((entry) => usesOpenCode(entry.work)).map((entry) => workKey(entry.work)),
    ]
  }

  private async pollOnce(signal: AbortSignal): Promise<DispatchWorkItem[]> {
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
  private pollReport(): {
    inFlight: string[]
    awaitingAck: string[]
    runtimeReadiness: RuntimeReadinessWitness[]
    connectionId: string | null
    admissionReady: boolean
  } {
    return {
      inFlight: [...this.inFlight.keys()],
      awaitingAck: [...this.awaitingAck.keys()],
      runtimeReadiness: this.runtimeReadiness(),
      connectionId: this.signalR.getConnectionId(),
      admissionReady:
        this.providerPolicyDiagnostic === null &&
        this.terminalTaskLogDelivery.ready() &&
        this.agentSessionRuntimeEventOutbox.ready() &&
        this.workResultJournal.ready(),
    }
  }

  private runtimeReadiness(): RuntimeReadinessWitness[] {
    return [
      {
        runtime: 'opencode',
        ready: this.openCodeRuntime?.ready() === true,
        generation: this.openCodeRuntime?.ownership().generation ?? null,
      },
      {
        runtime: 'pi',
        ready: this.piRuntime?.ready() === true,
        generation: this.piRuntime?.ready() === true ? this.piRuntimeGeneration : null,
      },
    ]
  }

  private isOpenCodeReadyForClaim(): boolean {
    const runtime = this.openCodeRuntime
    return runtime !== null && runtime.ready() && this.agentSessionRuntimeEventOutbox.ready()
  }

  /**
   * Reconciles a post-restart `started` entry. A null result means the
   * runtime/server fact was not determinate yet; the entry remains started
   * and the next delivery may retry the probe. No branch in this method can
   * call the normal executor for a fenced work item.
   */
  private async reconcileStartedDispatch(work: DispatchWorkItem, signal: AbortSignal, key: string): Promise<void> {
    let result: WorkItemResult | null
    let interruption: ReturnType<typeof runnerRestartedResult>['interruption'] | undefined
    try {
      const reconciliation = await this.reconcileStartedWork(work, signal)
      if (signal.aborted || reconciliation === null) {
        this.inFlight.delete(key)
        this.syncOpenCodeWorkOwners()
        return
      }
      result = reconciliation.result
      interruption = reconciliation.interruption
    } catch (error) {
      this.inFlight.delete(key)
      this.syncOpenCodeWorkOwners()
      log.warn('started work reconciliation deferred; retaining fence', { work: work.workId, exception: error })
      return
    }

    await this.completeAndQueueResult(work, result, key, interruption)
  }

  private async reconcileStartedWork(
    work: DispatchWorkItem,
    signal: AbortSignal,
  ): Promise<{
    result: WorkItemResult
    interruption?: ReturnType<typeof runnerRestartedResult>['interruption']
  } | null> {
    if (signal.aborted) return null
    const runtimeKind = runtimeKindForWork(work)
    if (!runtimeKind) return runnerRestartedResult(work)

    const binding = await resolvePersistedWorkBinding(work, this.connection, this.options.runnerId, signal)
    if (binding.kind === 'unavailable') return null
    if (binding.kind !== 'bound') return runnerRestartedResult(work)

    const runtime = runtimeForKind(runtimeKind, this.openCodeRuntime, this.piRuntime)
    if (!runtime) return null
    const probe = await probeRuntimeBinding(runtime, binding.binding)
    if (!probe.ok) {
      // A transport/runtime failure is uncertainty, not proof that the
      // physical turn died. Missing bindings are proof of a dead execution.
      if (probe.kind !== 'missing-session') return null
      return runnerRestartedResult(work)
    }
    if (!probe.activeTurn) return runnerRestartedResult(work)

    const adopted = await reattachRuntimeTurn(runtime, binding.binding, signal)
    if (signal.aborted) return null
    return { result: projectReattachedRuntimeResult(work, runtimeKind, adopted) }
  }

  /**
   * Persists the terminal result before moving a work into awaitingAck. The
   * same helper is used for normal execution and reconciliation so the
   * journal cannot be retired before the server has acknowledged the report.
   */
  private async completeAndQueueResult(
    work: DispatchWorkItem,
    result: WorkItemResult,
    key: string,
    interruption?: ReturnType<typeof runnerRestartedResult>['interruption'],
  ): Promise<void> {
    try {
      if (interruption) await this.workResultJournal.completeInterrupted(work, result, interruption)
      else await this.workResultJournal.complete(work, result)
    } catch (error) {
      log.error('work result journal could not persist settled result', { work: work.workId, exception: error })
      this.workResultJournal.disable()
      return
    }

    this.inFlight.delete(key)
    this.awaitingAck.set(key, { work, entry: { result, attempts: 0, retryAt: null } })
    this.syncOpenCodeWorkOwners()
    try {
      await this.reportOnce(key)
    } catch (error) {
      this.scheduleReportRetry(key)
      log.warn('first work report failed; will retry', { work: work.workId, exception: error })
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
  private async executeAndTransition(work: DispatchWorkItem, signal: AbortSignal, key: string): Promise<void> {
    let result: WorkItemResult
    try {
      result = await executeWork(
        this.taskLogDeps(),
        this.workExecutor,
        this.terminalTaskLogDeliveryInFlight,
        work,
        signal,
      )
    } catch (error) {
      if (signal.aborted) return
      log.error('work failed before report', { work: work.workId, exception: error })
      result = { status: 'failed', message: String(error) }
    }
    // A returned result is authoritative even when shutdown raced with its
    // delivery. Persist it before the host releases the work; only an abort
    // that prevented a result from returning stays as the started fence above.
    let persistence: Awaited<ReturnType<WorkResultJournal['complete']>>
    try {
      persistence = await this.workResultJournal.complete(work, result)
    } catch (error) {
      log.error('work result journal could not persist settled result', { work: work.workId, exception: error })
      // Keep the work in `inFlight` and stop admission. Reporting a result
      // without a durable local copy would turn a restart into result loss.
      this.markResultPersistencePending(key)
      this.workResultJournal.disable()
      return
    }

    if (persistence.state === 'pending' || !this.workResultJournal.ready()) {
      this.markResultPersistencePending(key)
      if (persistence.state === 'pending') {
        log.warn('work result journal persistence deferred; retaining result in memory', {
          work: work.workId,
          exception: persistence.error,
        })
      }
      return
    }

    await this.promoteAndReportDurableJournalResults()
    if (!this.workResultJournal.ready()) this.markResultPersistencePending(key)
  }

  private async retryPendingWorkResultPersistence(): Promise<void> {
    if (!this.workResultJournal.needsPersistenceRecovery()) return
    const persistence = await this.workResultJournal.retryPendingPersistence()
    if (persistence.state === 'pending') {
      log.warn('work result journal persistence recovery is still unavailable', { exception: persistence.error })
      return
    }
    await this.promoteAndReportDurableJournalResults()
  }

  private markResultPersistencePending(key: string): void {
    const held = this.inFlight.get(key)
    if (held) held.awaitingResultPersistence = true
  }

  private promoteDurableJournalResults(retryAt: number | null = null): string[] {
    if (!this.workResultJournal.ready()) return []
    const promoted: string[] = []
    for (const entry of this.workResultJournal.completed()) {
      const key = workKey(entry.work)
      if (this.awaitingAck.has(key)) continue
      this.inFlight.delete(key)
      this.awaitingAck.set(key, {
        work: entry.work,
        entry: { result: entry.result!, attempts: 0, retryAt },
      })
      promoted.push(key)
    }
    this.syncOpenCodeWorkOwners()
    return promoted
  }

  private async promoteAndReportDurableJournalResults(): Promise<void> {
    for (const key of this.promoteDurableJournalResults()) {
      const held = this.awaitingAck.get(key)
      if (!held) continue
      try {
        await this.reportOnce(key)
      } catch (error) {
        this.scheduleReportRetry(key)
        log.warn('first work report failed; will retry', { work: held.work.workId, exception: error })
      }
    }
  }

  /**
   * Reports a single awaitingAck entry. Accepted and stale reports are both
   * durable acknowledgements. An untracked response leaves the original
   * result in place for reconciliation rather than silently dropping it.
   */
  private async reportOnce(key: string): Promise<void> {
    const held = this.awaitingAck.get(key)
    if (!held) return
    held.entry.attempts += 1
    await reportAndRequireDurableAck(this.connection, held.work, held.entry.result)
    await this.workResultJournal.acknowledge(held.work)
    this.awaitingAck.delete(key)
    this.syncOpenCodeWorkOwners()
  }

  private scheduleReportRetry(key: string): void {
    const held = this.awaitingAck.get(key)
    if (held) held.entry.retryAt = Date.now() + AWAITING_ACK_RETRY_INTERVAL_MS
  }

  private async retryDueReports(): Promise<void> {
    const now = Date.now()
    const due = [...this.awaitingAck.entries()].filter(
      ([, held]) => held.entry.retryAt !== null && held.entry.retryAt <= now,
    )

    await Promise.all(
      due.map(async ([key, held]) => {
        held.entry.retryAt = null
        try {
          await this.reportOnce(key)
        } catch (error) {
          this.scheduleReportRetry(key)
          log.warn('work report retry failed', {
            work: held.work.workId,
            attempt: held.entry.attempts,
            exception: error,
          })
        }
      }),
    )
    await this.recoveredStartedWork.retryDue(now)
  }

  private nextReconciliationInterval(): number {
    let earliestRetryAt: number | null = null
    for (const { entry } of this.awaitingAck.values()) {
      if (entry.retryAt !== null && (earliestRetryAt === null || entry.retryAt < earliestRetryAt)) {
        earliestRetryAt = entry.retryAt
      }
    }
    earliestRetryAt = this.recoveredStartedWork.earlierRetryAt(earliestRetryAt)
    if (earliestRetryAt === null) return this.options.pollIntervalMs
    return Math.min(this.options.pollIntervalMs, Math.max(0, earliestRetryAt - Date.now()))
  }

  private taskLogDeps(): HostTaskLogDeps {
    return {
      connection: this.connection,
      terminalTaskLogDelivery: this.terminalTaskLogDelivery,
      options: this.options,
    }
  }

  private async shutdownConnection() {
    const cleanup = new AbortController()
    const timeout = setTimeout(() => cleanup.abort(), 5_000)
    timeout.unref?.()
    try {
      await Promise.allSettled([this.connection.disconnect(cleanup.signal), this.signalR.stop()])
    } finally {
      clearTimeout(timeout)
    }
  }

  private registrationState(): RunnerRegistration {
    return buildRegistrationState(this.options, this.piRuntime, this.actions.catalog(), () =>
      this.signalR.getConnectionId(),
    )
  }

  private async connectRunner(signal: AbortSignal) {
    while (!signal.aborted) {
      try {
        await this.connection.connect(
          {
            ...this.registrationState(),
            buildGitHash: this.buildGitHash,
            component: this.buildInfo.component,
            version: this.buildInfo.version,
            sourceRevision: this.buildInfo.sourceRevision ?? this.buildInfo.gitHash,
            treeHash: this.buildInfo.treeHash,
            artifactDigest: this.buildInfo.artifactDigest,
            releaseId: this.buildInfo.releaseId,
            generation: this.buildInfo.generation,
            runnerId: this.buildInfo.runnerId ?? this.options.runnerId,
          },
          signal,
        )
        await this.signalR.start()
        return
      } catch (error) {
        log.error('runner connection failed; retrying', {
          reason: `in ${this.options.pollIntervalMs}ms`,
          exception: error,
        })
        await this.disconnectForReconnect()
        await this.waitForConnectionRetry(this.options.pollIntervalMs, signal)
      }
    }
  }

  private async disconnectForReconnect() {
    const cleanup = new AbortController()
    const timeout = setTimeout(() => cleanup.abort(), 5_000)
    timeout.unref?.()
    try {
      await Promise.allSettled([this.connection.disconnect(cleanup.signal), this.signalR.disconnect()])
    } finally {
      clearTimeout(timeout)
    }
  }
}
