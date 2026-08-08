import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import {
  type DiscoveredOpencodeModels,
  type OpencodeModelDiscovery,
} from "../src/runtime/opencode-models.js"
import type { PiRuntime } from "../src/runtime/pi/index.js"
import type { RunnerFileSystem } from "../src/system/filesystem.js"
import type { RunnerLogger } from "../src/system/logger.js"
import type { ExternalProcessPolicy } from "../src/system/process-policy.js"
import { deferred } from "./support/deferred.js"
import {
  installFakeOpenCodeRuntimeFactory,
  type FakeRuntimeHandles,
  type OpenCodeRuntimeTestResources,
} from "./support/opencode-runtime-factory.js"
import { capturedLogs, createLoggerCapture } from "./support/logger-test.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"

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

type ModelMock = ReturnType<typeof vi.fn>
type ModelMocks = Record<
  "connect" | "heartbeat" | "disconnect" | "poll" | "report" | "uploadTaskLog" | "fetchConfig" |
  "startSignalR" | "stopSignalR" | "getConnectionId" | "probeLiveness" | "forceReconnect" | "converge",
  ModelMock
>

interface ModelTestResources extends OpenCodeRuntimeTestResources {
  fileSystem: RunnerFileSystem
  logger: RunnerLogger
  externalProcessPolicy: ExternalProcessPolicy
  opencodeModelDiscovery: OpencodeModelDiscovery
  piRuntimeFactory: () => PiRuntime
}

interface ModelTestState {
  readonly resources: ModelTestResources
  readonly mocks: ModelMocks
  connected: ReturnType<typeof deferred<void>>
  firstPoll: ReturnType<typeof deferred<void>>
  readonly discovery: ReturnType<typeof vi.fn<OpencodeModelDiscovery>>
  runtime: FakeRuntimeHandles
  onReconnected: (() => void) | null
}

const modelTestStorage = new AsyncLocalStorage<ModelTestState>()

function currentModelTestState(): ModelTestState {
  const state = modelTestStorage.getStore()
  if (!state) throw new Error("model rediscovery test context is not active")
  return state
}

function scopedMock(name: keyof ModelMocks): ModelMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, "_isMockFunction", { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentModelTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentModelTestState().mocks[name], property)
      return typeof value === "function" ? value.bind(currentModelTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentModelTestState().mocks[name], property, value)
    },
  }) as unknown as ModelMock
}

const mocks: Record<keyof ModelMocks, ModelMock> = {
  connect: scopedMock("connect"),
  heartbeat: scopedMock("heartbeat"),
  disconnect: scopedMock("disconnect"),
  poll: scopedMock("poll"),
  report: scopedMock("report"),
  uploadTaskLog: scopedMock("uploadTaskLog"),
  fetchConfig: scopedMock("fetchConfig"),
  startSignalR: scopedMock("startSignalR"),
  stopSignalR: scopedMock("stopSignalR"),
  getConnectionId: scopedMock("getConnectionId"),
  probeLiveness: scopedMock("probeLiveness"),
  forceReconnect: scopedMock("forceReconnect"),
  converge: scopedMock("converge"),
}

