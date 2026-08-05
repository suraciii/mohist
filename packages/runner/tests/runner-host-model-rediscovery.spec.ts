import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import {
  setOpencodeModelDiscoveryForTest,
  type DiscoveredOpencodeModels,
  type OpencodeModelDiscovery,
} from "../src/runtime/opencode-models.js"
import { deferred } from "./support/deferred.js"
import {
  clearOpenCodeRuntimeFactoryForTest,
  installFakeOpenCodeRuntimeFactory,
  type FakeRuntimeHandles,
} from "./support/opencode-runtime-factory.js"
import { capturedLogs } from "./support/logger-test.js"

const REDISCOVERY_INTERVAL_MS = 60_000
const DEFAULT_REDISCOVERY_INTERVAL_MS = 30 * 60_000
const QUIET_INTERVAL_MS = 4 * 60 * 60_000
const baseline: DiscoveredOpencodeModels = {
  models: ["openai/gpt-5.5", "anthropic/claude-sonnet-4"],
  variants: {
    "openai/gpt-5.5": ["low", "high"],
    "anthropic/claude-sonnet-4": ["max"],
  },
  complete: true,
}

const mocks = vi.hoisted(() => ({
  connect: vi.fn(), heartbeat: vi.fn(), disconnect: vi.fn(), poll: vi.fn(),
  report: vi.fn(), uploadTaskLog: vi.fn(), fetchConfig: vi.fn(async () => null),
  startSignalR: vi.fn(), stopSignalR: vi.fn(), getConnectionId: vi.fn(() => "conn-1"),
  probeLiveness: vi.fn(async () => true), forceReconnect: vi.fn(async () => undefined),
  converge: vi.fn(async () => undefined),
}))

let capturedOnReconnected: (() => void) | null = null

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = mocks.connect; heartbeat = mocks.heartbeat; disconnect = mocks.disconnect
    poll = mocks.poll; report = mocks.report; uploadTaskLog = mocks.uploadTaskLog
    fetchConfig = mocks.fetchConfig
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = mocks.startSignalR; stop = mocks.stopSignalR
    getConnectionId = mocks.getConnectionId; probeLiveness = mocks.probeLiveness
    forceReconnect = mocks.forceReconnect
    constructor(_serverUrl: string, _runnerId: string, _runnerRoot: string, _buildGitHash: string | null, options: { onReconnected?: () => void } = {}) {
      capturedOnReconnected = options.onReconnected ?? null
    }
  },
}))

vi.mock("../src/actions/registry.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/actions/registry.js")>()
  return { ...actual, createDefaultRegistry: () => new actual.ActionRegistry([]) }
})

vi.mock("../src/runtime/workspace.js", () => ({
  WorkspaceManager: class {
    async prepare() { return { path: "/virtual/work", branch: "main", changeDir: null } }
    async verify() { return { path: "/virtual/work", branch: "main", changeDir: null } }
  },
}))

vi.mock("../src/runtime/workspace-registry.js", () => ({
  WorkspaceRegistry: class {
    async load() {}
  },
}))

vi.mock("../src/runtime/cleanup-convergence.js", () => ({
  ConvergenceBackstop: class { runOnce = mocks.converge },
  ServerConnectionConvergenceAdapter: class {},
}))

vi.mock("../src/runtime/cleanup-loop.js", () => ({
  CleanupLoop: class {
    async runOnce() {
      return { retentionRemoved: 0, budgetRemoved: 0, guardAborted: 0, stuckResolved: 0, workspaceUsageBytes: 0 }
    }
  },
  DefaultCleanupRunner: class {},
}))

