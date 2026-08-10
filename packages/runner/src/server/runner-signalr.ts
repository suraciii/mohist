// The runner SignalR client owns connection-lifecycle hooks (start,
// stop, reconnect) and exposes the host-owned runtime accessors +
// outbox to the follow-up, cancel, and session-command handlers. The
// client does NOT resolve the runtime during registration; handlers
// resolve the runtime per command so a runtime initialized or replaced
// after client construction is visible to later commands.

import * as signalR from "@microsoft/signalr"
import { WorkspaceManager } from "../runtime/workspace.js"
import type { WorkspaceRegistry } from "../runtime/workspace-registry.js"
import type { WorkspaceRemovalFence } from "../runtime/workspace-removal-fence.js"
import {
  isUnderRunnerRoot,
  resolveWorkspaceQuery,
  type WorkspaceQuery,
} from "../runtime/workspace-query.js"
import {
  type FollowupTarget,
  type FollowupTargetResolver,
  type ReceiveFollowupPayload,
  type CancelAgentSessionPayload,
  type CancelAgentSessionReply,
  type ReceiveWorkflowRunStatusPayload,
  resolveSessionTarget,
} from "./session-target.js"
import { forceReconnect, notifyReconnected, probeLiveness } from "./liveness-probe.js"
import {
  registerWorkspaceGitHandlers,
  setRunnerSignalRExistsCheckerForTest,
  setRunnerSignalRGitRunnerForTest,
} from "./workspace-git-handlers.js"
import { registerWorkspaceRemovalHandler } from "./workspace-removal-handler.js"
import { registerFollowupHandler } from "./followup-handler.js"
import { registerCancelHandler } from "./cancel-handler.js"
import {
  registerSessionCommandHandler,
  type SessionCommandHandler,
  type SessionCommandRequest,
  type SessionCommandResult,
} from "./session-command-handler.js"
import { registerWorkflowRunStatusHandler } from "./workflow-run-status-handler.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "./runtime-event-outbox.js"
import type { SessionCommandJournalStore } from "../runtime/session-command-journal.js"
import type { FollowupOperationJournalStore } from "../runtime/followup-operation-journal.js"
import type { CancelOperationJournalStore } from "../runtime/cancel-operation-journal.js"
import type { BindingRecoveryCoordinator } from "../runtime/binding-recovery.js"
import type { ServerConnection } from "./connection.js"
import type { BuildInfo } from "../runtime/build-info.js"
import type { PiTurnObserver } from "../runtime/pi/index.js"
import {
  callSessionCommand,
  resolveAccessor,
  resolveCommandRuntime,
  type CommandRuntimeAccessors,
} from "./command-runtime.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("connection")

export {
  isUnderRunnerRoot,
  resolveWorkspaceQuery,
  resolveSessionTarget,
  setRunnerSignalRExistsCheckerForTest,
  setRunnerSignalRGitRunnerForTest,
}
export type {
  CancelAgentSessionPayload,
  CancelAgentSessionReply,
  FollowupTarget,
  FollowupTargetResolver,
  ReceiveFollowupPayload,
  ReceiveWorkflowRunStatusPayload,
  SessionCommandHandler,
  SessionCommandRequest,
  SessionCommandResult,
  WorkspaceQuery,
}
export interface RunnerSignalRClientOptions {
  probeTimeoutMs?: number
  onReconnected?: (connectionId: string) => void
  /**
   * The runner's machine credential; presented as
   * <c>Authorization: Bearer</c> on the hub connection. Absent for
   * anonymous (pre-registration) connections.
   */
  credential?: string | null
  followupTargetResolver?: FollowupTargetResolver | null
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  registry?: WorkspaceRegistry | null

  /**
   * Late-binding runtime accessor used by the Follow-up / Cancel
   * handlers. The host wires this so
   * the handler always consults the current runtime handle (which
   * is rebuilt on Server exit). Tests can pass either a static fake
   * or a getter to drive the timing used by acceptance criteria
   * such as "Runtime becomes ready after handler registration".
   */
  openCodeRuntime?: CommandRuntimeAccessors["openCode"]
  /**
   * Late-binding Pi runtime accessor.
   * The host wires this next to `openCodeRuntime`; the dispatch
   * selector reads the binding's `runtime` field per command.
   */
  piRuntime?: CommandRuntimeAccessors["pi"]
  serverConnection?: ServerConnection | null
  /**
   * Optional override for the runner's `SessionCommand` journal.
   * Production wires the file-backed journal owned by the host;
   * tests inject an in-memory `SessionCommandJournalStore`.
   */
  sessionCommandJournal?: SessionCommandJournalStore | null
  followupOperationJournal?: FollowupOperationJournalStore | null
  cancelOperationJournal?: CancelOperationJournalStore | null
  bindingRecoveryCoordinator?: BindingRecoveryCoordinator | null
  skillResolver?: import("../runtime/skill-resolver.js").SkillResolver
  allowUnverifiedWorkspaceQueriesForTest?: boolean
}