function createModelMocks(): ModelMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => ({})),
    uploadTaskLog: vi.fn(async () => ({ accepted: 0, truncated: false })),
    fetchConfig: vi.fn(async () => null),
    startSignalR: vi.fn(async () => undefined),
    stopSignalR: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => "conn-1"),
    probeLiveness: vi.fn(async () => true),
    forceReconnect: vi.fn(async () => undefined),
    converge: vi.fn(async () => undefined),
  }
}

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
      currentModelTestState().onReconnected = options.onReconnected ?? null
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
  NamedWorkspaceRegistry: class {
    async load() {}
    list() {
      return []
    }
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
  function it(name: string, body: (state: ModelTestState) => Promise<void>): void {
    vitestIt(name, async () => {
      vi.useFakeTimers()
      const discovery = vi.fn<OpencodeModelDiscovery>(async () => baseline)
      const resources: ModelTestResources = {
        fileSystem: new MemoryFileSystem(),
        logger: createLoggerCapture(),
        externalProcessPolicy: {
          assertAllowed(label) {
            throw new Error(`external process forbidden in model rediscovery test: ${label}`)
          },
          register() {},
        },
        opencodeModelDiscovery: discovery,
        piRuntimeFactory: () => ({
          start: async () => ({ ok: true, value: { ready: true, diagnostic: null, catalog: { models: [] } }, diagnostics: [] }),
          ready: () => true,
          diagnostic: () => null,
          catalog: () => ({ models: [] }),
          shutdown: async () => {},
        } as never),
      }
      const state: ModelTestState = {
        resources,
        mocks: createModelMocks(),
        connected: deferred<void>(),
        firstPoll: deferred<void>(),
        discovery,
        runtime: undefined as never,
        onReconnected: null,
      }
      state.runtime = installFakeOpenCodeRuntimeFactory(resources)
      try {
        await modelTestStorage.run(state, async () => {
          mocks.connect.mockImplementation(async () => { state.connected.resolve() })
          mocks.poll.mockImplementation(async () => { state.firstPoll.resolve(); return [] })
          await withTestRunnerResources(async () => await body(state), resources)
        })
      } finally {
        vi.useRealTimers()
      }
    })
  }

  function hostOptions(overrides: Record<string, number> = {}) {
    return {
      serverUrl: "https://runner.test",
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

  async function startHost(state: ModelTestState, overrides: Record<string, number> = {}) {
    const host = new RunnerHost(hostOptions(overrides))
    const controller = new AbortController()
    const run = host.run(controller.signal)
    await state.connected.promise
    await state.firstPoll.promise
    return { controller, run }
  }

  async function stopHost(controller: AbortController, run: Promise<void>) {
    controller.abort()
    await run
  }

  it("discovers once before first registration and registers the host-owned snapshot", async (state) => {
    const { controller, run } = await startHost(state, { modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    expect(state.discovery).toHaveBeenCalledTimes(1)
    expect(state.discovery.mock.invocationCallOrder[0]).toBeLessThan(mocks.connect.mock.invocationCallOrder[0]!)
    expect(mocks.connect.mock.calls[0]?.[0]).toMatchObject({
      coderModels: baseline.models,
      coderModelVariants: baseline.variants,
    })
    await stopHost(controller, run)
  })

  it("registers empty fields and keeps polling to claim work when initial discovery throws", async (state) => {
    const failure = new Error("models command unavailable")
    state.discovery.mockRejectedValueOnce(failure)
    const { controller, run } = await startHost(state, { modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "failed to discover opencode models", fields: { exception: failure } }),
    ]))
    expect(mocks.connect.mock.calls[0]?.[0]).toMatchObject({ coderModels: [], coderModelVariants: {} })
    expect(mocks.poll).toHaveBeenCalled()
    await stopHost(controller, run)
  })

  it("uses the 30-minute default and the 60-second configured floor", async (state) => {
    let running = await startHost(state)
    await vi.advanceTimersByTimeAsync(DEFAULT_REDISCOVERY_INTERVAL_MS - 1)
    expect(state.discovery).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(state.discovery).toHaveBeenCalledTimes(2)
    await stopHost(running.controller, running.run)

    state.discovery.mockClear()
    state.connected = deferred<void>()
    state.firstPoll = deferred<void>()
    mocks.connect.mockImplementation(async () => { state.connected.resolve() })
    mocks.poll.mockImplementation(async () => { state.firstPoll.resolve(); return [] })
    running = await startHost(state, { modelRediscoveryIntervalMs: 1 })
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS - 1)
    expect(state.discovery).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(state.discovery).toHaveBeenCalledTimes(2)
    await stopHost(running.controller, running.run)
  })

  it("starts the interval clock only after connection and startup convergence", async (state) => {
    const connectionRelease = deferred<void>()
    const convergenceEntered = deferred<void>()
    const convergenceRelease = deferred<void>()
    mocks.connect.mockImplementation(async () => {
      state.connected.resolve()
      await connectionRelease.promise
    })
    mocks.converge.mockImplementation(async () => {
      convergenceEntered.resolve()
      await convergenceRelease.promise
    })
    const host = new RunnerHost(hostOptions({ modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS }))
    const controller = new AbortController()
    const run = host.run(controller.signal)

    await state.connected.promise
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 2)
    expect(state.discovery).toHaveBeenCalledTimes(1)
    connectionRelease.resolve()
    await convergenceEntered.promise
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 2)
    expect(state.discovery).toHaveBeenCalledTimes(1)
    convergenceRelease.resolve()
    await state.firstPoll.promise
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS - 1)
    expect(state.discovery).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(state.discovery).toHaveBeenCalledTimes(2)
    await stopHost(controller, run)
  })

  it("rediscovers while the OpenCode runtime is not ready", async (state) => {
    state.runtime = installFakeOpenCodeRuntimeFactory(state.resources, { rebuildDelayMs: QUIET_INTERVAL_MS })
    const { controller, run } = await startHost(state, { modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    state.runtime.subscription.emit({ type: "server.disconnected", payload: {} })
    expect(state.runtime.lastRuntime?.ready()).toBe(false)
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(state.discovery).toHaveBeenCalledTimes(2)
    await stopHost(controller, run)
  })

  it("retains failed or empty results, ignores ordering, and heartbeats model and variant changes", async (state) => {
    const failure = new Error("rediscovery failed")
    state.discovery
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
    const { controller, run } = await startHost(state, { modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(mocks.heartbeat).not.toHaveBeenCalled()
    state.onReconnected?.()
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

  it("merges an incomplete rediscovery without removing known models or variants", async (state) => {
    state.discovery
      .mockResolvedValueOnce(baseline)
      .mockResolvedValueOnce({
        models: ["openai/gpt-5.5", "google/gemini-3"],
        variants: {
          "openai/gpt-5.5": ["high", "max"],
          "google/gemini-3": ["pro"],
        },
        complete: false,
      })
    const { controller, run } = await startHost(state, { modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })

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

  it("contains immediate-heartbeat failure and rediscovers on the next interval", async (state) => {
    state.discovery
      .mockResolvedValueOnce(baseline)
      .mockResolvedValueOnce({ models: ["openai/gpt-6"], variants: { "openai/gpt-6": ["high"] }, complete: true })
      .mockResolvedValueOnce({ models: ["openai/gpt-7"], variants: { "openai/gpt-7": ["max"] }, complete: true })
    const heartbeatFailure = new Error("heartbeat unavailable")
    mocks.heartbeat.mockRejectedValueOnce(heartbeatFailure).mockResolvedValue(undefined)
    const { controller, run } = await startHost(state, { modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })

    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "immediate runner heartbeat failed", fields: { exception: heartbeatFailure } }),
    ]))
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS)
    expect(state.discovery).toHaveBeenCalledTimes(3)
    expect(mocks.heartbeat).toHaveBeenCalledTimes(2)
    await stopHost(controller, run)
  })

  it("clears the rediscovery timer when the run loop terminates", async (state) => {
    const { controller, run } = await startHost(state, { modelRediscoveryIntervalMs: REDISCOVERY_INTERVAL_MS })
    await stopHost(controller, run)
    await vi.advanceTimersByTimeAsync(REDISCOVERY_INTERVAL_MS * 2)
    expect(state.discovery).toHaveBeenCalledTimes(1)
  })
})
