import { beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"

const connect = vi.fn()
const heartbeat = vi.fn()
const disconnect = vi.fn()
const poll = vi.fn()
const startSignalR = vi.fn()
const stopSignalR = vi.fn()
const getConnectionId = vi.fn(() => "conn-1")
const probeLiveness = vi.fn(async () => true)

// Capture the onReconnected callback that RunnerHost passes into the
// RunnerSignalRClient constructor. Each new RunnerSignalRClient instance
// overwrites this slot with its most-recently registered callback. Tests
// can then invoke it to simulate the client reporting a completed reconnect.
let capturedOnReconnected: ((connectionId: string) => void) | null = null

const forceReconnect = vi.fn(async () => undefined)
const createSharedAcpConnection = vi.fn()
const shutdownSharedAcpConnection = vi.fn()

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = startSignalR
    stop = stopSignalR
    getConnectionId = getConnectionId
    probeLiveness = probeLiveness
    forceReconnect = forceReconnect
    constructor(_serverUrl: string, _runnerId: string, _runnerRoot: string, _buildGitHash: string | null, options: { onReconnected?: (id: string) => void } = {}) {
      capturedOnReconnected = options.onReconnected ?? null
    }
  },
}))

vi.mock("../src/runtime/opencode-models.js", () => ({
  discoverOpencodeModels: vi.fn(async () => ({ models: ["openai/gpt-5.5"], variants: {} })),
}))

const setSessionHandlers = vi.fn()
const clearSessionHandlers = vi.fn()
const acpShutdown = vi.fn()

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    key(_workflowRunId: string, _sessionName: string) { return `${_workflowRunId}:${_sessionName}` }
    get(_key: string) { return undefined }
    set() {}
    has() { return false }
    delete() {}
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
})
describe("RunnerHost", () => {
  it("RunnerRegistration_ReportsConfiguredWorkflowSlots", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 3,
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
        maxWorkflowSlots: 3,
      }),
      expect.any(AbortSignal),
    )
  })

  it("RunnerShutdown_UnregistersRunner", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
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
    poll.mockResolvedValue(null)
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
      maxConcurrentWorkflows: 1,
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
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
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
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
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
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
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

  it("SelfCheckTimer_DoesNotReconnect_OnProbeSuccess", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
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
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
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
    poll.mockResolvedValue(null)
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
      maxConcurrentWorkflows: 1,
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
})
