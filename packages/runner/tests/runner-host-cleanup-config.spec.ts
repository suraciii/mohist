import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type { CleanupLoopResult } from "../src/runtime/cleanup-loop.js"
import type { CleanupPolicy } from "../src/core/types.js"

// Idle-system cleanup scenario: when `poll` is continuously
// returning 204 (no work dispatched), the runner's cleanup-loop tick
// must still drive eviction by fetching config from the dedicated
// `/config` channel. This spec exercises the host's
// `runCleanupOnce` wiring through a stubbed `CleanupLoop` so the
// assertions are about the channel (fetchConfig per tick, no caching,
// best-effort failure handling) without depending on the real
// retention / budget algorithm or the `du` subprocess.
//
// We deliberately avoid:
//   - real HTTP (a fake `ServerConnection` is injected via vi.mock);
//   - real time (`vi.useFakeTimers` + `vi.advanceTimersByTimeAsync`);
//   - real disk I/O for the runner root (a temp dir is used only so the
//     host constructor succeeds).

interface CleanupCall {
  policy: CleanupPolicy | null | undefined
  tickIndex: number
}

interface CleanupTestState {
  cleanupCalls: CleanupCall[]
  fetchConfigCalls: undefined[]
  fetchAttempts: number
  onCleanupCall: ((call: CleanupCall) => void) | null
  onFetchConfig: (() => void) | null
  stubFetchConfigBehavior: null | (() => Promise<CleanupPolicy | null>)
  stubRunOnceResult: CleanupLoopResult
}

const CLEANUP_INTERVAL_FLOOR_MS = 1000

const mocks = vi.hoisted(() => {
  const state: CleanupTestState = {
    cleanupCalls: [],
    fetchConfigCalls: [],
    fetchAttempts: 0,
    onCleanupCall: null,
    onFetchConfig: null,
    stubFetchConfigBehavior: null,
    stubRunOnceResult: {
      retentionRemoved: 0,
      budgetRemoved: 0,
      guardAborted: 0,
      stuckResolved: 0,
      workspaceUsageBytes: null,
    },
  }
  return {
    state,
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => ({})),
    startSignalR: vi.fn(async () => undefined),
    stopSignalR: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => "conn-1"),
    probeLiveness: vi.fn(async () => true),
    forceReconnect: vi.fn(async () => undefined),
    createSharedAcpConnection: vi.fn(),
    shutdownSharedAcpConnection: vi.fn(async () => undefined),
  }
})

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = mocks.connect
    heartbeat = mocks.heartbeat
    disconnect = mocks.disconnect
    poll = mocks.poll
    report = mocks.report
    fetchConfig = async (_signal: AbortSignal) => {
      mocks.state.fetchConfigCalls.push(undefined)
      mocks.state.onFetchConfig?.()
      if (!mocks.state.stubFetchConfigBehavior) return null
      return mocks.state.stubFetchConfigBehavior()
    }
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = mocks.startSignalR
    stop = mocks.stopSignalR
    getConnectionId = mocks.getConnectionId
    probeLiveness = mocks.probeLiveness
    forceReconnect = mocks.forceReconnect
    constructor() {
      void this
    }
  },
}))

vi.mock("../src/runtime/opencode-models.js", () => ({
  discoverOpencodeModels: vi.fn(async () => ({ models: ["openai/gpt-5.5"], variants: {} })),
}))

vi.mock("../src/runtime/cleanup-loop.js", () => {
  return {
    CleanupLoop: class {
      async runOnce(policy: CleanupPolicy | null | undefined, _signal: AbortSignal): Promise<CleanupLoopResult> {
        const call = { policy: policy ?? null, tickIndex: mocks.state.cleanupCalls.length + 1 }
        mocks.state.cleanupCalls.push(call)
        mocks.state.onCleanupCall?.(call)
        return mocks.state.stubRunOnceResult
      }
    },
    DefaultCleanupRunner: class {},
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

function resetState() {
  mocks.state.cleanupCalls.length = 0
  mocks.state.fetchConfigCalls.length = 0
  mocks.state.fetchAttempts = 0
  mocks.state.onCleanupCall = null
  mocks.state.onFetchConfig = null
  mocks.state.stubFetchConfigBehavior = null
  mocks.state.stubRunOnceResult = {
    retentionRemoved: 0,
    budgetRemoved: 0,
    guardAborted: 0,
    stuckResolved: 0,
    workspaceUsageBytes: null,
  }
}

beforeEach(() => {
  resetState()
  vi.clearAllMocks()
  mocks.createSharedAcpConnection.mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers: vi.fn(),
    clearSessionHandlers: vi.fn(),
    shutdown: mocks.shutdownSharedAcpConnection,
  })
})

async function importHost() {
  // Dynamic import so the `vi.mock` calls above are wired before the
  // module graph resolves.
  return (await import("../src/runtime/host.js")).RunnerHost
}

function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>((resolvePromise) => {
    resolve = resolvePromise
  })
  return { promise, resolve }
}

