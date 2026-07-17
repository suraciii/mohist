import { beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"
import type { SessionCommandRequest } from "../src/server/session-command-handler.js"
import type { FollowupTargetResolution } from "../src/server/session-target.js"
import { deferred } from "./support/deferred.js"

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000

const mocks = vi.hoisted(() => ({
  connect: vi.fn(),
  heartbeat: vi.fn(),
  disconnect: vi.fn(),
  poll: vi.fn(),
  report: vi.fn(),
  uploadTaskLog: vi.fn(),
  fetchConfig: vi.fn(async () => null),
  workflowAgentSessionRuntimeEvents: vi.fn(),
  agentSessionRuntimeEvents: vi.fn(),
  startSignalR: vi.fn(),
  stopSignalR: vi.fn(),
  getConnectionId: vi.fn(() => "conn-1"),
  probeLiveness: vi.fn(async () => true),
  blockingAction: vi.fn(),
  forceReconnect: vi.fn(async () => undefined),
  createSharedAcpConnection: vi.fn(),
  shutdownSharedAcpConnection: vi.fn(),
  setSessionHandlers: vi.fn(),
  clearSessionHandlers: vi.fn(),
  acpShutdown: vi.fn(),
}))

const {
  connect,
  heartbeat,
  disconnect,
  poll,
  report,
  uploadTaskLog,
  fetchConfig,
  workflowAgentSessionRuntimeEvents,
  agentSessionRuntimeEvents,
  startSignalR,
  stopSignalR,
  getConnectionId,
  probeLiveness,
  blockingAction,
  forceReconnect,
  createSharedAcpConnection,
  shutdownSharedAcpConnection,
  setSessionHandlers,
  clearSessionHandlers,
  acpShutdown,
} = mocks

let capturedOnReconnected: ((connectionId: string) => void) | null = null
let capturedFollowupTargetResolver: ((target: SessionTarget) => FollowupTargetResolution | Promise<FollowupTargetResolution>) | null = null
let capturedSessionCommandHandler: ((request: SessionCommandRequest) => unknown) | null = null

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
    report = report
    uploadTaskLog = uploadTaskLog
    fetchConfig = fetchConfig
    workflowAgentSessionRuntimeEvents = workflowAgentSessionRuntimeEvents
    agentSessionRuntimeEvents = agentSessionRuntimeEvents
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = startSignalR
    stop = stopSignalR
    getConnectionId = getConnectionId
    probeLiveness = probeLiveness
    forceReconnect = forceReconnect
    constructor(_serverUrl: string, _runnerId: string, _runnerRoot: string, _buildGitHash: string | null, options: { onReconnected?: (id: string) => void; followupTargetResolver?: typeof capturedFollowupTargetResolver; sessionCommandHandler?: typeof capturedSessionCommandHandler } = {}) {
      capturedOnReconnected = options.onReconnected ?? null
      capturedFollowupTargetResolver = options.followupTargetResolver ?? null
      capturedSessionCommandHandler = options.sessionCommandHandler ?? null
    }
  },
}))

vi.mock("../src/runtime/opencode-models.js", () => ({
  discoverOpencodeModels: vi.fn(async () => ({ models: ["openai/gpt-5.5"], variants: {} })),
}))

vi.mock("../src/actions/registry.js", () => ({
  createDefaultRegistry: () => ({
    resolve: (uses?: string | null) => uses === "test/block" ? blockingAction : undefined,
  }),
}))

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    private sessions = new Map<string, { sessionId: string; workDir: string }>()
    key(target: SessionTarget) { return target.kind === "workflow" ? this.workflowKey(target.workflowRunId, target.sessionName) : this.genericKey(target.sessionId) }
    workflowKey(workflowRunId: string, sessionName: string) { return `workflow:${workflowRunId}:${sessionName}` }
    genericKey(sessionId: string) { return `generic:${sessionId}` }
    get(key: string) { return this.sessions.get(key) }
    set(key: string, entry: { sessionId: string; workDir: string }) { this.sessions.set(key, entry) }
    has(key: string) { return this.sessions.has(key) }
    delete(key: string) { this.sessions.delete(key) }
  },
  createSharedAcpConnection: (...args: unknown[]) => createSharedAcpConnection(...args),
}))

