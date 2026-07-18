import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { DiscoveredOpencodeModels } from "../src/runtime/opencode-models.js"
import { deferred } from "./support/deferred.js"

// Idle-system rediscovery scenario: a runner-host spec focused on the
// periodic rediscovery timer that runs alongside the heartbeat /
// self-check / convergence / cleanup timers in `RunnerHost.run()`. We
// deliberately avoid:
//   - real HTTP (a fake `ServerConnection` is injected via `vi.mock`);
//   - real time (`vi.useFakeTimers` + `vi.advanceTimersByTimeAsync`);
//   - real disk I/O for the runner root (a temp dir is used only so
//     the host constructor succeeds);
//   - real `opencode` invocation (`discoverOpencodeModels` is mocked
//     so we can shape the order-insensitive set-comparison outcome).

const REDISCOVERY_INTERVAL_MS = 60_000
const QUIET_INTERVAL_MS = 600_000

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
  forceReconnect: vi.fn(async () => undefined),
  createSharedAcpConnection: vi.fn(),
  shutdownSharedAcpConnection: vi.fn(),
}))

const {
  connect,
  heartbeat,
  disconnect,
  poll,
  startSignalR,
  stopSignalR,
  getConnectionId,
  createSharedAcpConnection,
  shutdownSharedAcpConnection,
} = mocks

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = mocks.connect
    heartbeat = mocks.heartbeat
    disconnect = mocks.disconnect
    poll = mocks.poll
    report = mocks.report
    uploadTaskLog = mocks.uploadTaskLog
    fetchConfig = mocks.fetchConfig
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = mocks.startSignalR
    stop = mocks.stopSignalR
    getConnectionId = mocks.getConnectionId
    probeLiveness = mocks.probeLiveness
    forceReconnect = mocks.forceReconnect
    constructor() { void this }
  },
}))

vi.mock("../src/runtime/opencode-models.js", async () => {
  const actual = await vi.importActual<typeof import("../src/runtime/opencode-models.js")>("../src/runtime/opencode-models.js")
  return {
    ...actual,
    discoverOpencodeModels: vi.fn(),
  }
})

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    workflowKey(workflowRunId: string, sessionName: string) { return `workflow:${workflowRunId}:${sessionName}` }
    genericKey(sessionId: string) { return `generic:${sessionId}` }
    get() { return undefined }
    set() {}
    has() { return false }
    delete() {}
  },
  createSharedAcpConnection: (...args: unknown[]) => mocks.createSharedAcpConnection(...args),
}))

interface HostEvents {
  connected: ReturnType<typeof deferred>
  polled: ReturnType<typeof deferred>
}