interface EventQueue<T> {
  readonly count: number
  next(): Promise<T>
  push(value: T): void
}

function eventQueue<T>(): EventQueue<T> {
  const values: T[] = []
  const waiters: Array<(value: T) => void> = []
  let count = 0

  return {
    get count() {
      return count
    },
    next() {
      if (values.length > 0) return Promise.resolve(values.shift()!)
      return new Promise<T>((resolve) => waiters.push(resolve))
    },
    push(value) {
      count += 1
      const waiter = waiters.shift()
      if (waiter) waiter(value)
      else values.push(value)
    },
  }
}

interface HostEvents {
  connected: ReturnType<typeof deferred>
  polled: ReturnType<typeof deferred>
}

interface CleanupEvents {
  cleanupCalls: EventQueue<CleanupCall>
  fetches: EventQueue<void>
}

function configureHost(): HostEvents {
  const events: HostEvents = {
    connected: deferred(),
    polled: deferred(),
  }

  vi.clearAllMocks()
  mocks.connect.mockReset().mockImplementation(async () => {
    events.connected.resolve()
  })
  mocks.heartbeat.mockReset().mockResolvedValue(undefined)
  mocks.disconnect.mockReset().mockResolvedValue(undefined)
  mocks.poll.mockReset().mockImplementation(async () => {
    events.polled.resolve()
    return []
  })
  mocks.report.mockReset().mockResolvedValue({})
  mocks.startSignalR.mockReset().mockResolvedValue(undefined)
  mocks.stopSignalR.mockReset().mockResolvedValue(undefined)
  mocks.getConnectionId.mockReset().mockReturnValue("conn-1")
  mocks.probeLiveness.mockReset().mockResolvedValue(true)
  mocks.forceReconnect.mockReset().mockResolvedValue(undefined)
  mocks.createSharedAcpConnection.mockReset().mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers: vi.fn(),
    clearSessionHandlers: vi.fn(),
    shutdown: mocks.shutdownSharedAcpConnection,
  })
  mocks.shutdownSharedAcpConnection.mockReset().mockResolvedValue(undefined)
  return events
}

function observeCleanupTicks(): CleanupEvents {
  const events: CleanupEvents = {
    cleanupCalls: eventQueue<CleanupCall>(),
    fetches: eventQueue<void>(),
  }
  mocks.state.onFetchConfig = () => events.fetches.push()
  mocks.state.onCleanupCall = (call) => events.cleanupCalls.push(call)
  return events
}

async function waitForHostStartup(events: HostEvents) {
  await events.connected.promise
  await events.polled.promise
}

async function advanceFetchTick(events: CleanupEvents) {
  const fetch = events.fetches.next()
  await vi.advanceTimersByTimeAsync(CLEANUP_INTERVAL_FLOOR_MS)
  await fetch
}

async function advanceCleanupTick(events: CleanupEvents): Promise<CleanupCall> {
  const cleanup = events.cleanupCalls.next()
  await advanceFetchTick(events)
  return await cleanup
}

