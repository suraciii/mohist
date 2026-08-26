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
import type { PendingUpdateOperation } from './update-operation.js'
import { workKey } from './work-key.js'
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
  isOpenCodeReadyForClaim as isOpenCodeReadyForClaimForRuntime,
  resolveFollowupTarget,
  runtimeReadinessWitnesses,
  openCodeOwners as openCodeOwnersForRuntime,
  syncOpenCodeWorkOwners as syncOpenCodeWorkOwnersForRuntime,
  usesOpenCode,
} from './host-helpers.js'
import { resolveWorkspaceQuery } from './workspace-query.js'
import { createSessionCommandRouter } from '../server/command-runtime.js'
import { type AwaitingAckEntry, type InFlightEntry, type ShutdownWorkState } from './host-state.js'
import { ManagerExecutionBoundary } from './manager-execution-boundary.js'
import { ManagerExecutionRegistry } from './manager-execution-registry.js'
import {
  invalidateManagerExecutions,
  observeManagerDeploymentEpoch,
  revokeManagerExecution,
} from './manager-execution-lifecycle.js'
import {
  executeAndTransition,
  nextReconciliationInterval,
  retryDueReports,
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
}

export class RunnerHost {
  private readonly connection: ServerConnection
  private readonly control: RunnerControlWebSocketClient
  private readonly workspace: WorkspaceManager
  private readonly workspaceRegistry: WorkspaceRegistry
  private readonly namedWorkspaceRegistry: NamedWorkspaceRegistry
  private readonly namedWorkspaceManager: NamedWorkspaceManager
  private readonly namedWorkspaceReclaimProbe: NamedWorkspaceReclaimProbe
  private readonly agentSessionRuntimeEventOutbox: AgentSessionRuntimeEventOutbox
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
  private readonly fetchPendingUpdateOperation: (signal: AbortSignal) => Promise<PendingUpdateOperation | null>
  private readonly shutdownHandoffBudgetMs: number
  private readonly shutdownStopBudgetMs: number
  private readonly terminalTaskLogDelivery: TerminalTaskLogDeliveryStore
  private readonly hostShutdown: ReturnType<typeof createHostShutdown>
  private readonly waitForConnectionRetry: (delayMs: number, signal: AbortSignal) => Promise<void>
  private readonly skillResolver = new SkillResolver()
  private readonly processGeneration = randomUUID()

  // Lets an out-of-loop reconnect callback bound its immediate heartbeat.
  private activeSignal: AbortSignal | null = null

  // WorkExecutor is created once per host; per-work recreation leaves shared lifecycle state cold.
  private workExecutor: WorkExecutor | null = null

