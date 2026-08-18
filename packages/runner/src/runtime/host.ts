import { randomUUID } from 'node:crypto'
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
import { BindingRecoveryCoordinator } from './binding-recovery.js'
import { CleanupLoop, DefaultCleanupRunner } from './cleanup-loop.js'
import { WorkExecutor } from './executor.js'
import { AgentJobExecutor } from './agent-job-executor.js'
import { TaskLogCollector } from './task-log.js'
import { createHostCleanup } from './host-cleanup.js'
import { executeWork, retryPendingTerminalTaskLogs, type HostTaskLogDeps } from './host-task-log.js'
import {
  AWAITING_ACK_RETRY_INTERVAL_MS,
  POLL_TIMEOUT_MS,
  boundedSignal,
  delay as hostDelay,
  raceInterval,
  withTimeout,
} from './host-timing.js'
import {
  createHostShutdown,
  isShutdownFailureResult,
  isSyntheticStopResult,
  positiveBudget,
  SHUTDOWN_HANDOFF_BUDGET_MS,
} from './host-update-shutdown.js'
import { TerminalTaskLogDeliveryStoreImpl, type TerminalTaskLogDeliveryStore } from './terminal-task-log-delivery.js'
import { getOpenCodeRuntimeFactory, type OpenCodeRuntime } from './opencode/index.js'
import { getPiRuntimeFactory, parseProviderErrorPolicy, type PiRuntime } from './pi/index.js'
import { SessionCommandJournal } from './session-command-journal.js'
import { FollowupOperationJournal } from './followup-operation-journal.js'
import { CancelOperationJournal } from './cancel-operation-journal.js'
import { WorkResultJournal, workKey as journalWorkKey } from './work-result-journal.js'
import { RecoveredStartedWork } from './recovered-started-work.js'
import {
  createTerminalRecoveryReceipt,
  type PendingUpdateOperation,
  type RuntimeRecoveryReceipt,
} from './recovery-receipt.js'
import { RuntimeTurnRegistry } from './runtime-turn-registry.js'
import { loadBuildInfo } from './build-info.js'
import type { DispatchWorkItem } from '../core/types.js'
import type { WorkItemResult } from '../core/types.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { WorkflowSessionTurnCoordinator } from './workflow-session-turn-coordinator.js'
import { SkillResolver } from './skill-resolver.js'
import { runnerLogger } from '../system/logger.js'
import { type FollowupTarget, type FollowupTargetResolution, type SessionTarget } from '../server/session-target.js'
import { isAgentRecoveryDispatch, usesOpenCode } from './host-helpers.js'
import { reconcileStartedDispatch, type HostRecoveryContext } from './host-recovery.js'
import { type AwaitingAckEntry, type InFlightEntry, type ShutdownWorkState } from './host-state.js'
import {
  executeAndTransition,
  markResultPersistencePending,
  nextReconciliationInterval,
  promoteAndReportDurableJournalResults,
  promoteDurableJournalResults,
  reportOnce,
  retryDueReports,
  retryPendingWorkResultPersistence,
  scheduleReportRetry,
  type HostExecutionContext,
} from './host-execution.js'

export { startTaskLogFlushTrigger } from './host-task-log.js'

const log = runnerLogger.child('host')

export interface ReportResult {
  workflowRunId?: string | null
  workflowStatus?: string | null
}

export interface RunnerHostDependencies {
  terminalTaskLogDelivery?: TerminalTaskLogDeliveryStore
  waitForConnectionRetry?: (delayMs: number, signal: AbortSignal) => Promise<void>
  fetchPendingUpdateOperation?: (signal: AbortSignal) => Promise<PendingUpdateOperation | null>
  shutdownHandoffBudgetMs?: number
  shutdownStopBudgetMs?: number
  receiptId?: () => string
}