describe("RunnerHost idle-system cleanup", () => {
  let root: string

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-runner-host-idle-cleanup-"))
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
    vi.useRealTimers()
  })

  function defaultOptions() {
    return {
      serverUrl: "http://localhost:3456",
      runnerId: "runner-idle-cleanup",
      projectId: "project-1",
      runnerRoot: root,
      pollIntervalMs: 60_000,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
      // Uses the production interval floor while fake timers keep the
      // tests deterministic and fast.
      cleanupLoopIntervalMs: CLEANUP_INTERVAL_FLOOR_MS,
    }
  }

  it("FetchesConfigOnEachCleanupTick_AndRunsEviction_WhenPollStays204", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    mocks.state.stubFetchConfigBehavior = async () => ({ retentionDays: 7 })
    mocks.state.stubRunOnceResult = {
      retentionRemoved: 1,
      budgetRemoved: 0,
      guardAborted: 0,
      stuckResolved: 0,
      workspaceUsageBytes: 200_000,
    }

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await waitForHostStartup(hostEvents)
    const cleanupCalls: CleanupCall[] = []
    for (let tick = 0; tick < 3; tick += 1) {
      cleanupCalls.push(await advanceCleanupTick(cleanupEvents))
    }

    // Each cleanup tick issued its own GET — no caching between ticks.
    expect(mocks.state.fetchConfigCalls).toHaveLength(3)
    expect(cleanupCalls).toHaveLength(3)
    // The CleanupLoop received the policy from the latest fetch on
    // every tick (not a cached value from any dispatch — dispatch is
    // 204 with no body, no policy can leak through).
    for (const call of mocks.state.cleanupCalls) {
      expect(call.policy).toEqual({ retentionDays: 7 })
    }

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("FetchesConfigButEvictsNothing_WhenPolicyIsFullyUnconfigured", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    // /config returns a policy with all-null fields — "null means do not evict".
    mocks.state.stubFetchConfigBehavior = async () => ({
      retentionDays: null,
      storageBudgetBytes: null,
      storageTargetWatermarkBytes: null,
    })
    mocks.state.stubRunOnceResult = {
      retentionRemoved: 0,
      budgetRemoved: 0,
      guardAborted: 0,
      stuckResolved: 0,
      workspaceUsageBytes: null,
    }

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await waitForHostStartup(hostEvents)
    await advanceCleanupTick(cleanupEvents)
    await advanceCleanupTick(cleanupEvents)

    // The host still fetched config on every tick — that is the
    // channel-separation guarantee. Eviction is the loop's job (the
    // stub returns zeros); we only assert fetchConfig was awaited.
    expect(mocks.state.fetchConfigCalls).toHaveLength(2)
    expect(mocks.state.cleanupCalls).toHaveLength(2)
    for (const call of mocks.state.cleanupCalls) {
      expect(call.policy).toEqual({
        retentionDays: null,
        storageBudgetBytes: null,
        storageTargetWatermarkBytes: null,
      })
    }

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("SkipsCleanupTick_WhenFetchConfigThrows_BestEffortNextTickRetries", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    const failure = new Error("fetchConfig failed: 404")

    mocks.state.stubFetchConfigBehavior = async () => {
      mocks.state.fetchAttempts += 1
      // First attempt throws (simulates 404 from old server, network blip, etc).
      if (mocks.state.fetchAttempts === 1) throw failure
      // Subsequent attempts return a configured policy.
      return { retentionDays: 7 }
    }
    mocks.state.stubRunOnceResult = {
      retentionRemoved: 1,
      budgetRemoved: 0,
      guardAborted: 0,
      stuckResolved: 0,
      workspaceUsageBytes: 100_000,
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    try {
      await waitForHostStartup(hostEvents)
      await advanceFetchTick(cleanupEvents)
      expect(mocks.state.cleanupCalls).toHaveLength(0)

      const successfulCall = await advanceCleanupTick(cleanupEvents)
      expect(mocks.state.fetchConfigCalls).toHaveLength(2)
      expect(mocks.state.cleanupCalls).toEqual([successfulCall])
      expect(successfulCall.policy).toEqual({ retentionDays: 7 })
      expect(errorSpy).toHaveBeenCalledOnce()
      expect(errorSpy).toHaveBeenCalledWith("workspace cleanup loop failed:", failure)

      controller.abort()
      await expect(run).resolves.toBeUndefined()
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("IssuesIndependentFetchPerTick_NoCachingAcrossTicks", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    mocks.state.stubFetchConfigBehavior = async () => ({ retentionDays: 7 })

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await waitForHostStartup(hostEvents)
    for (let tick = 0; tick < 5; tick += 1) {
      await advanceCleanupTick(cleanupEvents)
    }

    // Five consecutive ticks issue five independent GETs to /config.
    expect(mocks.state.fetchConfigCalls).toHaveLength(5)
    expect(mocks.state.cleanupCalls).toHaveLength(5)

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("HostIntervals_ClampSubSecondCleanupAndConvergenceConfigurationToOneSecond", async () => {
    const RunnerHost = await importHost()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupLoopIntervalMs: 1,
      cleanupConvergenceIntervalMs: 1,
    })
    const resolved = host as unknown as {
      cleanupLoopIntervalMs: number
      cleanupConvergenceIntervalMs: number
    }

    expect(resolved.cleanupLoopIntervalMs).toBe(CLEANUP_INTERVAL_FLOOR_MS)
    expect(resolved.cleanupConvergenceIntervalMs).toBe(CLEANUP_INTERVAL_FLOOR_MS)
  })
})