  // These process-lifetime maps survive reconnects and form the full poll report.
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
    this.workspaceRegistry = new WorkspaceRegistry(options.runnerRoot, {
      runnerId: options.runnerId,
    })
    this.namedWorkspaceRegistry = new NamedWorkspaceRegistry(options.runnerRoot)
    this.agentSessionRuntimeEventOutbox = createAgentSessionRuntimeEventOutbox({
      filePath: `${options.runnerRoot}/${RUNTIME_EVENT_OUTBOX_FILE}`,
      deliver: createServerRuntimeEventDelivery({
        connection: this.connection,
      }),
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
    this.terminalTaskLogDelivery =
      dependencies.terminalTaskLogDelivery ?? new TerminalTaskLogDeliveryStoreImpl(options.runnerRoot)
    this.waitForConnectionRetry = dependencies.waitForConnectionRetry ?? hostDelay
    this.shutdownHandoffBudgetMs = positiveBudget(dependencies.shutdownHandoffBudgetMs, SHUTDOWN_HANDOFF_BUDGET_MS)
    this.shutdownStopBudgetMs = positiveBudget(dependencies.shutdownStopBudgetMs, 2_000)
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
          workspaceGit: {
            resolveQuery: resolveWorkspaceQuery,
            runnerRoot: options.runnerRoot,
          },
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
              {
                openCode: () => this.openCodeRuntime,
                pi: () => this.piRuntime,
              },
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
      openCodeRuntime: () => this.openCodeRuntime,
    })
    this.hostShutdown = createHostShutdown({
      options: this.options,
      connection: this.connection,
      inFlight: this.inFlight,
      fetchPendingUpdateOperation: this.fetchPendingUpdateOperation,
      shutdownHandoffBudgetMs: this.shutdownHandoffBudgetMs,
      shutdownStopBudgetMs: this.shutdownStopBudgetMs,
    })
  }

  private get executionContext(): HostExecutionContext {
    return {
      options: this.options,
      connection: this.connection,
      taskLogDeps: () => createHostTaskLogDeps(this.connection, this.terminalTaskLogDelivery, this.options),
      workExecutorRef: () => this.workExecutor,
      terminalTaskLogDelivery: this.terminalTaskLogDelivery,
      terminalTaskLogDeliveryInFlight: this.terminalTaskLogDeliveryInFlight,
      syncOpenCodeWorkOwners: () => this.syncOpenCodeWorkOwners(),
      inFlight: this.inFlight,
      awaitingAck: this.awaitingAck,
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
        log.error('failed to load workspace registry; starting empty', {
          exception: error,
        })
      }
      // Named workspace registry: same rebuildable-index rules as the
      // workflow registry — a missing or corrupt file starts empty.
      try {
        await this.namedWorkspaceRegistry.load()
      } catch (error) {
        log.error('failed to load named workspace registry; starting empty', {
          exception: error,
        })
      }
      try {
        await this.terminalTaskLogDelivery.load()
      } catch (error) {
        log.error('failed to load terminal task-log delivery store', {
          exception: error,
        })
      }
      if (!this.terminalTaskLogDelivery.ready()) {
        log.warn('terminal task-log delivery store unavailable; runner admission gated')
      }
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
      const heartbeat = setInterval(() => void this.heartbeatOnce(signal), this.options.heartbeatIntervalMs)
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
    if (signal) {
      void this.cleanup.runConvergenceOnce(signal)
      void this.cleanup.runCleanupOnce(signal)
    }
  }

  private async heartbeatOnce(signal: AbortSignal): Promise<void> {
    try {
      await this.connection.heartbeat(this.registrationState(), signal)
      await this.observeManagerDeploymentEpoch()
    } catch (error) {
      log.error('runner heartbeat failed', { exception: error })
    }
  }

  private async sendImmediateHeartbeat() {
    const signal = this.activeSignal
    if (!signal || signal.aborted) return
    try {
      await this.connection.heartbeat(this.registrationState(), signal)
      await this.observeManagerDeploymentEpoch()
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
      log.error('provider error policy invalid', {
        reason: this.providerPolicyDiagnostic,
      })
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
        process.cwd(),
        this.skillResolver,
        this.namedWorkspaceManager,
        {
          strictExecutionSourceValidation: this.options.strictExecutionSourceValidation === true,
        },
      ),
      this.agentSessionRuntimeEventOutbox,
      undefined,
      this.piRuntime,
      this.skillResolver,
      this.namedWorkspaceManager,
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
      log.error('agent-session runtime event outbox failed to load', {
        exception: error,
        session: 'outbox',
      })
    }
    if (signal.aborted) return
    if (!outbox.ready()) {
      log.warn('agent-session runtime event outbox unhealthy at startup; runner admission gated until it recovers', {
        session: 'outbox',
      })
    }
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
          log.warn('runner not ready; skipping poll', {
            reason: this.providerPolicyDiagnostic,
          })
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
        log.warn('runner poll failed; retrying', {
          reason: `in ${this.options.pollIntervalMs}ms`,
          exception: error,
        })
        await raceInterval(nextReconciliationInterval(this.executionContext), signal, [])
        continue
      }

      await this.prepareOpenCodeWork(
        works.map((item) => item.work),
        signal,
      )

      // A single poll may return multiple dispatches (repair + new claims).
      // Execute each concurrently, skipping re-deliveries the process
      // already holds.
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
          controller,
        }
        entry.done = executeAndTransition(this.executionContext, work, controller.signal, key, entry)
        this.inFlight.set(key, entry)

        this.syncOpenCodeWorkOwners()
      }

      if (signal.aborted) break
      // Pace the next round. With nothing in flight, sleep one interval
      // before re-polling; with in-flight work, race the interval against
      // any work settling so a freed slot re-polls promptly. A failed report
      // also bounds the wait: report retries must not inherit a long poll
      // interval.
      await raceInterval(
        nextReconciliationInterval(this.executionContext),
        signal,
        [...this.inFlight.values()].map((entry) => entry.done),
      )
    }

    await this.hostShutdown.shutdownInFlight()
    await withTimeout(Promise.allSettled([...this.inFlight.values()].map((e) => e.done)), this.shutdownStopBudgetMs)
  }

  private syncOpenCodeWorkOwners(): void {
    syncOpenCodeWorkOwnersForRuntime(this.openCodeRuntime, this.inFlight.values(), this.awaitingAck.values())
  }

  private async prepareOpenCodeWork(works: readonly DispatchWorkItem[], signal: AbortSignal): Promise<void> {
    const runtime = this.openCodeRuntime
    const owners = works.filter((work) => usesOpenCode(work) && !isManagerExecutionWork(work)).map(workKey)
    if (!runtime || owners.length === 0) return
    runtime.setWorkOwners([...openCodeOwnersForRuntime(this.inFlight.values(), this.awaitingAck.values()), ...owners])
    if (!runtime.ready()) {
      const started = await runtime.start(signal)
      if (!started.ok)
        log.error('opencode runtime could not be recreated for work', {
          reason: started.error.message,
        })
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
      await this.observeManagerDeploymentEpoch()
      return works
    } finally {
      bounded.dispose()
    }
  }

  private async observeManagerDeploymentEpoch(): Promise<void> {
    this.observedManagerDeploymentEpoch = await observeManagerDeploymentEpoch(
      this.observedManagerDeploymentEpoch,
      this.connection.deploymentEpoch,
      () => this.invalidateManagerExecutions(),
    )
  }

  private async revokeManagerExecution(executionId: string): Promise<void> {
    try {
      await revokeManagerExecution(this.connection, executionId, new AbortController().signal)
    } catch (error) {
      log.warn('Manager execution revocation could not be delivered', {
        executionId,
        exception: error,
      })
    }
  }

  private async invalidateManagerExecutions(): Promise<void> {
    const boundaries = [...this.managerExecutions.values()]
    this.managerExecutions.clear()
    await invalidateManagerExecutions(this.inFlight.values(), boundaries, this.managerExecutionRegistry)
  }

  private pollReport(): ReturnType<typeof buildRunnerPollReport> {
    return buildRunnerPollReport({
      processGeneration: this.processGeneration,
      inFlight: this.inFlight.keys(),
      awaitingAck: this.awaitingAck.keys(),
      runtimeReadiness: runtimeReadinessWitnesses(this.openCodeRuntime, this.piRuntime, this.piRuntimeGeneration),
      connectionId: this.control.getConnectionId(),
      admissionReady:
        this.providerPolicyDiagnostic === null &&
        this.terminalTaskLogDelivery.ready() &&
        this.agentSessionRuntimeEventOutbox.ready(),
      deploymentEpoch: this.connection.deploymentEpoch,
    })
  }

  private isOpenCodeReadyForClaim(): boolean {
    return isOpenCodeReadyForClaimForRuntime(this.openCodeRuntime, this.agentSessionRuntimeEventOutbox)
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
      buildRegistrationState(
        this.options,
        this.piRuntime,
        this.actions.catalog(),
        () => this.control.getConnectionId(),
        this.processGeneration,
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
