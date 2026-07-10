import { beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"

const mocks = vi.hoisted(() => ({
  connect: vi.fn(),
  heartbeat: vi.fn(),
  disconnect: vi.fn(),
  poll: vi.fn(),
  report: vi.fn(),
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
  createSharedAcpConnection.mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers,
    clearSessionHandlers,
    shutdown: shutdownSharedAcpConnection,
  })
  acpShutdown.mockResolvedValue(undefined)
  shutdownSharedAcpConnection.mockResolvedValue(undefined)
  blockingAction.mockImplementation(async ({ signal }: { signal: AbortSignal }) => new Promise((resolve) => {
    if (signal.aborted) {
      resolve({ status: "failed", message: "aborted" })
      return
    }
    signal.addEventListener("abort", () => resolve({ status: "failed", message: "aborted" }), { once: true })
  }))
})
describe("RunnerHost", () => {
  it("RunnerRegistration_DoesNotReportWorkflowSlots", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
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
    poll
      .mockResolvedValueOnce([work("1")])
      .mockResolvedValueOnce([work("2")])
      .mockResolvedValueOnce([work("3")])
      .mockImplementation(async () => {
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

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(poll.mock.calls.length).toBeGreaterThanOrEqual(4), { timeout: 5_000 })
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
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))
    expect(stopSignalR).toHaveBeenCalled()
  })

  it("RunnerConnection_WhenSignalRFails_DoesNotPollAndRetriesCleanly", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    let resolveSecondSignalR!: () => void
    const secondSignalR = new Promise<void>((resolve) => {
      resolveSecondSignalR = resolve
    })
    startSignalR
      .mockRejectedValueOnce(new Error("signalr unavailable"))
      .mockReturnValueOnce(secondSignalR)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(startSignalR).toHaveBeenCalledTimes(2))
    expect(poll).not.toHaveBeenCalled()
    expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))

    resolveSecondSignalR()
    await vi.waitFor(() => expect(poll).toHaveBeenCalled(), { timeout: 10_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("HeartbeatCarriesCurrentConnectionId_OnHeartbeatTick", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 1,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(heartbeat).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: "conn-A" }),
      expect.any(AbortSignal),
    ), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("SelfCheckTimer_ProbesAndForceReconnects_OnProbeFailure", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValueOnce(false)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 1,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(probeLiveness).toHaveBeenCalled(), { timeout: 5_000 })
    await vi.waitFor(() => expect(forceReconnect).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("SelfCheckTimer_SendsImmediateHeartbeat_WhenManualReconnectReportsNewConnection", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValueOnce(false).mockResolvedValue(true)
    forceReconnect.mockImplementation(async () => {
      getConnectionId.mockReturnValue("conn-AFTER")
      capturedOnReconnected?.("conn-AFTER")
      return undefined
    })
    connect.mockResolvedValue(undefined)
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 1,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(forceReconnect).toHaveBeenCalled(), { timeout: 5_000 })
    await vi.waitFor(() => expect(heartbeat).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: "conn-AFTER" }),
      expect.any(AbortSignal),
    ), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()
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
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 1,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(probeLiveness).toHaveBeenCalled(), { timeout: 5_000 })
    // Give the timer another tick to make sure no forceReconnect is fired
    await new Promise((r) => setTimeout(r, 30))
    expect(forceReconnect).not.toHaveBeenCalled()
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("OnReconnected_InvokesImmediateHeartbeatOnce", async () => {
    vi.clearAllMocks()
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())

    // The host wired an onReconnected callback in the RunnerSignalRClient
    // constructor; capture it for a direct invocation that mirrors a
    // SignalR auto-reconnect completing (the auto-reconnect path bypasses
    // forceReconnect but still funnels through the same callback).
    expect(capturedOnReconnected).toBeTypeOf("function")
    const heartbeatBefore = heartbeat.mock.calls.length

    // Simulate SignalR's auto-reconnect landing: by the time onreconnected
    // fires, getConnectionId() already returns the new id.
    getConnectionId.mockReturnValue("conn-AFTER")
    capturedOnReconnected!("conn-AFTER")

    await vi.waitFor(() => expect(heartbeat.mock.calls.length).toBeGreaterThan(heartbeatBefore), { timeout: 5_000 })
    const lastHeartbeat = heartbeat.mock.calls.at(-1)!
    const lastState = lastHeartbeat[0] as { connectionId?: string }
    expect(lastState.connectionId).toBe("conn-AFTER")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("SelfCheckTimer_ClearedOnShutdown_NoLeakAcrossReconnectLoops", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    let resolveFirstSignalR!: () => void
    const firstSignalR = new Promise<void>((resolve) => {
      resolveFirstSignalR = resolve
    })
    startSignalR.mockReturnValueOnce(firstSignalR)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 1,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    resolveFirstSignalR()
    await vi.waitFor(() => expect(probeLiveness).toHaveBeenCalled(), { timeout: 5_000 })

    // Abort — should clear both timers and stopSignalR called once.
    controller.abort()
    await expect(run).resolves.toBeUndefined()
    const probeCountAtShutdown = probeLiveness.mock.calls.length

    // Wait long enough that any leaked timer would fire again.
    await new Promise((r) => setTimeout(r, 30))
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
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    // report never resolves → the work stays in awaitingAck.
    report.mockImplementation(() => new Promise(() => {}))
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
    // First poll dispatches the held work; subsequent polls return empty.
    poll.mockResolvedValueOnce([held]).mockResolvedValue([])
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    // Wait until a poll after the dispatch carries the work key in the
    // awaitingAck set (execution completes quickly under the blocking
    // action, then report never acks → it sits in awaitingAck).
    await vi.waitFor(() => {
      const bodies = poll.mock.calls
        .filter((c) => c.length > 1 && c[1])
        .map((c) => c[1] as { inFlight: string[]; awaitingAck: string[] })
      expect(bodies.some((b) => b.awaitingAck.includes("workflow:wr-held:work-held"))).toBe(true)
    }, { timeout: 5_000 })

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("ReDispatchedWork_ReportedOnce_NotPerRedelivery", async () => {
    vi.clearAllMocks()
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
    report.mockReturnValue(new Promise(() => {}))
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
    poll.mockResolvedValueOnce([same]).mockResolvedValueOnce([same]).mockResolvedValueOnce([same]).mockResolvedValue([])
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    // Wait until all three re-dispatches have been polled.
    await vi.waitFor(() => expect(poll.mock.calls.length).toBeGreaterThanOrEqual(4), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    // The same work, re-delivered three times while its report is unacked,
    // is reported at most once: re-delivery of a held work is skipped.
    const reportsForDup = report.mock.calls.filter((c) => c[0]?.workId === "work-dup")
    expect(reportsForDup.length).toBeLessThanOrEqual(1)
  })

  it("AwaitingAck_RetriesReportUntilAcked", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    // First two report attempts fail; the third succeeds.
    report
      .mockRejectedValueOnce(new Error("transient"))
      .mockRejectedValueOnce(new Error("transient"))
      .mockResolvedValueOnce(undefined)
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
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    // The work is reported at least 3 times (first attempt + 2 retries
    // driven by the reconciliation loop at a 5s cadence). Allow generous
    // headroom for the retry cadence.
    await vi.waitFor(() => expect(report.mock.calls.length).toBeGreaterThanOrEqual(3), { timeout: 30_000 })

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  }, 40_000)
})