beforeEach(() => {
  vi.useFakeTimers()
  capturedOnReconnected = null
  capturedFollowupTargetResolver = null
  capturedSessionCommandHandler = null
  createSharedAcpConnection.mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers,
    clearSessionHandlers,
    shutdown: shutdownSharedAcpConnection,
  })
  acpShutdown.mockResolvedValue(undefined)
  shutdownSharedAcpConnection.mockResolvedValue(undefined)
  uploadTaskLog.mockResolvedValue({ accepted: 0, truncated: false })
  blockingAction.mockImplementation(async ({ signal }: { signal: AbortSignal }) => {
    const aborted = deferred<{ status: string; message: string }>()
    if (signal.aborted) {
      aborted.resolve({ status: "failed", message: "aborted" })
    } else {
      signal.addEventListener("abort", () => aborted.resolve({ status: "failed", message: "aborted" }), { once: true })
    }
    return aborted.promise
  })
})

describe("RunnerHost", () => {
  it.each(["OpenCode", "OPENCODE"])("recognizes %s as the configured runtime", (runtime) => {
    new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    expect(capturedSessionCommandHandler?.({
      sessionId: "session-1",
      runtime,
      runtimeSessionId: "runtime-1",
      runnerId: "runner-test",
      workDir: "/tmp/mohist-runner-test",
      command: "compact",
      operationId: "operation-1",
    })).toEqual({ ok: false, error: "unavailable" })
  })

  it("RunnerRegistration_DoesNotReportWorkflowSlots", async () => {
    vi.clearAllMocks()
    const connected = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await connected.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(connect).toHaveBeenCalledWith(
        expect.objectContaining({
          projectId: "project-1",
          coderModels: ["openai/gpt-5.5"],
        }),
        expect.any(AbortSignal),
      )
      expect(Object.keys(connect.mock.calls[0][0]).sort()).toEqual([
        "buildGitHash",
        "capabilities",
        "coderModelVariants",
        "coderModels",
        "connectionId",
        "projectId",
      ])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("WorkerPool_PollsUntilServerReturnsNoWorkWithoutLocalConcurrencyCap", async () => {
    vi.clearAllMocks()
    const pollCalls = [deferred<void>(), deferred<void>(), deferred<void>(), deferred<void>()]
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const work = (id: string) => ({
      workflowRunId: "",
      workId: `work-${id}`,
      workType: "task",
      uses: "test/block",
      ownerKind: "agent-job",
      agentJobId: `job-${id}`,
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    })
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollCalls[pollIndex]?.resolve()
      pollIndex += 1
      if (pollIndex === 4) {
        controller.abort()
        return []
      }
      return [work(String(pollIndex))]
    })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await pollCalls[0]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[1]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[2]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[3]!.promise
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("WorkerPool_PollFailure_RetriesWithoutRestartingRunner", async () => {
    vi.clearAllMocks()
    const firstPollStarted = deferred<void>()
    const retryPollStarted = deferred<void>()
    const failureLogged = deferred<void>()
    const pollFailure = new Error("server unavailable")
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    poll
      .mockImplementationOnce(async () => {
        firstPollStarted.resolve()
        throw pollFailure
      })
      .mockImplementationOnce(async () => {
        retryPollStarted.resolve()
        controller.abort()
        return []
      })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const warningSpy = vi.spyOn(console, "warn").mockImplementation((message: unknown) => {
      if (typeof message === "string" && message.includes("runner poll failed; retrying")) failureLogged.resolve()
    })
    const run = host.run(controller.signal)

    try {
      await firstPollStarted.promise
      await failureLogged.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await retryPollStarted.promise
      await expect(run).resolves.toBeUndefined()

      expect(connect).toHaveBeenCalledTimes(1)
      expect(startSignalR).toHaveBeenCalledTimes(1)
      expect(warningSpy).toHaveBeenCalledWith(expect.stringContaining("runner poll failed; retrying"), pollFailure)
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      warningSpy.mockRestore()
    }
  })

  it("WorkerPool_PollTimeout_AbortsAttemptAndContinuesPolling", async () => {
    vi.clearAllMocks()
    const firstPollStarted = deferred<void>()
    const retryPollStarted = deferred<void>()
    const pollAbort = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    poll
      .mockImplementationOnce((signal: AbortSignal) => new Promise((_, reject) => {
        firstPollStarted.resolve()
        signal.addEventListener("abort", () => {
          pollAbort.resolve()
          reject(signal.reason)
        }, { once: true })
      }))
      .mockImplementationOnce(async () => {
        retryPollStarted.resolve()
        controller.abort()
        return []
      })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    const run = host.run(controller.signal)

    try {
      await firstPollStarted.promise
      await vi.advanceTimersByTimeAsync(10_000 + POLL_INTERVAL_MS + 1)
      await pollAbort.promise
      await retryPollStarted.promise
      await expect(run).resolves.toBeUndefined()

      expect(warningSpy).toHaveBeenCalledWith(expect.stringContaining("runner poll failed; retrying"), expect.any(Error))
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      warningSpy.mockRestore()
    }
  })

  it("RunnerShutdown_UnregistersRunner", async () => {
    vi.clearAllMocks()
    const connected = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await connected.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))
      expect(stopSignalR).toHaveBeenCalled()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("RunnerConnection_WhenSignalRFails_DoesNotPollAndRetriesCleanly", async () => {
    vi.clearAllMocks()
    const firstSignalRStarted = deferred<void>()
    const secondSignalRStarted = deferred<void>()
    const secondSignalRRelease = deferred<void>()
    const disconnectedAfterFailure = deferred<void>()
    const firstPollStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockImplementation(async () => {
      disconnectedAfterFailure.resolve()
    })
    poll.mockImplementation(async () => {
      firstPollStarted.resolve()
      return []
    })
    const signalRUnavailable = new Error("signalr unavailable")
    startSignalR
      .mockImplementationOnce(async () => {
        firstSignalRStarted.resolve()
        throw signalRUnavailable
      })
      .mockImplementationOnce(async () => {
        secondSignalRStarted.resolve()
        await secondSignalRRelease.promise
      })
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const errorSpy = vi.spyOn(console, "error").mockClear().mockImplementation(() => undefined)
    const run = host.run(controller.signal)
    try {
      await firstSignalRStarted.promise
      await disconnectedAfterFailure.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await secondSignalRStarted.promise
      expect(poll).not.toHaveBeenCalled()
      expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))

      secondSignalRRelease.resolve()
      await firstPollStarted.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(errorSpy).toHaveBeenCalledTimes(1)
      expect(errorSpy).toHaveBeenNthCalledWith(
        1,
        `runner connection failed; retrying in ${POLL_INTERVAL_MS}ms`,
        signalRUnavailable,
      )
    } finally {
      secondSignalRRelease.resolve()
      controller.abort()
      await run.catch(() => undefined)
      errorSpy.mockRestore()
    }
  })

  it("GenericFollowupResolver_UsesPayloadProjectId_WhenRunnerProjectUnset", async () => {
    vi.clearAllMocks()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { sharedAcpConnection: unknown; sessionManager: { set(key: string, entry: unknown): void; genericKey(sessionId: string): string } }
    const connection = { prompt: vi.fn() }
    host.sharedAcpConnection = { connection }
    host.sessionManager.set(host.sessionManager.genericKey("gen-1"), { sessionId: "acp-1", workDir: "/tmp/work" })

    const resolved = capturedFollowupTargetResolver?.({ kind: "generic", projectId: "project-from-payload", sessionId: "gen-1" })

    expect(resolved).toEqual({ connection, sessionId: "acp-1", projectId: "project-from-payload" })
  })

  it("GenericFollowupResolver_StartupBeforeSharedAcpInitialization_IsTemporarilyUnavailable", () => {
    new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    })

    expect(resolved).toEqual({ unavailable: true })
  })

  it("GenericFollowupResolver_CacheMissResumesPersistedBindingOnce", async () => {
    vi.clearAllMocks()
    const resumeSession = vi.fn(async () => undefined)
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { sharedAcpConnection: unknown }
    const connection = { prompt: vi.fn(), cancel: vi.fn(), resumeSession }
    host.sharedAcpConnection = {
      connection,
      processPid: null,
      setSessionHandlers: vi.fn(),
      clearSessionHandlers: vi.fn(),
      shutdown: vi.fn(async () => undefined),
    }
    const target: SessionTarget = {
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    }

    const first = capturedFollowupTargetResolver?.(target)
    const second = capturedFollowupTargetResolver?.(target)

    await expect(Promise.all([first, second])).resolves.toEqual([
      { connection, sessionId: "runtime-1", projectId: "project-1" },
      { connection, sessionId: "runtime-1", projectId: "project-1" },
    ])
    expect(resumeSession).toHaveBeenCalledTimes(1)
    expect(resumeSession).toHaveBeenCalledWith({ sessionId: "runtime-1", cwd: "/tmp/work", mcpServers: [] })
  })

  it("GenericFollowupResolver_RestoredSessionRoutesRuntimeUpdatesAndPermissions", async () => {
    vi.clearAllMocks()
    const updateHandlers = new Map<string, (notification: unknown) => Promise<void>>()
    const permissionHandlers = new Map<string, (params: unknown) => Promise<unknown>>()
    const resumeSession = vi.fn(async () => undefined)
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { sharedAcpConnection: unknown }
    const connection = { prompt: vi.fn(), cancel: vi.fn(), resumeSession }
    host.sharedAcpConnection = {
      connection,
      processPid: null,
      setSessionHandlers(sessionId: string, update: (notification: unknown) => Promise<void>, permission: (params: unknown) => Promise<unknown>) {
        updateHandlers.set(sessionId, update)
        permissionHandlers.set(sessionId, permission)
      },
      clearSessionHandlers: vi.fn(),
      shutdown: vi.fn(async () => undefined),
    }
    const target: SessionTarget = {
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    }

    await expect(Promise.resolve(capturedFollowupTargetResolver?.(target))).resolves.toEqual({
      connection,
      sessionId: "runtime-1",
      projectId: "project-1",
    })
    const update = updateHandlers.get("runtime-1")
    const permission = permissionHandlers.get("runtime-1")
    if (!update || !permission) throw new Error("restored session handlers were not registered")

    await update({
      sessionId: "runtime-1",
      update: { sessionUpdate: "agent_message_chunk", content: { type: "text", text: "restored reply" } },
    })
    await update({
      sessionId: "runtime-1",
      update: { sessionUpdate: "tool_call", toolCall: { toolName: "bash", status: "in_progress" } },
    })

    expect(agentSessionRuntimeEvents).toHaveBeenNthCalledWith(
      1,
      "project-1",
      "gen-1",
      expect.objectContaining({
        runtimeSessionId: "runtime-1",
        runtimeEvents: [expect.objectContaining({
          type: "message.delta",
          payload: expect.objectContaining({ sessionUpdate: "agent_message_chunk" }),
        })],
      }),
      expect.any(AbortSignal),
    )
    expect(agentSessionRuntimeEvents).toHaveBeenNthCalledWith(
      2,
      "project-1",
      "gen-1",
      expect.objectContaining({
        runtimeEvents: [expect.objectContaining({ type: "tool_call.started" })],
      }),
      expect.any(AbortSignal),
    )
    await expect(permission({
      options: [{ kind: "allow_once", optionId: "allow" }],
    })).resolves.toEqual({ outcome: { outcome: "selected", optionId: "allow" } })
  })

  it("GenericFollowupResolver_StalledRestore_IsTemporarilyUnavailable", async () => {
    vi.clearAllMocks()
    const resumeSession = vi.fn(() => new Promise<never>(() => {}))
    const setSessionHandlers = vi.fn()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { sharedAcpConnection: unknown }
    host.sharedAcpConnection = {
      connection: { prompt: vi.fn(), cancel: vi.fn(), resumeSession },
      processPid: null,
      setSessionHandlers,
      clearSessionHandlers: vi.fn(),
      shutdown: vi.fn(async () => undefined),
    }
    const target: SessionTarget = {
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    }

    const resolution = capturedFollowupTargetResolver?.(target)
    await vi.advanceTimersByTimeAsync(30_000)

    await expect(Promise.resolve(resolution)).resolves.toEqual({ unavailable: true })
    expect(setSessionHandlers).not.toHaveBeenCalled()
  })

  it("GenericFollowupResolver_ResumeFailureReturnsMissingTarget", async () => {
    vi.clearAllMocks()
    const resumeSession = vi.fn(async () => { throw new Error("runtime unavailable") })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { sharedAcpConnection: unknown }
    host.sharedAcpConnection = { connection: { prompt: vi.fn(), cancel: vi.fn(), resumeSession } }

    const resolved = await capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    })

    expect(resolved).toBeNull()
    expect(resumeSession).toHaveBeenCalledTimes(1)
  })

  it("GenericFollowupResolver_RejectsMismatchedConfiguredRunnerProject", async () => {
    vi.clearAllMocks()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "runner-project",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { sharedAcpConnection: unknown; sessionManager: { set(key: string, entry: unknown): void; genericKey(sessionId: string): string } }
    host.sharedAcpConnection = { connection: { prompt: vi.fn() } }
    host.sessionManager.set(host.sessionManager.genericKey("gen-1"), { sessionId: "acp-1", workDir: "/tmp/work" })

    const resolved = capturedFollowupTargetResolver?.({ kind: "generic", projectId: "other-project", sessionId: "gen-1" })

    expect(resolved).toBeNull()
  })
})