describe("RunnerHost model discovery", () => {
  let connected: ReturnType<typeof deferred<void>>
  let firstPoll: ReturnType<typeof deferred<void>>
  let discovery: ReturnType<typeof vi.fn<OpencodeModelDiscovery>>
  let runtime: FakeRuntimeHandles

  beforeEach(() => {
    vi.useFakeTimers()
    vi.clearAllMocks()
    clearOpenCodeRuntimeFactoryForTest()
    capturedOnReconnected = null
    connected = deferred<void>()
    firstPoll = deferred<void>()
    mocks.connect.mockImplementation(async () => { connected.resolve() })
    mocks.heartbeat.mockResolvedValue(undefined)
    mocks.disconnect.mockResolvedValue(undefined)
    mocks.poll.mockImplementation(async () => { firstPoll.resolve(); return [] })
    mocks.report.mockResolvedValue({})
    mocks.uploadTaskLog.mockResolvedValue({ accepted: 0, truncated: false })
    mocks.startSignalR.mockResolvedValue(undefined)
    mocks.stopSignalR.mockResolvedValue(undefined)
    mocks.converge.mockResolvedValue(undefined)
    discovery = vi.fn<OpencodeModelDiscovery>(async () => baseline)
    setOpencodeModelDiscoveryForTest(discovery)
    runtime = installFakeOpenCodeRuntimeFactory()
  })

  afterEach(() => {
    clearOpenCodeRuntimeFactoryForTest()
  })

  function hostOptions(overrides: Record<string, number> = {}) {
    return {
      serverUrl: "http://localhost:3456",
      runnerId: "runner-rediscovery",
      projectId: "project-1",
      runnerRoot: "/virtual/runner",
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
      cleanupConvergenceIntervalMs: QUIET_INTERVAL_MS,
      cleanupLoopIntervalMs: QUIET_INTERVAL_MS,
      ...overrides,
    }
  }

  async function startHost(overrides: Record<string, number> = {}) {
    const host = new RunnerHost(hostOptions(overrides))
    const controller = new AbortController()
    const run = host.run(controller.signal)
    await connected.promise
    await firstPoll.promise
    return { controller, run }
  }

  async function stopHost(controller: AbortController, run: Promise<void>) {
    controller.abort()
    await run
  }

  it("discovers once before first registration and registers the host-owned snapshot", async () => {
    const { controller, run } = await startHost({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    expect(discovery).toHaveBeenCalledTimes(1)
    expect(discovery.mock.invocationCallOrder[0]).toBeLessThan(mocks.connect.mock.invocationCallOrder[0]!)
    expect(mocks.connect.mock.calls[0]?.[0]).toMatchObject({
      coderModels: baseline.models,
      coderModelVariants: baseline.variants,
    })
    await stopHost(controller, run)
  })

  it("registers empty fields and keeps polling to claim work when initial discovery throws", async () => {
    const failure = new Error("models command unavailable")
    discovery.mockRejectedValueOnce(failure)
    const { controller, run } = await startHost({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "failed to discover opencode models", fields: { exception: failure } }),
    ]))
    expect(mocks.connect.mock.calls[0]?.[0]).toMatchObject({ coderModels: [], coderModelVariants: {} })
    expect(mocks.poll).toHaveBeenCalled()
    await stopHost(controller, run)
  })

  it("uses the 30-minute default and the 60-second configured floor", async () => {
    let running = await startHost()
    await vi.advanceTimersByTimeAsync(DEFAULT_REDISCOVERY_INTERVAL_MS - 1)
    expect(discovery).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(discovery).toHaveBeenCalledTimes(2)
    await stopHost(running.controller, running.run)

    discovery.mockClear()
    connected = deferred<void>()
    firstPoll = deferred<void>()
    mocks.connect.mockImplementation(async () => { connected.resolve() })
    mocks.poll.mockImplementation(async () => { firstPoll.resolve(); return [] })
    running = await startHost({ modelRediscoveryIntervalMs: 1 })
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS - 1)
    expect(discovery).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(discovery).toHaveBeenCalledTimes(2)
    await stopHost(running.controller, running.run)
  })

  it("starts the interval clock only after connection and startup convergence", async () => {
    const connectionRelease = deferred<void>()
    const convergenceEntered = deferred<void>()
    const convergenceRelease = deferred<void>()
    mocks.connect.mockImplementation(async () => {
      connected.resolve()
      await connectionRelease.promise
    })
    mocks.converge.mockImplementation(async () => {
      convergenceEntered.resolve()
      await convergenceRelease.promise
    })
    const host = new RunnerHost(hostOptions({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS }))
    const controller = new AbortController()
    const run = host.run(controller.signal)

    await connected.promise
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 2)
    expect(discovery).toHaveBeenCalledTimes(1)
    connectionRelease.resolve()
    await convergenceEntered.promise
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 2)
    expect(discovery).toHaveBeenCalledTimes(1)
    convergenceRelease.resolve()
    await firstPoll.promise
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS - 1)
    expect(discovery).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(discovery).toHaveBeenCalledTimes(2)
    await stopHost(controller, run)
  })

  it("rediscovers while the OpenCode runtime is not ready", async () => {
    runtime = installFakeOpenCodeRuntimeFactory({ rebuildDelayMs: QUIET_INTERVAL_MS })
    const { controller, run } = await startHost({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    runtime.subscription.emit({ type: "server.disconnected", payload: {} })
    expect(runtime.lastRuntime?.ready()).toBe(false)
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discovery).toHaveBeenCalledTimes(2)
    await stopHost(controller, run)
  })

  it("retains failed or empty results, ignores ordering, and heartbeats model and variant changes", async () => {
    const failure = new Error("rediscovery failed")
    discovery
      .mockResolvedValueOnce(baseline)
      .mockResolvedValueOnce({ models: [...baseline.models].reverse(), variants: {
        "anthropic/claude-sonnet-4": ["max"],
        "openai/gpt-5.5": ["high", "low"],
      }, complete: true })
      .mockResolvedValueOnce({ models: [], variants: {}, complete: true })
      .mockRejectedValueOnce(failure)
      .mockResolvedValueOnce({
        models: ["openai/gpt-5.5", "openai/gpt-6"],
        variants: { "openai/gpt-5.5": ["low", "high"], "openai/gpt-6": ["max"] },
        complete: true,
      })
      .mockResolvedValueOnce({
        models: ["openai/gpt-5.5", "openai/gpt-6"],
        variants: { "openai/gpt-5.5": ["low", "high"], "openai/gpt-6": ["medium", "max"] },
        complete: true,
      })
    const { controller, run } = await startHost({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    capturedOnReconnected?.()
    await vi.advanceTimersByTimeAsync(0)
    expect(mocks.heartbeat.mock.calls[0]?.[0]).toMatchObject({
      coderModels: baseline.models,
      coderModelVariants: baseline.variants,
    })
    mocks.heartbeat.mockClear()

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "failed to discover opencode models", fields: { exception: failure } }),
    ]))
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).toHaveBeenCalledTimes(1)
    expect(mocks.heartbeat.mock.calls[0]?.[0]).toMatchObject({
      coderModels: ["openai/gpt-5.5", "openai/gpt-6"],
      coderModelVariants: { "openai/gpt-5.5": ["low", "high"], "openai/gpt-6": ["max"] },
    })
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).toHaveBeenCalledTimes(2)
    expect(mocks.heartbeat.mock.calls[1]?.[0]).toMatchObject({
      coderModels: ["openai/gpt-5.5", "openai/gpt-6"],
      coderModelVariants: { "openai/gpt-5.5": ["low", "high"], "openai/gpt-6": ["medium", "max"] },
    })
    await stopHost(controller, run)
  })

  it("merges an incomplete rediscovery without removing known models or variants", async () => {
    discovery
      .mockResolvedValueOnce(baseline)
      .mockResolvedValueOnce({
        models: ["openai/gpt-5.5", "google/gemini-3"],
        variants: {
          "openai/gpt-5.5": ["high", "max"],
          "google/gemini-3": ["pro"],
        },
        complete: false,
      })
    const { controller, run } = await startHost({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)

    expect(mocks.heartbeat).toHaveBeenCalledTimes(1)
    expect(mocks.heartbeat.mock.calls[0]?.[0]).toMatchObject({
      coderModels: ["openai/gpt-5.5", "anthropic/claude-sonnet-4", "google/gemini-3"],
      coderModelVariants: {
        "openai/gpt-5.5": ["low", "high", "max"],
        "anthropic/claude-sonnet-4": ["max"],
        "google/gemini-3": ["pro"],
      },
    })
    await stopHost(controller, run)
  })

  it("contains immediate-heartbeat failure and rediscovers on the next interval", async () => {
    discovery
      .mockResolvedValueOnce(baseline)
      .mockResolvedValueOnce({ models: ["openai/gpt-6"], variants: { "openai/gpt-6": ["high"] }, complete: true })
      .mockResolvedValueOnce({ models: ["openai/gpt-7"], variants: { "openai/gpt-7": ["max"] }, complete: true })
    const heartbeatFailure = new Error("heartbeat unavailable")
    mocks.heartbeat.mockRejectedValueOnce(heartbeatFailure).mockResolvedValue(undefined)
    const { controller, run } = await startHost({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "immediate runner heartbeat failed", fields: { exception: heartbeatFailure } }),
    ]))
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(discovery).toHaveBeenCalledTimes(3)
    expect(mocks.heartbeat).toHaveBeenCalledTimes(2)
    await stopHost(controller, run)
  })

  it("clears the rediscovery timer when the run loop terminates", async () => {
    const { controller, run } = await startHost({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    await stopHost(controller, run)
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 2)
    expect(discovery).toHaveBeenCalledTimes(1)
  })
})
