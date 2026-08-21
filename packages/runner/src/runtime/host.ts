import { randomUUID } from 'node:crypto'
import type { RunnerOptions, RunnerRegistration } from '../core/types.js'
import { ServerConnection } from '../server/connection.js'
import { RunnerControlWebSocketClient } from '../server/runner-control-websocket.js'
import { createRunnerControlHandlers } from '../server/runner-control-handlers.js'
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
import { executeWork, retryPendingTerminalTaskLogs } from './host-task-log.js'
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
export { getRunnerBuildGitHash } from './build-info.js'
import type { DispatchWorkItem, PolledDispatch } from '../core/types.js'
import type { WorkItemResult } from '../core/types.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { WorkflowSessionTurnCoordinator } from './workflow-session-turn-coordinator.js'
import { SkillResolver } from './skill-resolver.js'
import { runnerLogger } from '../system/logger.js'
import {
  buildRunnerPollReport,
  createManagerExecutionBoundary,
  gateManagerCapabilities,
  isManagerExecutionWork,
  supportsManagerExecution,
  createHostTaskLogDeps,
  currentCatalogRevision,
  isAgentRecoveryDispatch,
  isOpenCodeReadyForClaim as isOpenCodeReadyForClaimForRuntime,
  resolveFollowupTarget,
  runtimeReadinessWitnesses,
  openCodeOwners as openCodeOwnersForRuntime,
  syncOpenCodeWorkOwners as syncOpenCodeWorkOwnersForRuntime,
  usesOpenCode,
} from './host-helpers.js'
import { resolveWorkspaceQuery } from './workspace-query.js'
import { createSessionCommandRouter } from '../server/command-runtime.js'
import { reconcileStartedDispatch, type HostRecoveryContext } from './host-recovery.js'
import { type AwaitingAckEntry, type InFlightEntry, type ShutdownWorkState } from './host-state.js'
import { ManagerExecutionBoundary } from './manager-execution-boundary.js'
import { ManagerExecutionRegistry } from './manager-execution-registry.js'
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

export class RunnerHost {
  private readonly connection: ServerConnection
  private readonly control: RunnerControlWebSocketClient
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
   * Shared with the control transport so its Session command handler reuses the in-flight
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
  private readonly managerExecutions = new Map<string, ManagerExecutionBoundary>()
  private readonly managerExecutionRegistry = new ManagerExecutionRegistry()
  private observedManagerDeploymentEpoch: string | null = null
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
    // first dispatch or control RPC: active
    // entries remain active until a terminal transition is observed.
    // The registry is shared with WorkspaceManager (for materialize /
    // verify registration hooks) and control handlers (for the
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
    this.control = new RunnerControlWebSocketClient(
      options.serverUrl,
      options.runnerId,
      options.runnerRoot,
      this.buildGitHash,
      {
        onReconnected: () => this.onDispatchReconnected(),
        credential: options.credential ?? null,
        handlers: createRunnerControlHandlers({
          workspaceGit: { resolveQuery: resolveWorkspaceQuery, runnerRoot: options.runnerRoot },
          workspaceRemoval: {
            runnerRoot: options.runnerRoot,
            registry: this.workspaceRegistry,
            removalFence: () => this.openCodeRuntime,
          },
          followup: {
            followupTargetResolver: (target) => resolveFollowupTarget(this.options, target),
            agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
            openCodeRuntime: () => this.openCodeRuntime,
            piRuntime: () => this.piRuntime,
            connection: this.connection,
            runnerId: options.runnerId,
            runnerRoot: options.runnerRoot,
            managerExecutionRegistry: this.managerExecutionRegistry,
            onManagerExecutionFinished: (executionId) => this.revokeManagerExecution(executionId),
            followupOperationJournal: this.followupOperationJournal,
            bindingRecoveryCoordinator: this.bindingRecoveryCoordinator,
            skillResolver: this.skillResolver,
            strictExecutionSourceValidation: options.strictExecutionSourceValidation === true,
          },
          cancel: {
            followupTargetResolver: (target) => resolveFollowupTarget(this.options, target),
            openCodeRuntime: () => this.openCodeRuntime,
            piRuntime: () => this.piRuntime,
            agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
            managerExecutionRegistry: this.managerExecutionRegistry,
            onManagerExecutionFinished: (executionId) => this.revokeManagerExecution(executionId),
            cancelOperationJournal: this.cancelOperationJournal,
          },
          sessionCommand: {
            handler: createSessionCommandRouter(
              { openCode: () => this.openCodeRuntime, pi: () => this.piRuntime },
              this.agentSessionRuntimeEventOutbox,
            ),
            journal: this.sessionCommandJournal,
          },
          onWorkflowStatusChanged: async () => {
            const signal = this.activeSignal
            if (signal && !signal.aborted) await this.cleanup.runConvergenceOnce(signal)
          },
        }),
        agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
        sessionCommandJournal: this.sessionCommandJournal,
        followupOperationJournal: this.followupOperationJournal,
        cancelOperationJournal: this.cancelOperationJournal,
        strictExecutionSourceValidation: options.strictExecutionSourceValidation === true,
      },
      this.buildInfo,
    )
    this.cleanup = createHostCleanup({
      runnerRoot: options.runnerRoot,
      connection: this.connection,
      control: this.control,
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
      taskLogDeps: () => createHostTaskLogDeps(this.connection, this.terminalTaskLogDelivery, this.options),
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
      currentCatalogRevision: (runtime) => currentCatalogRevision(this.registrationState().runtimeCatalogs, runtime),
      managerExecutionFor: (key) => this.managerExecutions.get(key) ?? null,
      releaseManagerExecution: async (key) => {
        const boundary = this.managerExecutions.get(key)
        if (!boundary) return
        this.managerExecutions.delete(key)
        await this.managerExecutionRegistry.dispose(boundary)
      },
    }
  }

