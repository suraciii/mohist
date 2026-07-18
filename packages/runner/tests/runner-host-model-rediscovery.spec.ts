import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { RuntimeModelCatalog } from "../src/runtime/opencode/index.js"
import { deferred } from "./support/deferred.js"
import {
  clearOpenCodeRuntimeFactoryForTest,
  installFakeOpenCodeRuntimeFactory,
  type FakeRuntimeHandles,
} from "./support/opencode-runtime-factory.js"

const REDISCOVERY_INTERVAL_MS = 60_000
const QUIET_INTERVAL_MS = 600_000
const baseline: RuntimeModelCatalog = {
  models: [{ providerID: "openai", modelID: "gpt-5.5", variants: [] }],
  fetchedAt: 0,
}

const mocks = vi.hoisted(() => ({
  connect: vi.fn(), heartbeat: vi.fn(), disconnect: vi.fn(), poll: vi.fn(),
  report: vi.fn(), uploadTaskLog: vi.fn(), fetchConfig: vi.fn(async () => null),
  startSignalR: vi.fn(), stopSignalR: vi.fn(), getConnectionId: vi.fn(() => "conn-1"),
  probeLiveness: vi.fn(async () => true), forceReconnect: vi.fn(async () => undefined),
  createSharedAcpConnection: vi.fn(), shutdownSharedAcpConnection: vi.fn(),
}))

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
    constructor() { void this }
  },
}))

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

describe("RunnerHost periodic model rediscovery", () => {
  let root: string
  let connected: ReturnType<typeof deferred>
  let polled: ReturnType<typeof deferred>
  let runtime: FakeRuntimeHandles

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-runner-host-rediscovery-"))
    vi.useFakeTimers()
    vi.clearAllMocks()
    clearOpenCodeRuntimeFactoryForTest()
    connected = deferred()
    polled = deferred()
    mocks.connect.mockImplementation(async () => connected.resolve(undefined))
    mocks.heartbeat.mockResolvedValue(undefined)
    mocks.disconnect.mockResolvedValue(undefined)
    mocks.poll.mockImplementation(async () => { polled.resolve(undefined); return [] })
    mocks.startSignalR.mockResolvedValue(undefined)
    mocks.stopSignalR.mockResolvedValue(undefined)
    mocks.createSharedAcpConnection.mockResolvedValue({
      connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
      processPid: 99999,
      setSessionHandlers: vi.fn(),
      clearSessionHandlers: vi.fn(),
      shutdown: mocks.shutdownSharedAcpConnection,
    })
    mocks.shutdownSharedAcpConnection.mockResolvedValue(undefined)
    runtime = installFakeOpenCodeRuntimeFactory({ catalog: baseline })
  })

  afterEach(async () => {
    clearOpenCodeRuntimeFactoryForTest()
    vi.useRealTimers()
    await rm(root, { recursive: true, force: true })
  })

  async function bootstrapHost() {
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456", runnerId: "runner-rediscovery", projectId: "project-1",
      runnerRoot: root, pollIntervalMs: QUIET_INTERVAL_MS, heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS, modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS,
    })
    const controller = new AbortController()
    const run = host.run(controller.signal)
    await connected.promise
    await polled.promise
    return { controller, run }
  }

  it("refreshes only at the configured interval", async () => {
    const { controller, run } = await bootstrapHost()
    expect(runtime.catalogList).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS - 1)
    expect(runtime.catalogList).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(runtime.catalogList).toHaveBeenCalledTimes(2)
    controller.abort()
    await run
  })

  it("does not heartbeat when the refreshed set is unchanged", async () => {
    const { controller, run } = await bootstrapHost()
    mocks.heartbeat.mockClear()
    runtime.catalogList.mockResolvedValueOnce({ ...baseline, fetchedAt: 1 })
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    controller.abort()
    await run
  })

  it("heartbeats immediately with a changed SDK catalog", async () => {
    const { controller, run } = await bootstrapHost()
    mocks.heartbeat.mockClear()
    runtime.catalogList.mockResolvedValueOnce({
      models: [
        { providerID: "openai", modelID: "gpt-5.5", variants: ["low"] },
        { providerID: "anthropic", modelID: "claude-sonnet-4", variants: ["max"] },
      ],
      fetchedAt: 1,
    })
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).toHaveBeenCalledTimes(1)
    expect(mocks.heartbeat.mock.calls[0]?.[0]).toMatchObject({
      coderModels: ["openai/gpt-5.5", "anthropic/claude-sonnet-4"],
      coderModelVariants: {
        "openai/gpt-5.5": ["low"],
        "anthropic/claude-sonnet-4": ["max"],
      },
    })
    controller.abort()
    await run
  })

  it("retains the previous catalog after an empty or failed refresh", async () => {
    const { controller, run } = await bootstrapHost()
    mocks.heartbeat.mockClear()
    runtime.catalogList.mockResolvedValueOnce({ models: [], fetchedAt: 1 })
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    runtime.catalogList.mockRejectedValueOnce(new Error("opencode unavailable"))
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    expect(runtime.lastRuntime?.catalog()).toEqual({ models: [], fetchedAt: 1 })
    controller.abort()
    await run
  })

  it("clears the refresh timer on shutdown", async () => {
    const { controller, run } = await bootstrapHost()
    controller.abort()
    await run
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 2)
    expect(runtime.catalogList).toHaveBeenCalledTimes(1)
  })
})
