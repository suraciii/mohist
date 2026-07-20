import * as signalR from "@microsoft/signalr"
import { WorkspaceManager } from "../runtime/workspace.js"
import type { WorkspaceRegistry } from "../runtime/workspace-registry.js"
import type { ServerConnection } from "./connection.js"
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
import type { FollowupFailureOutboxStore } from "./followup-failure-outbox.js"
import { registerCancelHandler } from "./cancel-handler.js"
import { registerWorkflowRunStatusHandler } from "./workflow-run-status-handler.js"
import type { OpenCodeRuntime } from "../runtime/opencode/index.js"

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
  WorkspaceQuery,
}

export interface RunnerSignalRClientOptions {
  probeTimeoutMs?: number
  onReconnected?: (connectionId: string) => void
  serverConnection?: ServerConnection | null
  followupTargetResolver?: FollowupTargetResolver | null
  followupFailureOutbox?: FollowupFailureOutboxStore | null
  registry?: WorkspaceRegistry | null
  /**
   * Late-binding runtime accessor used by the Follow-up / Cancel
   * handlers (issue-410 T-003 / design D3). The host wires this so
   * the handler always consults the current runtime handle (which
   * is rebuilt on Server exit). Tests pass a static fake instead of
   * a getter.
   */
  openCodeRuntime?: OpenCodeRuntime | (() => OpenCodeRuntime | null) | null
  allowUnverifiedWorkspaceQueriesForTest?: boolean
}

export class RunnerSignalRClient {
  private connection: signalR.HubConnection
  private readonly workspaceManager: WorkspaceManager
  private readonly registry: WorkspaceRegistry | null
  private readonly probeTimeoutMs: number
  private readonly onReconnected: ((connectionId: string) => void) | undefined
  private readonly serverConnection: ServerConnection | null
  private readonly followupTargetResolver: FollowupTargetResolver | null
  private readonly followupFailureOutbox: FollowupFailureOutboxStore | null
  private readonly openCodeRuntime: OpenCodeRuntime | (() => OpenCodeRuntime | null) | null
  private readonly allowUnverifiedWorkspaceQueriesForTest: boolean

  constructor(
    serverUrl: string,
    runnerId: string,
    private readonly runnerRoot: string,
    buildGitHash: string | null = null,
    options: RunnerSignalRClientOptions = {},
  ) {
    const baseUrl = serverUrl.replace(/\/$/, "")
    const params = new URLSearchParams()
    params.set("runnerId", runnerId)
    if (buildGitHash) params.set("buildGitHash", buildGitHash)
    this.probeTimeoutMs = options.probeTimeoutMs ?? 5_000
    this.onReconnected = options.onReconnected
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/runner?${params.toString()}`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build()
    this.registry = options.registry ?? null
    this.workspaceManager = new WorkspaceManager(runnerRoot, this.registry)
    this.serverConnection = options.serverConnection ?? null
    this.followupTargetResolver = options.followupTargetResolver ?? null
    this.followupFailureOutbox = options.followupFailureOutbox ?? null
    this.openCodeRuntime = options.openCodeRuntime ?? null
    this.allowUnverifiedWorkspaceQueriesForTest = options.allowUnverifiedWorkspaceQueriesForTest === true

    this.registerHandlers()
    this.registerLifecycleCallbacks()
  }

  async start(): Promise<void> {
    if (this.followupFailureOutbox) await this.followupFailureOutbox.load()
    await this.connection.start()
    if (this.followupFailureOutbox && this.serverConnection)
      await this.followupFailureOutbox.drain(this.serverConnection)
  }

  async stop(): Promise<void> {
    await this.connection.stop()
  }

  getConnectionId(): string | null {
    return this.connection.connectionId
  }

  async probeLiveness(signal: AbortSignal): Promise<boolean> {
    return probeLiveness(this.connection, this.probeTimeoutMs, signal)
  }

  async forceReconnect(signal: AbortSignal): Promise<void> {
    return forceReconnect(this.connection, this.onReconnected, signal)
  }

  private registerLifecycleCallbacks(): void {
    this.connection.onreconnected((connectionId) => {
      notifyReconnected(this.connection, this.onReconnected, connectionId)
      if (this.followupFailureOutbox && this.serverConnection)
        void this.followupFailureOutbox.drain(this.serverConnection).catch(() => {})
    })
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
    })

    registerFollowupHandler(this.connection, {
      serverConnection: this.serverConnection,
      followupTargetResolver: this.followupTargetResolver,
      followupFailureOutbox: this.followupFailureOutbox,
      openCodeRuntime: this.resolveOpenCodeRuntime(),
    })

    registerCancelHandler(this.connection, {
      followupTargetResolver: this.followupTargetResolver,
      openCodeRuntime: this.resolveOpenCodeRuntime(),
    })

    registerWorkflowRunStatusHandler(this.connection, {
      registry: this.registry,
    })
  }

  private resolveOpenCodeRuntime(): OpenCodeRuntime | null {
    if (this.openCodeRuntime === null) return null
    if (typeof this.openCodeRuntime === "function") return this.openCodeRuntime()
    return this.openCodeRuntime
  }
}
