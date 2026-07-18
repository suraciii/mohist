import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"
import { deferred } from "./support/deferred.js"
import { clearOpenCodeRuntimeFactoryForTest, installReadyOpenCodeRuntimeFactory } from "./support/opencode-runtime-factory.js"

const installReadyRuntimeFactory = installReadyOpenCodeRuntimeFactory

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000
const HEARTBEAT_INTERVAL_MS = 10
const SELF_CHECK_INTERVAL_MS = 10

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
  installReadyRuntimeFactory()
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

afterEach(() => {
  clearOpenCodeRuntimeFactoryForTest()
})

describe("RunnerHost", () => {
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
    try {
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
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
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
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      expect(probeLiveness).toHaveBeenCalledTimes(2)
      expect(forceReconnect).not.toHaveBeenCalled()
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })

  it("OnReconnected_InvokesImmediateHeartbeatOnce", async () => {
    vi.clearAllMocks()
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const immediateHeartbeat = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
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
    try {
      await pollStarted.promise
      expect(capturedOnReconnected).toBeTypeOf("function")
      getConnectionId.mockReturnValue("conn-AFTER")
      capturedOnReconnected!("conn-AFTER")

      await immediateHeartbeat.promise
      const lastHeartbeat = heartbeat.mock.calls.at(-1)!
      const lastState = lastHeartbeat[0] as { connectionId?: string }
      expect(lastState.connectionId).toBe("conn-AFTER")
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
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
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      expect(probeLiveness).toHaveBeenCalledTimes(1)

      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()
      const probeCountAtShutdown = probeLiveness.mock.calls.length

      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS * 2)
      expect(probeLiveness.mock.calls.length).toBe(probeCountAtShutdown)
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })
})
