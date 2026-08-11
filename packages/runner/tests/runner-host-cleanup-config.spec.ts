import { AsyncLocalStorage } from "node:async_hooks"
import { join } from "node:path"
import { describe, expect, it as vitestIt, vi } from "vitest"
import type { CleanupLoopResult } from "../src/runtime/cleanup-loop.js"
import type { CleanupPolicy } from "../src/core/types.js"
import { installReadyOpenCodeRuntimeFactory } from "./support/opencode-runtime-factory.js"
import type { FakeRuntimeHandles } from "./support/opencode-runtime-factory.js"
import { capturedLogs } from "./support/logger-test.js"
import type { DefaultRunnerTestResources } from "./support/test-resources.js"
import { withDefaultRunnerTestResources } from "./support/test-resources.js"

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
//   - real disk I/O for the runner root (the test uses a MemoryFileSystem).

interface CleanupCall {
  policy: CleanupPolicy | null | undefined
  tickIndex: number
}

interface CleanupTestState {
  readonly resources: DefaultRunnerTestResources
  readonly root: string
  readonly runtimeHandles: FakeRuntimeHandles
  hostEvents: HostEvents | null
  cleanupCalls: CleanupCall[]
  fetchConfigCalls: undefined[]
  fetchAttempts: number
  onCleanupCall: ((call: CleanupCall) => void) | null
  onFetchConfig: (() => void) | null
  stubFetchConfigBehavior: null | (() => Promise<CleanupPolicy | null>)
  stubRunOnceResult: CleanupLoopResult
  blockedPaths: ReadonlySet<string> | null
}

const CLEANUP_INTERVAL_FLOOR_MS = 1000

const cleanupTestStorage = new AsyncLocalStorage<CleanupTestState>()

function currentCleanupTestState(): CleanupTestState {
  const state = cleanupTestStorage.getStore()
  if (!state) throw new Error("cleanup test resource context is not active")
  return state
}

function createCleanupTestState(resources: DefaultRunnerTestResources, runtimeHandles: FakeRuntimeHandles): CleanupTestState {
  return {
    resources,
    root: "/virtual/runner-host-idle-cleanup",
    runtimeHandles,
    hostEvents: null,
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
    blockedPaths: null,
  }
}

function testRoot(): string {
  return currentCleanupTestState().root
}

function testState(): CleanupTestState {
  return currentCleanupTestState()
}

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    async connect() {
      currentCleanupTestState().hostEvents?.connected.resolve()
    }

    async heartbeat() {}

    async disconnect() {}

    async poll() {
      currentCleanupTestState().hostEvents?.polled.resolve()
      return []
    }

    async report() {
      return {}
    }

    fetchConfig = async (_signal: AbortSignal) => {
      const state = currentCleanupTestState()
      state.fetchConfigCalls.push(undefined)
      state.onFetchConfig?.()
      if (!state.stubFetchConfigBehavior) return null
      return state.stubFetchConfigBehavior()
    }
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    async start() {}
    async stop() {}
    getConnectionId() { return "conn-1" }
    async probeLiveness() { return true }
    async forceReconnect() {}
  },
}))

