// Issue-461 T-001 / design D1 + D7 + issue-451 T-004 / design D2-D4:
// the runner SignalR client owns connection-lifecycle hooks (start,
// stop, reconnect) and exposes the host-owned runtime accessors +
// outbox to the follow-up, cancel, and session-command handlers. The
// client does NOT resolve the runtime during registration; handlers
// resolve the runtime per command so a runtime initialized or replaced
// after client construction is visible to later commands.

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
import {
  registerSessionCommandHandler,
  type SessionCommandHandler,
  type SessionCommandRequest,
  type SessionCommandResult,
} from "./session-command-handler.js"
import { registerWorkflowRunStatusHandler } from "./workflow-run-status-handler.js"
import type { AgentSessionRuntimeEventOutbox, RuntimeEventRecord } from "./runtime-event-outbox.js"
import type { SessionCommandJournalStore } from "../runtime/session-command-journal.js"
import type { PiTurnObserver } from "../runtime/pi/index.js"
import {
  callSessionCommand,
  resolveCommandRuntime,
  type CommandRuntimeAccessors,
} from "./command-runtime.js"

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
  openCodeRuntime?: CommandRuntimeAccessors["openCode"]
  /**
   * Late-binding Pi runtime accessor (issue-451 T-004 / design D2).
   * The host wires this next to `openCodeRuntime`; the dispatch
   * selector reads the binding's `runtime` field per command.
   */
  piRuntime?: CommandRuntimeAccessors["pi"]
  /**
   * Optional override for the runner's `SessionCommand` journal.
   * Production wires the file-backed journal owned by the host;
   * tests inject an in-memory `SessionCommandJournalStore`.
   */
  sessionCommandJournal?: SessionCommandJournalStore | null
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
  private readonly sessionCommandJournal: SessionCommandJournalStore | null
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
    this.piRuntime = options.piRuntime ?? null
    this.sessionCommandJournal = options.sessionCommandJournal ?? null
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
        console.error("session command journal failed to load:", error)
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
    })

    registerFollowupHandler(this.connection, {
      followupTargetResolver: this.followupTargetResolver,
      agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
      openCodeRuntime: this.openCodeRuntime,
      piRuntime: this.piRuntime,
    })

    registerCancelHandler(this.connection, {
      followupTargetResolver: this.followupTargetResolver,
      openCodeRuntime: this.openCodeRuntime,
      piRuntime: this.piRuntime,
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
    if (!runtimeSessionId || !workDir) {
      return { ok: false, error: "unavailable" }
    }
    const observer = this.buildSessionCommandObserver(request)
    if (handle.kind === "pi" && request.command === "compact" && !observer) {
      return { ok: false, error: "unavailable" }
    }
    return await callSessionCommand(handle, request.command, {
      runtimeSessionId,
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
