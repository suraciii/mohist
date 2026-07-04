import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type { CleanupLoopResult } from "../src/runtime/cleanup-loop.js"
import type { CleanupPolicy } from "../src/core/types.js"

// Idle-system cleanup scenario (issue-359): when `poll` is continuously
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
  fetchConfigCalls: number[]
  fetchAttempts: number
  stubFetchConfigBehavior: null | (() => Promise<CleanupPolicy | null>)
  stubRunOnceResult: CleanupLoopResult
}

const mocks = vi.hoisted(() => {
  const state: CleanupTestState = {
    cleanupCalls: [],
    fetchConfigCalls: [],
    fetchAttempts: 0,
    stubFetchConfigBehavior: null,
    stubRunOnceResult: {
      retentionRemoved: 0,
      budgetRemoved: 0,
      guardAborted: 0,
      workspaceUsageBytes: null,
    },
  }
  return {
    state,
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => null),
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
      mocks.state.fetchConfigCalls.push(Date.now())
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
        mocks.state.cleanupCalls.push({ policy: policy ?? null, tickIndex: mocks.state.cleanupCalls.length + 1 })
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
  mocks.state.stubFetchConfigBehavior = null
  mocks.state.stubRunOnceResult = {
    retentionRemoved: 0,
    budgetRemoved: 0,
    guardAborted: 0,
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

async function flushMicrotasks() {
  // Two awaits in series drain microtasks until quiescence. A single
  // `await Promise.resolve()` is not enough because some continuations
  // are scheduled on later microtask turns.
  await Promise.resolve()
  await Promise.resolve()
}

describe("RunnerHost idle-system cleanup (issue-359 T-002)", () => {
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
      // Short interval so the test can drive ticks deterministically.
      cleanupLoopIntervalMs: 50,
    }
  }

  it("FetchesConfigOnEachCleanupTick_AndRunsEviction_WhenPollStays204", async () => {
    vi.useFakeTimers()
    // poll returns 204 (null) every call — the idle scenario.
    mocks.poll.mockImplementation(async () => null)
    mocks.state.stubFetchConfigBehavior = async () => ({ retentionDays: 7 })
    mocks.state.stubRunOnceResult = {
      retentionRemoved: 1,
      budgetRemoved: 0,
      guardAborted: 0,
      workspaceUsageBytes: 200_000,
    }

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    // Wait for startup connect + first poll cycle.
    await vi.waitFor(() => expect(mocks.connect).toHaveBeenCalled(), { timeout: 5_000 })

    // Advance in 50ms slices and flush microtasks between ticks so the
    // fire-and-forget runCleanupOnce promises settle before the next
    // timer fires. With `void this.runCleanupOnce(signal)` the host
    // discards the promise, so vi.advanceTimersByTimeAsync alone
    // does not await it.
    for (let i = 0; i < 5; i += 1) {
      await vi.advanceTimersByTimeAsync(50)
      await flushMicrotasks()
    }
    await vi.waitFor(() => expect(mocks.state.cleanupCalls.length).toBeGreaterThanOrEqual(3), { timeout: 5_000 })

    // Each cleanup tick issued its own GET — no caching between ticks.
    expect(mocks.state.fetchConfigCalls.length).toBeGreaterThanOrEqual(3)
    // The CleanupLoop received the policy from the latest fetch on
    // every tick (not a cached value from any dispatch — dispatch is
    // 204 with no body, no policy can leak through).
    for (const call of mocks.state.cleanupCalls) {
      expect(call.policy).toEqual({ retentionDays: 7 })
    }

    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })
  })

  it("FetchesConfigButEvictsNothing_WhenPolicyIsFullyUnconfigured", async () => {
    vi.useFakeTimers()
    mocks.poll.mockImplementation(async () => null)
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
      workspaceUsageBytes: null,
    }

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await vi.waitFor(() => expect(mocks.connect).toHaveBeenCalled(), { timeout: 5_000 })
    for (let i = 0; i < 5; i += 1) {
      await vi.advanceTimersByTimeAsync(50)
      await flushMicrotasks()
    }
    await vi.waitFor(() => expect(mocks.state.cleanupCalls.length).toBeGreaterThanOrEqual(2), { timeout: 5_000 })

    // The host still fetched config on every tick — that is the
    // channel-separation guarantee. Eviction is the loop's job (the
    // stub returns zeros); we only assert fetchConfig was awaited.
    expect(mocks.state.fetchConfigCalls.length).toBeGreaterThanOrEqual(2)
    for (const call of mocks.state.cleanupCalls) {
      expect(call.policy).toEqual({
        retentionDays: null,
        storageBudgetBytes: null,
        storageTargetWatermarkBytes: null,
      })
    }

    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })
  })

  it("SkipsCleanupTick_WhenFetchConfigThrows_BestEffortNextTickRetries", async () => {
    vi.useFakeTimers()
    mocks.poll.mockImplementation(async () => null)

    mocks.state.stubFetchConfigBehavior = async () => {
      mocks.state.fetchAttempts += 1
      // First attempt throws (simulates 404 from old server, network blip, etc).
      if (mocks.state.fetchAttempts === 1) throw new Error("fetchConfig failed: 404")
      // Subsequent attempts return a configured policy.
      return { retentionDays: 7 }
    }
    mocks.state.stubRunOnceResult = {
      retentionRemoved: 1,
      budgetRemoved: 0,
      guardAborted: 0,
      workspaceUsageBytes: 100_000,
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await vi.waitFor(() => expect(mocks.connect).toHaveBeenCalled(), { timeout: 5_000 })
    for (let i = 0; i < 5; i += 1) {
      await vi.advanceTimersByTimeAsync(50)
      await flushMicrotasks()
    }
    await vi.waitFor(() => expect(mocks.state.fetchConfigCalls.length).toBeGreaterThanOrEqual(2), { timeout: 5_000 })

    // The first tick's fetch threw — runOnce was NOT called for it.
    // Subsequent ticks succeeded and reached runOnce. Wait until
    // microtasks settle so every successful fetch has reached
    // runOnce.
    await vi.waitFor(() => expect(mocks.state.cleanupCalls.length).toBeGreaterThanOrEqual(1), { timeout: 5_000 })
    await flushMicrotasks()
    expect(mocks.state.cleanupCalls.length).toBeGreaterThanOrEqual(1)
    expect(mocks.state.cleanupCalls.length).toBeLessThan(mocks.state.fetchConfigCalls.length)
    // The successful ticks carried the configured policy from the
    // post-failure fetch attempts.
    for (const call of mocks.state.cleanupCalls) {
      expect(call.policy).toEqual({ retentionDays: 7 })
    }
    // The first-tick failure was logged (best-effort per design D4).
    expect(errorSpy.mock.calls.some((call) => call.some((arg) => typeof arg === "string" && arg.includes("workspace cleanup loop failed")))).toBe(true)

    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })
    errorSpy.mockRestore()
  })

  it("IssuesIndependentFetchPerTick_NoCachingAcrossTicks", async () => {
    vi.useFakeTimers()
    mocks.poll.mockImplementation(async () => null)
    mocks.state.stubFetchConfigBehavior = async () => ({ retentionDays: 7 })

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await vi.waitFor(() => expect(mocks.connect).toHaveBeenCalled(), { timeout: 5_000 })
    for (let i = 0; i < 12; i += 1) {
      await vi.advanceTimersByTimeAsync(50)
      await flushMicrotasks()
    }
    await vi.waitFor(() => expect(mocks.state.fetchConfigCalls.length).toBeGreaterThanOrEqual(5), { timeout: 5_000 })

    // Five consecutive ticks ⇒ five independent GETs to /config. No
    // ETag / If-None-Match / version conditional fetch is performed.
    // The host did not retain any cached policy between ticks.
    // Wait until fetchConfig and runOnce counts align (every fetch
    // should have reached the cleanup loop in this test — no
    // fetchConfig failures).
    await vi.waitFor(() => expect(mocks.state.cleanupCalls.length).toBeGreaterThanOrEqual(5), { timeout: 5_000 })
    await flushMicrotasks()
    expect(mocks.state.fetchConfigCalls.length).toBeGreaterThanOrEqual(5)
    expect(mocks.state.cleanupCalls.length).toBe(mocks.state.fetchConfigCalls.length)

    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })
  })
})