vi.mock("../src/runtime/cleanup-loop.js", () => {
  return {
    CleanupLoop: class {
      async runOnce(policy: CleanupPolicy | null | undefined, _signal: AbortSignal, blockedPaths: ReadonlySet<string>): Promise<CleanupLoopResult> {
        const state = currentCleanupTestState()
        state.blockedPaths = blockedPaths
        const call = { policy: policy ?? null, tickIndex: state.cleanupCalls.length + 1 }
        state.cleanupCalls.push(call)
        state.onCleanupCall?.(call)
        return state.stubRunOnceResult
      }
    },
    DefaultCleanupRunner: class {},
  }
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

  testState().hostEvents = events
  return events
}

function observeCleanupTicks(): CleanupEvents {
  const events: CleanupEvents = {
    cleanupCalls: eventQueue<CleanupCall>(),
    fetches: eventQueue<void>(),
  }
  testState().onFetchConfig = () => events.fetches.push()
  testState().onCleanupCall = (call) => events.cleanupCalls.push(call)
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
  function it(name: string, body: () => Promise<void>): void {
    vitestIt(name, async () => {
      await withDefaultRunnerTestResources(async (resources) => {
        const runtimeHandles = installReadyOpenCodeRuntimeFactory(resources)
        const state = createCleanupTestState(resources, runtimeHandles)
        await cleanupTestStorage.run(state, async () => {
          try {
            await body()
          } finally {
            vi.useRealTimers()
          }
        })
      })
    })
  }

  function defaultOptions() {
    return {
      serverUrl: "https://runner.test",
      runnerId: "runner-idle-cleanup",
      projectId: "project-1",
      runnerRoot: testRoot(),
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
    testState().stubFetchConfigBehavior = async () => ({ retentionDays: 7 })
    testState().stubRunOnceResult = {
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
    expect(testState().fetchConfigCalls).toHaveLength(3)
    expect(cleanupCalls).toHaveLength(3)
    // The CleanupLoop received the policy from the latest fetch on
    // every tick (not a cached value from any dispatch — dispatch is
    // 204 with no body, no policy can leak through).
    for (const call of testState().cleanupCalls) {
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
    testState().stubFetchConfigBehavior = async () => ({
      retentionDays: null,
      storageBudgetBytes: null,
      storageTargetWatermarkBytes: null,
    })
    testState().stubRunOnceResult = {
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
    expect(testState().fetchConfigCalls).toHaveLength(2)
    expect(testState().cleanupCalls).toHaveLength(2)
    for (const call of testState().cleanupCalls) {
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

    testState().stubFetchConfigBehavior = async () => {
      testState().fetchAttempts += 1
      // First attempt throws (simulates 404 from old server, network blip, etc).
      if (testState().fetchAttempts === 1) throw failure
      // Subsequent attempts return a configured policy.
      return { retentionDays: 7 }
    }
    testState().stubRunOnceResult = {
      retentionRemoved: 1,
      budgetRemoved: 0,
      guardAborted: 0,
      stuckResolved: 0,
      workspaceUsageBytes: 100_000,
    }

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    {
      await waitForHostStartup(hostEvents)
      await advanceFetchTick(cleanupEvents)
      expect(testState().cleanupCalls).toHaveLength(0)

      const successfulCall = await advanceCleanupTick(cleanupEvents)
      expect(testState().fetchConfigCalls).toHaveLength(2)
      expect(testState().cleanupCalls).toEqual([successfulCall])
      expect(successfulCall.policy).toEqual({ retentionDays: 7 })
      expect(capturedLogs()).toEqual(expect.arrayContaining([
        expect.objectContaining({ level: "ERROR", message: "workspace cleanup loop failed", fields: { exception: failure } }),
      ]))

      controller.abort()
      await expect(run).resolves.toBeUndefined()
    }
  })

  it("ReclaimsBeforeConfig_AndPassesOnlyTheBlockedSnapshotToCleanup", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    testState().stubFetchConfigBehavior = async () => ({ retentionDays: 7 })
    const order: string[] = []
    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)
    await waitForHostStartup(hostEvents)
    const runtime = await testState().runtimeHandles.runtimeCreated
    vi.spyOn(runtime, "reclaimWhere").mockImplementation(async () => {
      order.push("reclaim")
      return {
        tracked: 1,
        candidates: 1,
        disposed: 0,
        busy: 1,
        failed: 0,
        blockedDirectories: ["/blocked"],
        diagnostics: [],
      }
    })
    const previousFetch = testState().onFetchConfig
    testState().onFetchConfig = () => {
      order.push("fetch")
      previousFetch?.()
    }
    const previousCleanup = testState().onCleanupCall
    testState().onCleanupCall = (call) => {
      order.push("cleanup")
      previousCleanup?.(call)
    }

    await advanceCleanupTick(cleanupEvents)

    expect(order).toEqual(["reclaim", "fetch", "cleanup"])
    expect(testState().blockedPaths).toEqual(new Set(["/blocked"]))
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("LogsOneBoundedReclaimSummaryOnlyWhenCandidatesExist", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    testState().stubFetchConfigBehavior = async () => ({ retentionDays: 7 })
    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)
    await waitForHostStartup(hostEvents)
    const runtime = await testState().runtimeHandles.runtimeCreated
    const reclaim = vi.spyOn(runtime, "reclaimWhere")
      .mockResolvedValueOnce({ tracked: 2, candidates: 0, disposed: 0, busy: 0, failed: 0, blockedDirectories: [], diagnostics: [] })
      .mockResolvedValueOnce({
        tracked: 6,
        candidates: 2,
        disposed: 1,
        busy: 1,
        failed: 0,
        blockedDirectories: ["/busy"],
        diagnostics: [
          { severity: "warning", code: "zeta", message: "hidden" },
          { severity: "info", code: "alpha", message: "hidden" },
          { severity: "warning", code: "alpha", message: "hidden" },
          { severity: "warning", code: "delta", message: "hidden" },
          { severity: "warning", code: "beta", message: "hidden" },
          { severity: "warning", code: "omega", message: "hidden" },
        ],
      })
    try {
      await advanceCleanupTick(cleanupEvents)
      expect(capturedLogs().filter((record) => record.message === "workspace reclaim completed")).toHaveLength(0)
      await advanceCleanupTick(cleanupEvents)
      expect(capturedLogs().filter((record) => record.message === "workspace reclaim completed")).toEqual([
        expect.objectContaining({ level: "INFO", fields: { reason: "workspace reclaim: tracked=6 candidates=2 disposed=1 busy=1 failed=0 diagnostics=alpha:2,beta:1,delta:1,omega:1 omitted=1" } }),
      ])
      expect(reclaim).toHaveBeenCalledTimes(2)
    } finally {
      controller.abort()
      await expect(run).resolves.toBeUndefined()
    }
  })

  it("SkipsDiskCleanup_WhenRuntimeReclamationThrows", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    const failure = new Error("runtime reclaim failed")
    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)
    await waitForHostStartup(hostEvents)
    const runtime = await testState().runtimeHandles.runtimeCreated
    vi.spyOn(runtime, "reclaimWhere").mockRejectedValue(failure)

    await vi.advanceTimersByTimeAsync(CLEANUP_INTERVAL_FLOOR_MS)

    expect(testState().fetchConfigCalls).toHaveLength(0)
    expect(testState().cleanupCalls).toHaveLength(0)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "workspace cleanup runtime reclamation failed", fields: { exception: failure } }),
    ]))
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("SingleFlightsOverlappingTimerInvocations", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    testState().stubFetchConfigBehavior = async () => ({ retentionDays: 7 })
    const reclaimStarted = deferred()
    const reclaimGate = deferred()
    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)
    await waitForHostStartup(hostEvents)
    const runtime = await testState().runtimeHandles.runtimeCreated
    const reclaim = vi.spyOn(runtime, "reclaimWhere").mockImplementation(async () => {
      reclaimStarted.resolve()
      await reclaimGate.promise
      return { tracked: 0, candidates: 0, disposed: 0, busy: 0, failed: 0, blockedDirectories: [], diagnostics: [] }
    })

    const firstTick = vi.advanceTimersByTimeAsync(CLEANUP_INTERVAL_FLOOR_MS)
    await reclaimStarted.promise
    await vi.advanceTimersByTimeAsync(CLEANUP_INTERVAL_FLOOR_MS)
    expect(reclaim).toHaveBeenCalledTimes(1)

    const fetch = cleanupEvents.fetches.next()
    const cleanup = cleanupEvents.cleanupCalls.next()
    reclaimGate.resolve()
    await firstTick
    await fetch
    await cleanup
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("IssuesIndependentFetchPerTick_NoCachingAcrossTicks", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    testState().stubFetchConfigBehavior = async () => ({ retentionDays: 7 })

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await waitForHostStartup(hostEvents)
    for (let tick = 0; tick < 5; tick += 1) {
      await advanceCleanupTick(cleanupEvents)
    }

    // Five consecutive ticks issue five independent GETs to /config.
    expect(testState().fetchConfigCalls).toHaveLength(5)
    expect(testState().cleanupCalls).toHaveLength(5)

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("SweepsRetiredAgentWorkspacesDirectoryOnCleanupTick_AndIsIdempotentAcrossTicks", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
    const hostEvents = configureHost()
    const cleanupEvents = observeCleanupTicks()
    testState().stubFetchConfigBehavior = async () => ({ retentionDays: 7 })

    // The retired managed-worktree tree is pre-existing data under
    // the runner root — the cleanup tick must sweep it whole.
    const legacyWorkspaces = join(testRoot(), "agent-workspaces")
    await testState().resources.fileSystem.ensureDir(legacyWorkspaces)
    await testState().resources.fileSystem.writeText(join(legacyWorkspaces, "stale-worktree"), "retired")
    await testState().resources.fileSystem.writeText(join(legacyWorkspaces, "manifest.json"), "{}")

    const RunnerHost = await importHost()
    const controller = new AbortController()
    const host = new RunnerHost(defaultOptions())
    const run = host.run(controller.signal)

    await waitForHostStartup(hostEvents)
    await advanceCleanupTick(cleanupEvents)

    // First tick: the whole directory is gone as disk-policy cleanup.
    await expect(testState().resources.fileSystem.stat(legacyWorkspaces)).rejects.toMatchObject({ code: "ENOENT" })
    expect(capturedLogs().filter((record) => record.message === "removed retired agent-workspaces directory")).toEqual([
      expect.objectContaining({ level: "INFO", fields: { path: legacyWorkspaces } }),
    ])

    // Second tick: nothing left to sweep — no error, directory stays gone.
    await advanceCleanupTick(cleanupEvents)
    await expect(testState().resources.fileSystem.stat(legacyWorkspaces)).rejects.toMatchObject({ code: "ENOENT" })

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