/**
 * Builds the work key used to dedupe in-flight / awaiting-ack tracking.
 * `ownerKind:ownerId:workId`. The ownerId is the agentJobId for agent-job
 * work, the workflowRunId for workflow work. Matches the server-side
 * `workKey` convention.
 */
const workKey = journalWorkKey

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
  private readonly runtimeTurnRegistry = new RuntimeTurnRegistry()
  private readonly fetchPendingUpdateOperation: (signal: AbortSignal) => Promise<PendingUpdateOperation | null>
  private readonly shutdownHandoffBudgetMs: number
  private readonly shutdownStopBudgetMs: number
  private readonly receiptId: () => string
  private readonly terminalTaskLogDelivery: TerminalTaskLogDeliveryStore
  private readonly hostShutdown: ReturnType<typeof createHostShutdown>
  private readonly waitForConnectionRetry: (delayMs: number, signal: AbortSignal) => Promise<void>
  private readonly skillResolver = new SkillResolver()

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
    this.waitForConnectionRetry = dependencies.waitForConnectionRetry ?? hostDelay
    this.shutdownHandoffBudgetMs = positiveBudget(dependencies.shutdownHandoffBudgetMs, SHUTDOWN_HANDOFF_BUDGET_MS)
    this.shutdownStopBudgetMs = positiveBudget(dependencies.shutdownStopBudgetMs, 2_000)
    this.receiptId = dependencies.receiptId ?? (() => `receipt-${randomUUID()}`)
    this.fetchPendingUpdateOperation =
      dependencies.fetchPendingUpdateOperation ??
      (async (signal) => {
        const connection = this.connection as ServerConnection & {
          fetchPendingUpdateOperation?: (requestSignal: AbortSignal) => Promise<PendingUpdateOperation | null>
        }
        return typeof connection.fetchPendingUpdateOperation === 'function'
          ? await connection.fetchPendingUpdateOperation(signal)
          : null
      })
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
    this.hostShutdown = createHostShutdown({
      options: this.options,
      connection: this.connection,
      openCodeRuntime: () => this.openCodeRuntime,
      piRuntime: () => this.piRuntime,
      inFlight: this.inFlight,
      awaitingAck: this.awaitingAck,
      runtimeTurnRegistry: this.runtimeTurnRegistry,
      workResultJournal: this.workResultJournal,
      receiptId: this.receiptId,
      reportOnce: (key, signal) => reportOnce(this.executionContext, key, signal),
      scheduleReportRetry: (key) => scheduleReportRetry(this.executionContext, key),
      syncOpenCodeWorkOwners: () => this.syncOpenCodeWorkOwners(),
      fetchPendingUpdateOperation: this.fetchPendingUpdateOperation,
      shutdownHandoffBudgetMs: this.shutdownHandoffBudgetMs,
      shutdownStopBudgetMs: this.shutdownStopBudgetMs,
    })
  }

  /**
   * Lightweight projection of the host's process-lifetime state for the
   * helpers under {@link ./host-execution.ts}. The host passes this object
   * by reference; the helpers see only the surface they were designed for.
   */
  private get executionContext(): HostExecutionContext {
    return {
      options: this.options,
      connection: this.connection,
      receiptId: this.receiptId,
      taskLogDeps: () => this.taskLogDepsForExecution(),
      workExecutorRef: () => this.workExecutor,
      workResultJournal: this.workResultJournal,
      runtimeTurnRegistry: this.runtimeTurnRegistry,
      recoveredStartedWork: this.recoveredStartedWork,
      terminalTaskLogDelivery: this.terminalTaskLogDelivery,
      terminalTaskLogDeliveryInFlight: this.terminalTaskLogDeliveryInFlight,
      syncOpenCodeWorkOwners: () => this.syncOpenCodeWorkOwners(),
      inFlight: this.inFlight,
      awaitingAck: this.awaitingAck,
      hostShutdown: this.hostShutdown,
      currentCatalogRevision: (runtime) => this.currentCatalogRevision(runtime),
    }
  }

  private currentCatalogRevision(runtime: string): string | null {
    const normalized = runtime.trim().toLowerCase()
    const catalogs = this.registrationState().runtimeCatalogs
    if (!catalogs) return null
    for (const [key, entry] of Object.entries(catalogs)) {
      if (key.trim().toLowerCase() === normalized) return entry.capabilityRevision ?? null
    }
    return null
  }

  private taskLogDepsForExecution(): HostTaskLogDeps {
    return {
      connection: this.connection,
      terminalTaskLogDelivery: this.terminalTaskLogDelivery,
      options: this.options,
    }
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
    if (signal)
      void retryPendingTerminalTaskLogs(this.taskLogDepsForExecution(), this.terminalTaskLogDeliveryInFlight, signal)
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
        undefined,
        this.runtimeTurnRegistry,
      ),
      this.agentSessionRuntimeEventOutbox,
      undefined,
      this.piRuntime,
      this.bindingRecoveryCoordinator,
      this.skillResolver,
      this.namedWorkspaceManager,
      this.runtimeTurnRegistry,
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
    promoteDurableJournalResults(this.executionContext, 0)
    this.recoveredStartedWork.recover()
    for (const entry of this.workResultJournal.interrupted()) {
      const key = workKey(entry.work)
      if (this.awaitingAck.has(key) || !entry.receipt) continue
      this.awaitingAck.set(key, {
        work: entry.work,
        entry: {
          result: { status: 'interrupted' },
          receipt: entry.receipt,
          attempts: 0,
          retryAt: 0,
        },
      })
    }
    this.syncOpenCodeWorkOwners()
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
      void retryPendingTerminalTaskLogs(this.taskLogDepsForExecution(), this.terminalTaskLogDeliveryInFlight, signal)
      await retryDueReports(this.executionContext)

      // Runtime readiness is sent as a claim-time witness. Polling must stay
      // alive while a runtime is unhealthy so held work can be reconciled and
      // terminal receipts can be redelivered after a restart.
      if (this.providerPolicyDiagnostic !== null) {
        if (this.providerPolicyDiagnostic !== this.lastProviderPolicyDiagnosticLogged) {
          log.warn('runner not ready; skipping poll', { reason: this.providerPolicyDiagnostic })
          this.lastProviderPolicyDiagnosticLogged = this.providerPolicyDiagnostic
        }
        await raceInterval(nextReconciliationInterval(this.executionContext), signal, [])
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
        await raceInterval(nextReconciliationInterval(this.executionContext), signal, [])
        continue
      }

      // A successful control-plane round is the recovery boundary for a
      // result held in memory after a local journal write failure. No result
      // becomes reportable until this retry restores its durable receipt.
      await retryPendingWorkResultPersistence(this.executionContext)

      await this.prepareOpenCodeWork(works, signal)

      // A single poll may return multiple dispatches (repair + new claims).
      // Execute each concurrently, skipping re-deliveries the process
      // already holds.
      const startupRecoveryKeys = new Set<string>()
      for (const work of works) {
        if (signal.aborted) break
        const key = workKey(work)
        // Re-delivery is the normal recovery path under at-least-once:
        // skip a work the process already holds (inFlight or awaitingAck)
        // rather than execute it twice. The server may re-dispatch a
        // Running work it thinks we lost; if we still have it, we know
        // better.
        if (this.inFlight.has(key) || this.awaitingAck.has(key)) continue

        const startupObserved = this.recoveredStartedWork.has(key)
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
        if (!recovery && startupObserved && admission === 'new') {
          // The startup observation may have retired the durable fence just
          // before this same identity was redelivered. Re-arm it locally and
          // reconcile the delivery instead of treating it as fresh work.
          this.recoveredStartedWork.drop(key)
          const controller = new AbortController()
          const entry: InFlightEntry = { done: Promise.resolve(), work, awaitingResultPersistence: false, controller }
          entry.done = reconcileStartedDispatch(this.recoveryContext(), work, controller.signal, key)
          this.inFlight.set(key, entry)
          this.syncOpenCodeWorkOwners()
          continue
        }
        if (admission === 'started' && !recovery) {
          // A redelivered started fence can adopt a live bound turn or
          // produce the runner-restarted observation; it never re-executes.
          this.recoveredStartedWork.drop(key)
          const controller = new AbortController()
          const entry: InFlightEntry = { done: Promise.resolve(), work, awaitingResultPersistence: false, controller }
          entry.done = reconcileStartedDispatch(this.recoveryContext(), work, controller.signal, key)
          this.inFlight.set(key, entry)
          this.syncOpenCodeWorkOwners()
          continue
        }
        if (admission !== 'new' && (!recovery || admission === 'completed')) {
          log.warn('work dispatch has a durable unfinished result; retaining fence', {
            work: work.workId,
            state: admission,
          })
          continue
        }
        if (recovery) {
          // The delivery-driven reconciliation supersedes the startup
          // unknown-report sweep for this identity.
          if (startupObserved) startupRecoveryKeys.add(key)
          if (work.agentRecovery!.runtime.trim().toLowerCase() !== 'pi') {
            this.recoveredStartedWork.drop(key)
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

        const controller = new AbortController()
        const entry: InFlightEntry = {
          done: Promise.resolve(),
          work,
          awaitingResultPersistence: false,
          controller,
        }
        entry.done = executeAndTransition(this.executionContext, work, controller.signal, key, entry)
        this.inFlight.set(key, entry)

        this.syncOpenCodeWorkOwners()
      }

      await this.recoveredStartedWork.retryDue(Date.now())
      for (const key of startupRecoveryKeys) this.recoveredStartedWork.drop(key)

      if (signal.aborted) break
      // Pace the next round. With nothing in flight, sleep one interval
      // before re-polling; with in-flight work, race the interval against
      // any work settling so a freed slot re-polls promptly. A failed report
      // also bounds the wait: report retries must not inherit a long poll
      // interval.
      await raceInterval(
        nextReconciliationInterval(this.executionContext),
        signal,
        [...this.inFlight.values()].filter((entry) => !entry.awaitingResultPersistence).map((entry) => entry.done),
      )
    }

    // Shutdown is deliberately bounded. The handoff identifies an update
    // operation before runtime stop is attempted; a missing or unavailable
    // handoff leaves the started fences untouched.
    await this.hostShutdown.shutdownInFlight()
    await withTimeout(Promise.allSettled([...this.inFlight.values()].map((e) => e.done)), this.shutdownStopBudgetMs)
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
    const durableStarted = this.workResultJournal.ready()
      ? this.workResultJournal.started().map((entry) => workKey(entry.work))
      : []
    return {
      // A started journal entry survives a Runner restart without an
      // execution promise. Report it as held so Server reconciliation does
      // not keep redelivering an identity that the journal will refuse to
      // execute.
      inFlight: [...new Set([...this.inFlight.keys(), ...durableStarted])],
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

  private recoveryContext(): HostRecoveryContext {
    return {
      connection: this.connection,
      runnerId: this.options.runnerId,
      openCodeRuntime: this.openCodeRuntime,
      piRuntime: this.piRuntime,
      workResultJournal: this.workResultJournal,
      removeInFlight: (key) => this.inFlight.delete(key),
      queueAwaitingAck: (key, work, result) =>
        this.awaitingAck.set(key, { work, entry: { result, attempts: 0, retryAt: null } }),
      syncOpenCodeWorkOwners: () => this.syncOpenCodeWorkOwners(),
      reportOnce: (key) => reportOnce(this.executionContext, key),
      scheduleReportRetry: (key) => scheduleReportRetry(this.executionContext, key),
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