  async run(signal: AbortSignal) {
    this.activeSignal = signal
    try {
      // Load the runner-local workspace registry before any dispatch /
      // control WebSocket RPC can fire. A missing file is treated as an empty
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
      // control WebSocket commands or claiming work. An unreadable snapshot is
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
      // process was down). Runs immediately after control WebSocket is up so the
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
      void retryPendingTerminalTaskLogs(
        createHostTaskLogDeps(this.connection, this.terminalTaskLogDelivery, this.options),
        this.terminalTaskLogDeliveryInFlight,
        signal,
      )
    // Convergence on every reconnect: the control WebSocket transport just
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
        { strictExecutionSourceValidation: this.options.strictExecutionSourceValidation === true },
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
    await this.invalidateManagerExecutions()
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
      void retryPendingTerminalTaskLogs(
        createHostTaskLogDeps(this.connection, this.terminalTaskLogDelivery, this.options),
        this.terminalTaskLogDeliveryInFlight,
        signal,
      )
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

      let works: PolledDispatch[]
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

      await this.prepareOpenCodeWork(
        works.map((item) => item.work),
        signal,
      )

      // A single poll may return multiple dispatches (repair + new claims).
      // Execute each concurrently, skipping re-deliveries the process
      // already holds.
      const startupRecoveryKeys = new Set<string>()
      for (const polled of works) {
        const work = polled.work
        if (signal.aborted) break
        const key = workKey(work)
        // Re-delivery is the normal recovery path under at-least-once:
        // skip a work the process already holds (inFlight or awaitingAck)
        // rather than execute it twice. The server may re-dispatch a
        // Running work it thinks we lost; if we still have it, we know
        // better.
        if (this.inFlight.has(key) || this.awaitingAck.has(key)) continue

        const isManagerExecution = isManagerExecutionWork(work)
        let managerBoundary: ManagerExecutionBoundary | null = null
        if (isManagerExecution) {
          if (!supportsManagerExecution(this.registrationState()) || !polled.managerExecutionGrant) continue
          managerBoundary = await createManagerExecutionBoundary(
            polled.managerExecutionGrant,
            this.options.runnerRoot,
            {
              workDir: this.options.runnerRoot,
            },
          )
          if (!managerBoundary) continue
        }

        const startupObserved = this.recoveredStartedWork.has(key)
        const recovery = isAgentRecoveryDispatch(work)
        let admission: Awaited<ReturnType<WorkResultJournal['begin']>>
        try {
          admission = recovery
            ? await this.workResultJournal.beginRecovery(work)
            : await this.workResultJournal.begin(work)
        } catch (error) {
          await managerBoundary?.dispose().catch(() => undefined)
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
          await managerBoundary?.dispose().catch(() => undefined)
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
          await managerBoundary?.dispose().catch(() => undefined)
          this.syncOpenCodeWorkOwners()
          continue
        }
        if (admission !== 'new' && (!recovery || admission === 'completed')) {
          await managerBoundary?.dispose().catch(() => undefined)
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
            await managerBoundary?.dispose().catch(() => undefined)
            log.warn('recovery dispatch runtime has no turn adoption; reporting unknown', {
              work: work.workId,
              runtime: work.agentRecovery!.runtime,
            })
            continue
          }
          log.info('recovery dispatch admitted for reconciliation', { work: work.workId })
        }

        if (isManagerExecution && managerBoundary) {
          this.managerExecutions.set(key, managerBoundary)
          this.managerExecutionRegistry.register({
            executionId: polled.managerExecutionGrant!.executionId,
            boundary: managerBoundary,
            sessionId: '',
            runtimeSessionId: '',
            workDir: this.options.runnerRoot,
          })
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
    syncOpenCodeWorkOwnersForRuntime(this.openCodeRuntime, this.inFlight.values(), this.awaitingAck.values())
  }

  private async prepareOpenCodeWork(works: readonly DispatchWorkItem[], signal: AbortSignal): Promise<void> {
    const runtime = this.openCodeRuntime
    const owners = works
      .filter((work) => usesOpenCode(work) && !isManagerExecutionWork(work) && !isAgentRecoveryDispatch(work))
      .map(workKey)
    if (!runtime || owners.length === 0) return
    runtime.setWorkOwners([...openCodeOwnersForRuntime(this.inFlight.values(), this.awaitingAck.values()), ...owners])
    if (!runtime.ready()) {
      const started = await runtime.start(signal)
      if (!started.ok) log.error('opencode runtime could not be recreated for work', { reason: started.error.message })
    }
  }

  private async pollOnce(signal: AbortSignal): Promise<PolledDispatch[]> {
    const bounded = boundedSignal(signal, POLL_TIMEOUT_MS)
    try {
      const workItems = await this.connection.poll(bounded.signal, this.pollReport())
      const takeLast = (
        this.connection as ServerConnection & {
          takeLastPolledDispatches?: (items: readonly DispatchWorkItem[]) => PolledDispatch[]
        }
      ).takeLastPolledDispatches
      const works = takeLast ? takeLast.call(this.connection, workItems) : workItems.map((work) => ({ work }))
      const epoch = this.connection.deploymentEpoch
      if (epoch && this.observedManagerDeploymentEpoch && epoch !== this.observedManagerDeploymentEpoch) {
        await this.invalidateManagerExecutions()
      }
      if (epoch) this.observedManagerDeploymentEpoch = epoch
      return works
    } finally {
      bounded.dispose()
    }
  }

  private async revokeManagerExecution(executionId: string): Promise<void> {
    if (!executionId) return
    try {
      await this.connection.revokeManagerExecution(executionId, new AbortController().signal)
    } catch (error) {
      log.warn('Manager execution revocation could not be delivered', { executionId, exception: error })
    }
  }

  private async invalidateManagerExecutions(): Promise<void> {
    for (const entry of this.inFlight.values()) {
      if (entry.work.projectId !== '__mohist_slack_manager__') continue
      entry.managerInvalidated = true
      entry.controller.abort(new Error('manager deployment epoch changed'))
    }
    const boundaries = [...this.managerExecutions.values()]
    this.managerExecutions.clear()
    await this.managerExecutionRegistry.disposeAll()
    await Promise.allSettled(boundaries.map((boundary) => boundary.dispose()))
  }

  /**
   * The process's full level state, sent in every poll body so the server
   * can reconcile (Batch 2). In Batch 1 the server ignores the body; the
   * value of sending it now is that the reported set is correct the moment
   * the server starts consuming it, with no second runner-side change.
   */
  private pollReport(): ReturnType<typeof buildRunnerPollReport> {
    return buildRunnerPollReport({
      durableStarted: this.workResultJournal.ready()
        ? this.workResultJournal.started().map((entry) => workKey(entry.work))
        : [],
      inFlight: this.inFlight.keys(),
      awaitingAck: this.awaitingAck.keys(),
      runtimeReadiness: runtimeReadinessWitnesses(this.openCodeRuntime, this.piRuntime, this.piRuntimeGeneration),
      connectionId: this.control.getConnectionId(),
      admissionReady:
        this.providerPolicyDiagnostic === null &&
        this.terminalTaskLogDelivery.ready() &&
        this.agentSessionRuntimeEventOutbox.ready() &&
        this.workResultJournal.ready(),
      deploymentEpoch: this.connection.deploymentEpoch,
    })
  }

  private isOpenCodeReadyForClaim(): boolean {
    return isOpenCodeReadyForClaimForRuntime(this.openCodeRuntime, this.agentSessionRuntimeEventOutbox)
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
      await Promise.allSettled([this.connection.disconnect(cleanup.signal), this.control.stop()])
    } finally {
      clearTimeout(timeout)
    }
  }

  private registrationState(): RunnerRegistration {
    return gateManagerCapabilities(
      buildRegistrationState(this.options, this.piRuntime, this.actions.catalog(), () =>
        this.control.getConnectionId(),
      ),
      this.openCodeRuntime?.ready() === true,
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
        await this.control.start(signal)
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
      await Promise.allSettled([this.connection.disconnect(cleanup.signal), this.control.disconnect()])
    } finally {
      clearTimeout(timeout)
    }
  }
}