export class RunnerSignalRClient {
  private connection: signalR.HubConnection
  private readonly workspaceManager: WorkspaceManager
  private readonly registry: WorkspaceRegistry | null

  private readonly probeTimeoutMs: number
  private readonly onReconnected: ((connectionId: string) => void) | undefined
  private readonly followupTargetResolver: FollowupTargetResolver | null
  private readonly agentSessionRuntimeEventOutbox: AgentSessionRuntimeEventOutbox | null
  private readonly openCodeRuntime: CommandRuntimeAccessors["openCode"]
  private readonly piRuntime: CommandRuntimeAccessors["pi"]
  private readonly serverConnection: ServerConnection | null
  private readonly sessionCommandJournal: SessionCommandJournalStore | null
  private readonly followupOperationJournal: FollowupOperationJournalStore | null
  private readonly cancelOperationJournal: CancelOperationJournalStore | null
  private readonly bindingRecoveryCoordinator: BindingRecoveryCoordinator | null
  private readonly skillResolver: RunnerSignalRClientOptions["skillResolver"]
  private readonly allowUnverifiedWorkspaceQueriesForTest: boolean

  constructor(
    serverUrl: string,
    runnerId: string,
    private readonly runnerRoot: string,
    buildGitHash: string | null = null,
    options: RunnerSignalRClientOptions = {},
    buildInfo: BuildInfo | null = null,
  ) {
    const baseUrl = serverUrl.replace(/\/$/, "")
    const params = new URLSearchParams()
    params.set("runnerId", runnerId)
    if (buildGitHash) params.set("buildGitHash", buildGitHash)
    if (buildInfo?.component) params.set("component", buildInfo.component)
    if (buildInfo?.version) params.set("version", buildInfo.version)
    if (buildInfo?.sourceRevision ?? buildInfo?.gitHash) params.set("sourceRevision", buildInfo.sourceRevision ?? buildInfo.gitHash!)
    if (buildInfo?.treeHash) params.set("treeHash", buildInfo.treeHash)
    if (buildInfo?.artifactDigest) params.set("artifactDigest", buildInfo.artifactDigest)
    if (buildInfo?.releaseId) params.set("releaseId", buildInfo.releaseId)
    if (buildInfo?.generation) params.set("generation", String(buildInfo.generation))
    this.probeTimeoutMs = options.probeTimeoutMs ?? 5_000
    this.onReconnected = options.onReconnected
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/runner?${params.toString()}`, {
        // SignalR omits the Authorization header when the factory returns
        // a falsy value, so pre-registration connections stay headerless.
        accessTokenFactory: () => options.credential ?? "",
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build()
    this.registry = options.registry ?? null
    this.workspaceManager = new WorkspaceManager(runnerRoot, this.registry)
    this.followupTargetResolver = options.followupTargetResolver ?? null
    this.agentSessionRuntimeEventOutbox = options.agentSessionRuntimeEventOutbox ?? null
    this.openCodeRuntime = options.openCodeRuntime ?? null
    this.piRuntime = options.piRuntime ?? null
    this.serverConnection = options.serverConnection ?? null
    this.sessionCommandJournal = options.sessionCommandJournal ?? null
    this.followupOperationJournal = options.followupOperationJournal ?? null
    this.cancelOperationJournal = options.cancelOperationJournal ?? null
    this.bindingRecoveryCoordinator = options.bindingRecoveryCoordinator ?? null
    this.skillResolver = options.skillResolver
    this.allowUnverifiedWorkspaceQueriesForTest = options.allowUnverifiedWorkspaceQueriesForTest === true

    this.registerHandlers()
    this.registerLifecycleCallbacks()
  }

  async start(): Promise<void> {
    if (this.agentSessionRuntimeEventOutbox) {
      await this.recoverRuntimeEventOutbox()
    }
    if (this.sessionCommandJournal) {
      try {
        await this.sessionCommandJournal.load()
      } catch (error) {
        log.error("session command journal failed to load", { session: "command", exception: error })
      }
    }
    if (this.followupOperationJournal) {
      try {
        await this.followupOperationJournal.load()
      } catch (error) {
        log.error("followup operation journal failed to load", { session: "followup", exception: error })
      }
    }
    if (this.cancelOperationJournal) {
      try {
        await this.cancelOperationJournal.load()
      } catch (error) {
        log.error("cancel operation journal failed to load", { session: "cancel", exception: error })
      }
    }
    await this.connection.start()
  }

  async stop(): Promise<void> {
    if (this.agentSessionRuntimeEventOutbox) {
      await this.agentSessionRuntimeEventOutbox.stop()
    }
    await this.connection.stop()
  }

  async disconnect(): Promise<void> {
    await this.connection.stop()
  }

  getConnectionId(): string | null {
    return this.connection.connectionId
  }

  async probeLiveness(signal: AbortSignal): Promise<boolean> {
    return probeLiveness(this.connection, this.probeTimeoutMs, signal)
  }

  async forceReconnect(signal: AbortSignal): Promise<void> {
    await forceReconnect(this.connection, this.onReconnected, signal)
    await this.recoverRuntimeEventOutbox()
  }

  private registerLifecycleCallbacks(): void {
    this.connection.onreconnected((connectionId) => {
      notifyReconnected(this.connection, this.onReconnected, connectionId)
      void this.recoverRuntimeEventOutbox().catch(() => undefined)
    })
  }

  private async recoverRuntimeEventOutbox(): Promise<void> {
    if (!this.agentSessionRuntimeEventOutbox) return
    await this.agentSessionRuntimeEventOutbox.recover()
  }

  private registerHandlers(): void {
    registerWorkspaceGitHandlers(this.connection, {
      resolveQuery: resolveWorkspaceQuery,
      runnerRoot: this.runnerRoot,
      allowUnverifiedWorkspaceQueriesForTest: this.allowUnverifiedWorkspaceQueriesForTest,
    })

    registerWorkspaceRemovalHandler(this.connection, {
      runnerRoot: this.runnerRoot,
      registry: this.registry,
      removalFence: () => resolveAccessor(this.openCodeRuntime) as WorkspaceRemovalFence | null,
    })

    registerFollowupHandler(this.connection, {
      followupTargetResolver: this.followupTargetResolver,
      agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
      openCodeRuntime: this.openCodeRuntime,
      piRuntime: this.piRuntime,
      connection: this.serverConnection,
      runnerId: this.serverConnection?.runnerId ?? null,
      followupOperationJournal: this.followupOperationJournal,
      bindingRecoveryCoordinator: this.bindingRecoveryCoordinator,
      skillResolver: this.skillResolver,
    })

    registerCancelHandler(this.connection, {
      followupTargetResolver: this.followupTargetResolver,
      openCodeRuntime: this.openCodeRuntime,
      piRuntime: this.piRuntime,
      agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
      cancelOperationJournal: this.cancelOperationJournal,
    })

    registerSessionCommandHandler(this.connection, {
      handler: this.routeSessionCommand,
      journal: this.sessionCommandJournal,
    })

    registerWorkflowRunStatusHandler(this.connection, {
      registry: this.registry,
    })
  }

  private readonly routeSessionCommand: SessionCommandHandler = async (request) => {
    const handle = resolveCommandRuntime(
      { runtime: request.runtime },
      { openCode: this.openCodeRuntime, pi: this.piRuntime },
    )
    if (!handle) {
      return { ok: false, error: "unavailable" }
    }
    const runtimeSessionId = request.runtimeSessionId
    const workDir = request.workDir
    if (!workDir || (request.command === "compact" && !runtimeSessionId)) {
      return { ok: false, error: "unavailable" }
    }
    const observer = this.buildSessionCommandObserver(request)
    if (handle.kind === "pi" && request.command === "compact" && !observer) {
      return { ok: false, error: "unavailable" }
    }
    return await callSessionCommand(handle, request.command, {
      runtimeSessionId: runtimeSessionId ?? "",
      workDir,
    }, observer)
  }

  private buildSessionCommandObserver(request: SessionCommandRequest): PiTurnObserver | null {
    const outbox = this.agentSessionRuntimeEventOutbox
    if (!outbox || !outbox.ready() || !request.projectId) return null
    return {
      onEvent: async (event) => {
        const record: RuntimeEventRecord = {
          id: `session-command-event:${request.operationId}:${event.id}`,
          producerFamily: "generic-followup",
          target: { kind: "generic", projectId: request.projectId!, sessionId: request.sessionId },
          runtimeSessionId: request.runtimeSessionId!,
          work: null,
          event: {
            type: event.type,
            payload: { ...event.payload, source: "session-command", command: request.command, operationId: request.operationId, runtimeSessionId: request.runtimeSessionId },
          },
          acknowledgementPolicy: "successful-response",
        }
        await outbox.enqueueProducedFact(record)
      },
    }
  }
}
