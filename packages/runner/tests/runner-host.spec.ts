import { beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"
import { deferred } from "./support/deferred.js"

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000
const HEARTBEAT_INTERVAL_MS = 10
const SELF_CHECK_INTERVAL_MS = 10
const AWAITING_ACK_RETRY_INTERVAL_MS = 5_000

const mocks = vi.hoisted(() => ({
  connect: vi.fn(),
  heartbeat: vi.fn(),
  disconnect: vi.fn(),
  poll: vi.fn(),
  report: vi.fn(),
  uploadTaskLog: vi.fn(),
  fetchConfig: vi.fn(async () => null),
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

// Capture the onReconnected callback that RunnerHost passes into the
// RunnerSignalRClient constructor. Each new RunnerSignalRClient instance
// overwrites this slot with its most-recently registered callback. Tests
// can then invoke it to simulate the client reporting a completed reconnect.
let capturedOnReconnected: ((connectionId: string) => void) | null = null
let capturedFollowupTargetResolver: ((target: SessionTarget) => { connection: unknown; sessionId: string; projectId: string } | null) | null = null

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
    report = report
    uploadTaskLog = uploadTaskLog
    fetchConfig = fetchConfig
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = startSignalR
    stop = stopSignalR
    getConnectionId = getConnectionId
    probeLiveness = probeLiveness
    forceReconnect = forceReconnect
    constructor(_serverUrl: string, _runnerId: string, _runnerRoot: string, _buildGitHash: string | null, options: { onReconnected?: (id: string) => void; followupTargetResolver?: typeof capturedFollowupTargetResolver } = {}) {
      capturedOnReconnected = options.onReconnected ?? null
      capturedFollowupTargetResolver = options.followupTargetResolver ?? null
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
    await pollCalls[0]!.promise
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
    await pollCalls[1]!.promise
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
    await pollCalls[2]!.promise
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
    await pollCalls[3]!.promise
    await expect(run).resolves.toBeUndefined()
  })

  it("WorkerPool_PollFailure_RetriesWithoutRestartingRunner", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    const controller = new AbortController()
    poll
      .mockRejectedValueOnce(new Error("server unavailable"))
      .mockImplementationOnce(async () => {
        controller.abort()
        return []
      })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    try {
      const run = host.run(controller.signal)
      await vi.waitFor(() => expect(poll).toHaveBeenCalledTimes(2), { timeout: 5_000 })
      await expect(run).resolves.toBeUndefined()

      expect(connect).toHaveBeenCalledTimes(1)
      expect(startSignalR).toHaveBeenCalledTimes(1)
      expect(warn).toHaveBeenCalledWith(
        expect.stringContaining("runner poll failed; retrying"),
        expect.any(Error),
      )
    } finally {
      warn.mockRestore()
    }
  })

  it("WorkerPool_PollTimeout_AbortsAttemptAndContinuesPolling", async () => {
    vi.useFakeTimers()
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    const controller = new AbortController()
    poll
      .mockImplementationOnce((signal: AbortSignal) => new Promise((_, reject) => {
        signal.addEventListener("abort", () => reject(signal.reason), { once: true })
      }))
      .mockImplementationOnce(async () => {
        controller.abort()
        return []
      })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    try {
      const run = host.run(controller.signal)
      await vi.waitFor(() => expect(poll).toHaveBeenCalledTimes(1))
      await vi.advanceTimersByTimeAsync(10_002)
      await vi.waitFor(() => expect(poll).toHaveBeenCalledTimes(2))
      await expect(run).resolves.toBeUndefined()
    } finally {
      warn.mockRestore()
      vi.useRealTimers()
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
    await connected.promise
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))
    expect(stopSignalR).toHaveBeenCalled()
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

  it("HeartbeatCarriesCurrentConnectionId_OnHeartbeatTick", async () => {
    vi.clearAllMocks()
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const heartbeatSent = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockImplementation(async () => {
      heartbeatSent.resolve()
    })
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: HEARTBEAT_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    await pollStarted.promise
    await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS)
    await heartbeatSent.promise
    expect(heartbeat).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: "conn-A" }),
      expect.any(AbortSignal),
    )
    controller.abort()
    pollRelease.resolve([])
    await expect(run).resolves.toBeUndefined()

  })

  it("SelfCheckTimer_ProbesAndForceReconnects_OnProbeFailure", async () => {
    vi.clearAllMocks()
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const reconnectStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValueOnce(false)
    forceReconnect.mockImplementation(async () => {
      reconnectStarted.resolve()
    })
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      await reconnectStarted.promise
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()

      expect(warningSpy).toHaveBeenCalledTimes(1)
      expect(warningSpy).toHaveBeenNthCalledWith(1, "dispatch liveness probe failed; forcing reconnect")
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
      warningSpy.mockRestore()
    }
  })

  it("SelfCheckTimer_SendsImmediateHeartbeat_WhenManualReconnectReportsNewConnection", async () => {
    vi.clearAllMocks()
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const reconnectStarted = deferred<void>()
    const immediateHeartbeat = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValueOnce(false).mockResolvedValue(true)
    forceReconnect.mockImplementation(async () => {
      reconnectStarted.resolve()
      getConnectionId.mockReturnValue("conn-AFTER")
      capturedOnReconnected?.("conn-AFTER")
      return undefined
    })
    connect.mockResolvedValue(undefined)
    heartbeat.mockImplementation(async () => {
      immediateHeartbeat.resolve()
    })
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      await reconnectStarted.promise
      await immediateHeartbeat.promise
      expect(heartbeat).toHaveBeenCalledWith(
        expect.objectContaining({ connectionId: "conn-AFTER" }),
        expect.any(AbortSignal),
      )
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()

      expect(warningSpy).toHaveBeenCalledTimes(1)
      expect(warningSpy).toHaveBeenNthCalledWith(1, "dispatch liveness probe failed; forcing reconnect")
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
      warningSpy.mockRestore()
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

  it("SelfCheckTimer_DoesNotReconnect_OnProbeSuccess", async () => {
    vi.clearAllMocks()
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    await pollStarted.promise
    await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
    await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
    expect(probeLiveness).toHaveBeenCalledTimes(2)
    expect(forceReconnect).not.toHaveBeenCalled()
    controller.abort()
    pollRelease.resolve([])
    await expect(run).resolves.toBeUndefined()
  })

  it("OnReconnected_InvokesImmediateHeartbeatOnce", async () => {
    vi.clearAllMocks()
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const immediateHeartbeat = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockImplementation(async () => {
      // After a successful (re)connect the real RunnerSignalRClient
      // exposes the new connection id from getConnectionId(). Mirror
      // that update synchronously so the host's immediate heartbeat
      // reads the new id.
      getConnectionId.mockReturnValue("conn-AFTER")
      capturedOnReconnected?.("conn-AFTER")
      return undefined
    })
    connect.mockResolvedValue(undefined)
    heartbeat.mockImplementation(async () => {
      immediateHeartbeat.resolve()
    })
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
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
    await pollStarted.promise

    // The host wired an onReconnected callback in the RunnerSignalRClient
    // constructor; capture it for a direct invocation that mirrors a
    // SignalR auto-reconnect completing (the auto-reconnect path bypasses
    // forceReconnect but still funnels through the same callback).
    expect(capturedOnReconnected).toBeTypeOf("function")
    // Simulate SignalR's auto-reconnect landing: by the time onreconnected
    // fires, getConnectionId() already returns the new id.
    getConnectionId.mockReturnValue("conn-AFTER")
    capturedOnReconnected!("conn-AFTER")

    await immediateHeartbeat.promise
    const lastHeartbeat = heartbeat.mock.calls.at(-1)!
    const lastState = lastHeartbeat[0] as { connectionId?: string }
    expect(lastState.connectionId).toBe("conn-AFTER")

    controller.abort()
    pollRelease.resolve([])
    await expect(run).resolves.toBeUndefined()
  })

  it("SelfCheckTimer_ClearedOnShutdown_NoLeakAcrossReconnectLoops", async () => {
    vi.clearAllMocks()
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    await pollStarted.promise
    await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
    expect(probeLiveness).toHaveBeenCalledTimes(1)

    // Abort — should clear both timers and stopSignalR called once.
    controller.abort()
    pollRelease.resolve([])
    await expect(run).resolves.toBeUndefined()
    const probeCountAtShutdown = probeLiveness.mock.calls.length

    await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS * 2)
    expect(probeLiveness.mock.calls.length).toBe(probeCountAtShutdown)
  })

  // =========================================================================
  // Process-lifetime reported set (Batch 1): inFlight ∪ awaitingAck must
  // survive poll exceptions and connection resets; re-dispatched works are
  // skipped; awaitingAck retries until acked. These tests pin the fix for
  // the rollback-storm root cause (design/workflow/scheduling.md §Poll
  // Reconciliation — Implementation constraint).
  // =========================================================================

  it("PollBody_CarriesInFlightAndAwaitingAck_Keys", async () => {
    vi.clearAllMocks()
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    const secondPollStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockImplementation(async () => {
      reportStarted.resolve()
      await reportRelease.promise
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const held = {
      workflowRunId: "wr-held",
      workId: "work-held",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    }
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollIndex += 1
      if (pollIndex === 1) return [held]
      secondPollStarted.resolve()
      return []
    })
    blockingAction.mockResolvedValue({ status: "success", message: "ok" })
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

    await reportStarted.promise
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
    await secondPollStarted.promise

    const bodies = poll.mock.calls
      .filter((calls) => calls.length > 1 && calls[1])
      .map((calls) => calls[1] as { inFlight: string[]; awaitingAck: string[] })
    expect(bodies.some((body) => body.awaitingAck.includes("workflow:wr-held:work-held"))).toBe(true)

    controller.abort()
    reportRelease.resolve()
    await expect(run).resolves.toBeUndefined()
  })

  it("ReDispatchedWork_ReportedOnce_NotPerRedelivery", async () => {
    vi.clearAllMocks()
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    const pollCalls = [deferred<void>(), deferred<void>(), deferred<void>(), deferred<void>()]
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    // The owner has not yet acked — the work stays in awaitingAck and is
    // reported in every poll body. Under at-least-once the server may
    // re-dispatch a work whose report is still in flight; the runner must
    // skip re-deliveries of a work it still holds (inFlight or awaitingAck)
    // rather than execute and report it again.
    report.mockImplementation(async () => {
      reportStarted.resolve()
      await reportRelease.promise
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const same = {
      workflowRunId: "wr-dup",
      workId: "work-dup",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    }
    // The server re-dispatches the same work on three consecutive polls
    // (the recovery path under at-least-once). The runner must dedupe:
    // the work is reported at most once for the set of re-deliveries.
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollCalls[pollIndex]?.resolve()
      pollIndex += 1
      return pollIndex <= 3 ? [same] : []
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
    blockingAction.mockResolvedValue({ status: "success", message: "ok" })
    const run = host.run(controller.signal)

    await reportStarted.promise
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
    await pollCalls[1]!.promise
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
    await pollCalls[2]!.promise
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
    await pollCalls[3]!.promise
    controller.abort()
    reportRelease.resolve()
    await expect(run).resolves.toBeUndefined()

    // The same work, re-delivered three times while its report is unacked,
    // is reported at most once: re-delivery of a held work is skipped.
    const reportsForDup = report.mock.calls.filter((c) => c[0]?.workId === "work-dup")
    expect(reportsForDup.length).toBeLessThanOrEqual(1)
  })

  it("AwaitingAck_RetriesReportUntilAcked", async () => {
    vi.clearAllMocks()
    const firstReport = deferred<void>()
    const secondReport = deferred<void>()
    const thirdReport = deferred<void>()
    const firstFailureLogged = deferred<void>()
    const secondFailureLogged = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    // First two report attempts fail; the third succeeds.
    const firstFailure = new Error("first transient")
    const secondFailure = new Error("second transient")
    let attempt = 0
    report.mockImplementation(async () => {
      attempt += 1
      if (attempt === 1) {
        firstReport.resolve()
        throw firstFailure
      }
      if (attempt === 2) {
        secondReport.resolve()
        throw secondFailure
      }
      thirdReport.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const work = {
      workflowRunId: "wr-retry",
      workId: "work-retry",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    }
    poll.mockResolvedValueOnce([work]).mockResolvedValue([])
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    blockingAction.mockResolvedValue({ status: "success", message: "ok" })
    const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation((message: unknown) => {
      if (message === "first report for work work-retry failed; will retry") firstFailureLogged.resolve()
      if (message === "retry report for work work-retry failed (attempt 2)") secondFailureLogged.resolve()
    })
    const run = host.run(controller.signal)
    try {
      await firstReport.promise
      await firstFailureLogged.promise
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await secondReport.promise
      await secondFailureLogged.promise
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await thirdReport.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(uploadTaskLog).toHaveBeenCalledTimes(1)
      expect(warningSpy).toHaveBeenCalledTimes(2)
      expect(warningSpy).toHaveBeenNthCalledWith(
        1,
        "first report for work work-retry failed; will retry",
        firstFailure,
      )
      expect(warningSpy).toHaveBeenNthCalledWith(
        2,
        "retry report for work work-retry failed (attempt 2)",
        secondFailure,
      )
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      warningSpy.mockRestore()
    }
  })
})