describe("RunnerHost periodic model rediscovery", () => {
  let root: string
  let events: HostEvents
  let discover: ReturnType<typeof vi.fn>

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-runner-host-rediscovery-"))
    vi.useFakeTimers()
    events = { connected: deferred(), polled: deferred() }
    vi.clearAllMocks()
    mocks.connect.mockImplementation(async () => {
      events.connected.resolve(undefined)
    })
    mocks.heartbeat.mockResolvedValue(undefined)
    mocks.disconnect.mockResolvedValue(undefined)
    mocks.poll.mockImplementation(async () => {
      events.polled.resolve(undefined)
      return []
    })
    mocks.startSignalR.mockResolvedValue(undefined)
    mocks.stopSignalR.mockResolvedValue(undefined)
    mocks.getConnectionId.mockReturnValue("conn-1")
    mocks.createSharedAcpConnection.mockResolvedValue({
      connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
      processPid: 99999,
      setSessionHandlers: vi.fn(),
      clearSessionHandlers: vi.fn(),
      shutdown: mocks.shutdownSharedAcpConnection,
    })
    mocks.shutdownSharedAcpConnection.mockResolvedValue(undefined)
    discover = (await import("../src/runtime/opencode-models.js")).discoverOpencodeModels as unknown as ReturnType<typeof vi.fn>
    discover.mockReset().mockResolvedValue({ models: ["openai/gpt-5.5"], variants: {} })
  })

  afterEach(async () => {
    vi.useRealTimers()
    if (root) await rm(root, { recursive: true, force: true })
  })

  function buildHost() {
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-rediscovery",
      projectId: "project-1",
      runnerRoot: root,
      pollIntervalMs: 600_000,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
      modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS,
    })
    const controller = new AbortController()
    const run = host.run(controller.signal)
    return { host, controller, run }
  }

  async function bootstrapHost() {
    const ctx = buildHost()
    await events.connected.promise
    await events.polled.promise
    return ctx
  }

  function readState(host: RunnerHost): { coderModels: string[]; coderModelVariants: Record<string, string[]> } {
    return host as unknown as { coderModels: string[]; coderModelVariants: Record<string, string[]> }
  }

  it("TimerDoesNotFireBeforeInterval_AndFiresOnceAtInterval", async () => {
    const { host, controller, run } = await bootstrapHost()
    heartbeat.mockClear()
    expect(discover.mock.calls.length).toBe(1)

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS - 1)
    expect(discover.mock.calls.length).toBe(1)

    await vi.advanceTimersByTimeAsync(1)
    expect(discover.mock.calls.length).toBe(2)

    controller.abort()
    await run
    void host
  })

  it("UnchangedRediscovery_DoesNotTriggerExtraHeartbeat", async () => {
    const { host, controller, run } = await bootstrapHost()
    heartbeat.mockClear()

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discover.mock.calls.length).toBe(2)
    expect(heartbeat).not.toHaveBeenCalled()

    const stateAfterFirstFire = readState(host)
    expect(stateAfterFirstFire.coderModels).toEqual(["openai/gpt-5.5"])
    expect(stateAfterFirstFire.coderModelVariants).toEqual({})

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discover.mock.calls.length).toBe(3)
    expect(heartbeat).not.toHaveBeenCalled()

    controller.abort()
    await run
  })

  it("ChangedRediscovery_TriggersOneImmediateHeartbeatWithUpdatedState", async () => {
    const { host, controller, run } = await bootstrapHost()
    const baselineState: DiscoveredOpencodeModels = { models: ["openai/gpt-5.5"], variants: {} }
    const changedState: DiscoveredOpencodeModels = {
      models: ["openai/gpt-5.5", "anthropic/claude-sonnet-4"],
      variants: { "openai/gpt-5.5": ["low"], "anthropic/claude-sonnet-4": ["max"] },
    }
    discover.mockReset()
      .mockResolvedValueOnce(baselineState)
      .mockResolvedValueOnce(changedState)
    heartbeat.mockClear()

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discover.mock.calls.length).toBe(1)
    expect(heartbeat).not.toHaveBeenCalled()

    const stateAfterFirst = readState(host)
    expect(stateAfterFirst.coderModels).toEqual(["openai/gpt-5.5"])
    expect(stateAfterFirst.coderModelVariants).toEqual({})

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discover.mock.calls.length).toBe(2)
    expect(heartbeat).toHaveBeenCalledTimes(1)

    const heartbeatBody = heartbeat.mock.calls[0]?.[0] as { coderModels: string[]; coderModelVariants: Record<string, string[]> }
    expect(heartbeatBody.coderModels).toEqual(["openai/gpt-5.5", "anthropic/claude-sonnet-4"])
    expect(heartbeatBody.coderModelVariants).toEqual({
      "openai/gpt-5.5": ["low"],
      "anthropic/claude-sonnet-4": ["max"],
    })

    controller.abort()
    await run
  })

  it("EmptyRediscovery_LeavesLocalStateUnchanged_AndDoesNotTriggerHeartbeat", async () => {
    const { host, controller, run } = await bootstrapHost()
    discover.mockReset()
      .mockResolvedValueOnce({ models: ["openai/gpt-5.5"], variants: {} })
      .mockResolvedValueOnce({ models: [], variants: {} })
    heartbeat.mockClear()

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discover.mock.calls.length).toBe(1)

    const stateAfterFirst = readState(host)
    expect(stateAfterFirst.coderModels).toEqual(["openai/gpt-5.5"])

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discover.mock.calls.length).toBe(2)
    expect(heartbeat).not.toHaveBeenCalled()

    const stateAfterEmpty = readState(host)
    expect(stateAfterEmpty.coderModels).toEqual(["openai/gpt-5.5"])
    expect(stateAfterEmpty.coderModelVariants).toEqual({})

    controller.abort()
    await run
  })

  it("ThrownDiscoveryError_IsLogged_AndNextIntervalStillFires", async () => {
    const { host, controller, run } = await bootstrapHost()
    discover.mockReset()
      .mockResolvedValueOnce({ models: ["openai/gpt-5.5"], variants: {} })
      .mockImplementationOnce(async () => { throw new Error("opencode unavailable") })
      .mockResolvedValueOnce({ models: ["openai/gpt-5.5"], variants: {} })
    heartbeat.mockClear()
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    try {
      await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
      expect(discover.mock.calls.length).toBe(1)

      await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
      expect(discover.mock.calls.length).toBe(2)
      expect(heartbeat).not.toHaveBeenCalled()
      expect(errorSpy).toHaveBeenCalledWith("model rediscovery fire failed", expect.any(Error))

      const stateAfterThrow = readState(host)
      expect(stateAfterThrow.coderModels).toEqual(["openai/gpt-5.5"])

      await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
      expect(discover.mock.calls.length).toBe(3)
      expect(heartbeat).not.toHaveBeenCalled()
    } finally {
      errorSpy.mockRestore()
    }

    controller.abort()
    await run
    void host
  })

  it("AbortingRunner_ClearsTimerAndPreventsFurtherFires", async () => {
    const { controller, run } = await bootstrapHost()

    controller.abort()
    await run

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 5)
    expect(discover.mock.calls.length).toBe(1)
  })
})