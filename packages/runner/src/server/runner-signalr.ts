// Issue-461 T-001 / design D1 + D7: the runner SignalR client owns
// connection-lifecycle hooks (start, stop, reconnect) and exposes the
// host-owned runtime accessor + outbox to the follow-up and cancel
// handlers. The client does NOT resolve the runtime during
// registration; handlers resolve the runtime per command so a runtime
// initialized or replaced after client construction is visible to
// later commands.

import * as signalR from "@microsoft/signalr"
import { WorkspaceManager } from "../runtime/workspace.js"
import type { WorkspaceRegistry } from "../runtime/workspace-registry.js"
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
import { registerWorkflowRunStatusHandler } from "./workflow-run-status-handler.js"
import type { OpenCodeRuntime } from "../runtime/opencode/index.js"
import type { AgentSessionRuntimeEventOutbox } from "./runtime-event-outbox.js"

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
  followupTargetResolver?: FollowupTargetResolver | null
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  registry?: WorkspaceRegistry | null
  /**
   * Late-binding runtime accessor used by the Follow-up / Cancel
   * handlers (issue-461 T-001 / design D1). The host wires this so
   * the handler always consults the current runtime handle (which
   * is rebuilt on Server exit). Tests can pass either a static fake
   * or a getter to drive the timing used by acceptance criteria
   * such as "Runtime becomes ready after handler registration".
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
  private readonly followupTargetResolver: FollowupTargetResolver | null
  private readonly agentSessionRuntimeEventOutbox: AgentSessionRuntimeEventOutbox | null
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
    this.followupTargetResolver = options.followupTargetResolver ?? null
    this.agentSessionRuntimeEventOutbox = options.agentSessionRuntimeEventOutbox ?? null
    this.openCodeRuntime = options.openCodeRuntime ?? null
    this.allowUnverifiedWorkspaceQueriesForTest = options.allowUnverifiedWorkspaceQueriesForTest === true

    this.registerHandlers()
    this.registerLifecycleCallbacks()
  }

  async start(): Promise<void> {
    if (this.agentSessionRuntimeEventOutbox) {
      await this.agentSessionRuntimeEventOutbox.load()
      void this.agentSessionRuntimeEventOutbox.kick().catch(() => undefined)
    }
    await this.connection.start()
  }

  async stop(): Promise<void> {
    if (this.agentSessionRuntimeEventOutbox) {
      await this.agentSessionRuntimeEventOutbox.stop()
    }
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
      if (this.agentSessionRuntimeEventOutbox) {
        void this.agentSessionRuntimeEventOutbox.kick().catch(() => undefined)
      }
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
      followupTargetResolver: this.followupTargetResolver,
      agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
      openCodeRuntime: this.openCodeRuntime,
    })

    registerCancelHandler(this.connection, {
      followupTargetResolver: this.followupTargetResolver,
      openCodeRuntime: this.openCodeRuntime,
    })

    registerWorkflowRunStatusHandler(this.connection, {
      registry: this.registry,
    })
  }
}
