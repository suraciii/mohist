import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"

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
  acpShutdown,
} = mocks

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
    constructor() {
      void this
    }
  },
}))

vi.mock("../src/runtime/opencode-models.js", () => ({
  discoverOpencodeModels: vi.fn(async () => ({ models: ["openai/gpt-5.5"], variants: {} })),
}))

vi.mock("../src/actions/registry.js", () => ({
  createDefaultRegistry: () => ({
    resolve: (uses?: string | null) => uses === "test/block" || uses === "test/log" ? blockingAction : undefined,
  }),
}))

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    private sessions = new Map<string, { sessionId: string; workDir: string }>()
    key(target: SessionTarget) { return target.kind === "workflow" ? this.workflowKey(target.workflowRunId, target.sessionName) : this.genericKey(target.sessionId) }
    workflowKey(workflowRunId: string, sessionName: string) { return `workflow:${workflowRunId}:${sessionName}` }
    genericKey(sessionId: string) { return `generic:${sessionId}` }
    get(key: string) { return this.sessions.get(key) }
    set(key: string, entry: { sessionId: string; workDir: string }) { return this.sessions.set(key, entry) }
    has(key: string) { return this.sessions.has(key) }
    delete(key: string) { return this.sessions.delete(key) }
  },
  createSharedAcpConnection: (...args: unknown[]) => createSharedAcpConnection(...args),
}))

beforeEach(() => {
  createSharedAcpConnection.mockResolvedValue({
    connection: {
      prompt: vi.fn(),
      cancel: vi.fn(),
      newSession: vi.fn(),
      resumeSession: vi.fn(),
      setSessionConfigOption: vi.fn(),
      closeSession: vi.fn(),
    },
    processPid: 99999,
    setSessionHandlers: vi.fn(),
    clearSessionHandlers: vi.fn(),
    shutdown: shutdownSharedAcpConnection,
  })
  acpShutdown.mockResolvedValue(undefined)
  shutdownSharedAcpConnection.mockResolvedValue(undefined)
  // Default implementation: write two rebase-tagged lines and resolve
  // with success — overridable per test.
  blockingAction.mockReset()
  blockingAction.mockImplementation(async ({ log }: { log?: { write: (source: string, text: string) => void } }) => {
    log?.write("action:rebase", "rebasing commit a1b2c3")
    log?.write("action:rebase", "Auto-merging src/lib/rebase.ts")
    return { status: "success", message: "ok", output: JSON.stringify({ rebase: "ok" }) }
  })
})

function buildHost() {
  return new RunnerHost({
    serverUrl: "http://localhost:3456",
    runnerId: "runner-test",
    runnerRoot: "/tmp/mohist-runner-host-task-log",
    pollIntervalMs: 1,
    heartbeatIntervalMs: 60_000,
    dispatchLivenessProbeIntervalMs: 60_000,
  })
}

function deferred() {
  let resolve!: () => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<void>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function workWith(overrides: Partial<{ workflowRunId: string; workId: string; uses: string; ownerKind: string; agentJobId: string }> = {}) {
  return {
    workflowRunId: "wf-336",
    workId: "work-336",
    workType: "task",
    uses: "test/log",
    ownerKind: "agent-job",
    agentJobId: "aj-336",
    variables: { workspace: { path: "/tmp/mohist-runner-host-task-log" } },
    ...overrides,
  }
}

// Drain microtasks until quiescence. Under fake timers the host's
// async chains (poll → executeAndReport → append → noteAppend → flush →
// upload) advance on microtask turns; a single `await Promise.resolve()`
// is not enough because some continuations schedule on later turns.
// Mirrors the helper in runner-host-cleanup-config.spec.ts so the two
// specs stay symmetric in how they drive the same `RunnerHost.run` loop.
async function flushMicrotasks() {
  await Promise.resolve()
  await Promise.resolve()
}

// Advance fake time in fixed steps, draining microtasks between ticks
// so fire-and-forget promises (heartbeat, runCleanupOnce, the host's
// per-work microtask chain) settle before the next timer fires. Using
// small steps (rather than one big jump) keeps ordering deterministic
// when several intervals share the clock.
async function flushCycles(ms: number, cycles: number) {
  for (let i = 0; i < cycles; i += 1) {
    await vi.advanceTimersByTimeAsync(ms)
    await flushMicrotasks()
  }
}

// Drain microtasks and advance fake timers until a predicate is satisfied.
// Used instead of vi.waitFor under fake timers because waitFor's internal
// polling timer is not advanced by advanceTimersByTimeAsync, which makes
// timer-dependent assertions order-dependent across specs.
async function drainUntil(predicate: () => boolean, label: string, maxTicks = 100) {
  for (let i = 0; i < maxTicks; i += 1) {
    if (predicate()) return
    await flushMicrotasks()
    if (predicate()) return
    await vi.advanceTimersByTimeAsync(1)
  }
  throw new Error(`drainUntil timed out waiting for: ${label}`)
}

describe("RunnerHost task-log best-effort flush (T-003)", () => {
  // Drive the host loop with fake timers so the assertions never depend
  // on wall-clock scheduling (project rule: tests must not rely on real
  // time). `vi.waitFor` is retained as a polling helper, but under fake
  // timers its `timeout` is a sentinel, not a real deadline — every
  // state transition is advanced deterministically by
  // `vi.advanceTimersByTimeAsync` + microtask flushes.
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it("UploadsIncrementalLogBeforeWorkCompletes", async () => {
    vi.clearAllMocks()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockResolvedValue({ accepted: 1, truncated: false })
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workId: "work-live", agentJobId: "aj-live" })]).mockImplementation(async () => [])
    const release = deferred()
    blockingAction.mockImplementationOnce(async ({ log }: { log?: { write: (source: string, text: string) => void } }) => {
      log?.write("action:test", "line before completion")
      await release.promise
      return { status: "success", message: "ok" }
    })

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-host-task-log-live",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
      taskLogFlushLineThreshold: 1,
      taskLogFlushIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    // Start the host loop (connect + first poll under fake timers).
    await vi.waitFor(() => expect(connect).toHaveBeenCalled(), { timeout: 5_000 })
    // Drive the poll cycle so the work item is dequeued and the
    // action's append → noteAppend → flush microtask chain runs.
    await flushCycles(1, 3)
    await vi.waitFor(() => expect(uploadTaskLog).toHaveBeenCalled(), { timeout: 5_000 })
    expect(report).not.toHaveBeenCalled()

    release.resolve()
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })
  })

  it("RoutesConcurrentIncrementalUploadsToEachWorkItemCollector", async () => {
    vi.clearAllMocks()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockResolvedValue({ accepted: 1, truncated: false })
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    // Dispatch both works in a single poll so they start concurrently.
    // Using one batch removes the dependency on a second poll cycle and
    // the race between timer advancement and action microtasks.
    poll
      .mockResolvedValueOnce([
        workWith({ workId: "work-A", agentJobId: "aj-A" }),
        workWith({ workId: "work-B", agentJobId: "aj-B" }),
      ])
      .mockImplementation(async () => [])
    const releases = new Map<string, ReturnType<typeof deferred>>()
    const gate = deferred()
    blockingAction.mockImplementation(async ({ workId, log }: { workId: string; log?: { write: (source: string, text: string) => void } }) => {
      const release = deferred()
      releases.set(workId, release)
      log?.write("action:test", `line for ${workId}`)
      // Block on a shared gate so the test can observe both incremental
      // flushes before either work completes.
      await gate.promise
      await release.promise
      return { status: "success", message: "ok" }
    })

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-host-task-log-concurrent",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
      taskLogFlushLineThreshold: 1,
      taskLogFlushIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    await vi.waitFor(() => expect(connect).toHaveBeenCalled(), { timeout: 5_000 })
    // Drain until both concurrent works have logged and their threshold-1
    // incremental flushes have been uploaded. drainUntil advances fake
    // timers and microtasks deterministically, avoiding the order-
    // dependence that made the previous flushCycles/vi.waitFor mix flaky.
    await drainUntil(() => uploadTaskLog.mock.calls.length >= 2, "two incremental uploads")

    for (const call of uploadTaskLog.mock.calls) {
      const [, workId, batch] = call as [string, string, { entries: Array<{ text: string }> }]
      const workLines = batch.entries.filter((entry) => entry.text.startsWith("line for "))
      for (const entry of workLines) {
        expect(entry.text).toBe(`line for ${workId}`)
      }
    }

    gate.resolve()
    releases.get("work-A")?.resolve()
    releases.get("work-B")?.resolve()
    await vi.waitFor(() => expect(report).toHaveBeenCalledTimes(2), { timeout: 5_000 })
    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })
  })

  it("FallbackExecutorPathStreamsIncrementalLogsBeforeReport", async () => {
    vi.clearAllMocks()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockResolvedValue({ accepted: 1, truncated: false })
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workId: "work-fallback", agentJobId: "aj-fallback" })]).mockImplementation(async () => [])
    const release = deferred()
    createSharedAcpConnection
      .mockRejectedValueOnce(new Error("shared ACP unavailable"))
      .mockResolvedValueOnce({
        connection: {
          prompt: vi.fn(),
          cancel: vi.fn(),
          newSession: vi.fn(),
          resumeSession: vi.fn(),
          setSessionConfigOption: vi.fn(),
          closeSession: vi.fn(),
        },
        processPid: 99999,
        setSessionHandlers: vi.fn(),
        clearSessionHandlers: vi.fn(),
        shutdown: shutdownSharedAcpConnection,
      })
    blockingAction.mockImplementationOnce(async ({ log }: { log?: { write: (source: string, text: string) => void } }) => {
      log?.write("action:test", "fallback live line")
      await release.promise
      return { status: "success", message: "ok" }
    })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-host-task-log-fallback",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
      taskLogFlushLineThreshold: 1,
      taskLogFlushIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    await vi.waitFor(() => expect(connect).toHaveBeenCalled(), { timeout: 5_000 })
    await flushCycles(1, 3)
    await vi.waitFor(() => expect(uploadTaskLog).toHaveBeenCalled(), { timeout: 5_000 })
    expect(report).not.toHaveBeenCalled()

    release.resolve()
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })
    errorSpy.mockRestore()
  })

  it("FlushesCapturedLogViaUploadTaskLogBeforeReport", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockResolvedValue({ accepted: 2, truncated: false })
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith()]).mockImplementation(async () => [])

    const controller = new AbortController()
    const host = buildHost()
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled(), { timeout: 5_000 })
    // The default action writes two rebase lines and resolves; the
    // terminal flush + report then run as a microtask chain after the
    // poll dequeues the work item.
    await flushCycles(1, 3)
    await vi.waitFor(() => expect(uploadTaskLog).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })

    const uploadCall = uploadTaskLog.mock.calls[0] as [string, string, { entries: Array<{ source: string; text: string }>; truncated: boolean }]
    expect(uploadCall[0]).toBe("aj-336")
    expect(uploadCall[1]).toBe("work-336")
    expect(report).toHaveBeenCalledTimes(1)
    // The flush call must precede the report call so the verdict
    // round-trip never carries a flushed log (design D6).
    const uploadIdx = uploadTaskLog.mock.invocationCallOrder[0]!
    const reportIdx = report.mock.invocationCallOrder[0]!
    expect(uploadIdx).toBeLessThan(reportIdx)
    const rebaseEntries = uploadCall[2].entries.filter((e) => e.source === "action:rebase")
    expect(rebaseEntries.length).toBeGreaterThanOrEqual(2)
    expect(rebaseEntries.map((e) => e.text)).toContain("rebasing commit a1b2c3")
  })

  it("FailedUploadIsLoggedAndSwallowed_ReportStillSucceeds", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    // With incremental flushing the FIRST upload may be the increment
    // (depending on the trigger fire ordering); the test mocks a
    // rejection on the first call and the terminal call resolves via
    // the default mock. Both failures / successes are best-effort and
    // never block the report (design D1 / D6).
    uploadTaskLog.mockRejectedValueOnce(new Error("server returned 500"))
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workflowRunId: "wf-fail", workId: "work-fail", agentJobId: "aj-fail" })]).mockImplementation(async () => [])
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-host-task-log-fail",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled(), { timeout: 5_000 })
    await flushCycles(1, 3)
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })

    expect(uploadTaskLog).toHaveBeenCalled()
    expect(report).toHaveBeenCalledTimes(1)
    // At least one upload error must have been logged. The label is
    // "incremental" or "terminal" — both contain "upload failed for work".
    expect(errorSpy.mock.calls.some((call) => call.some((arg) => typeof arg === "string" && arg.includes("upload failed for work")))).toBe(true)

    errorSpy.mockRestore()
  })

  it("PendingUploadIsTimedOut_ReportStillSucceeds", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockImplementationOnce(() => new Promise(() => undefined))
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workflowRunId: "wf-pending", workId: "work-pending", agentJobId: "aj-pending" })]).mockImplementation(async () => [])
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    const controller = new AbortController()
    const host = buildHost()
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled(), { timeout: 5_000 })
    // Dequeue the work item; the terminal flush then races a forever-
    // pending upload against the 250ms terminal-timeout guard. Under
    // fake timers that guard NEVER fires on its own — advance past it
    // explicitly so the upload rejects, the error is logged, and the
    // report is reached.
    await flushCycles(1, 3)
    await vi.advanceTimersByTimeAsync(250)
    await flushMicrotasks()
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })

    expect(uploadTaskLog).toHaveBeenCalled()
    expect(report).toHaveBeenCalledTimes(1)
    expect(errorSpy.mock.calls.some((call) => call.some((arg) => typeof arg === "string" && arg.includes("upload failed for work")))).toBe(true)

    errorSpy.mockRestore()
  })

  it("ReportCarriesTheVerdict_WhenLogUploadSucceeds", async () => {
    vi.clearAllMocks()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockResolvedValue({ accepted: 1, truncated: false })
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    blockingAction.mockImplementationOnce(async ({ log }: { signal: AbortSignal; log?: { write: (source: string, text: string) => void } }) => {
      log?.write("action:rebase", "final line")
      return { status: "failed", message: "boom" }
    })
    poll.mockResolvedValueOnce([workWith({ workflowRunId: "wf-verdict", workId: "work-verdict", agentJobId: "aj-verdict" })]).mockImplementation(async () => [])

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-host-task-log-verdict",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled(), { timeout: 5_000 })
    await flushCycles(1, 3)
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await vi.waitFor(() => expect(run).resolves.toBeUndefined(), { timeout: 5_000 })

    const reportCall = report.mock.calls[0] as [unknown, { status: string; message: string }]
    expect(reportCall[1].status).toBe("failed")
    expect(reportCall[1].message).toBe("boom")
  })
})

describe("TaskLogCollector incremental flush integration (T-003 Phase 2)", () => {
  // These tests exercise the `drain` + watermark + flush trigger
  // contract end-to-end through the host's `executeAndReport`. They
  // use a stub collector + flush helper instead of running a real
  // work item, so the assertions are not entangled with branch-stability
  // git() writes or workspace-prep output.

  it("IncrementalFlushFiresBeforeCompletion_WhenIntervalElapsesDuringWork", async () => {
    vi.useFakeTimers()
    const uploadBatches: Array<Array<{ seq: number }>> = []
    uploadTaskLog.mockImplementation(async (_ownerId: string, _workId: string, batch: { entries: Array<{ seq: number }> }) => {
      uploadBatches.push(batch.entries.map((e) => ({ seq: e.seq })))
      return { accepted: batch.entries.length, truncated: false }
    })

    // Drive the trigger manually with a real collector + a fake
    // helper that mirrors what `executeAndReport` wires up. This
    // verifies the trigger timing without depending on the executor's
    // git() side-effects.
    const collector = new (await import("../src/runtime/task-log.js")).TaskLogCollector()
    const flushCalls: Array<Array<{ seq: number }>> = []
    const flushIncremental = () => {
      const batch = collector.drain()
      if (batch === null) return
      flushCalls.push(batch.entries.map((e) => ({ seq: e.seq })))
    }
    const trigger = startTaskLogFlushTriggerForTest(flushIncremental, 60, 1_000)

    collector.append("a", "1")
    await vi.advanceTimersByTimeAsync(120)
    collector.append("a", "2")
    await vi.advanceTimersByTimeAsync(120)
    await trigger.stop()

    expect(flushCalls.length).toBeGreaterThanOrEqual(2)
    // Each incremental carries only the NEW lines since the last drain.
    const first = flushCalls[0]!
    expect(first.map((e) => e.seq)).toEqual([1])
    const second = flushCalls[1]!
    expect(second.map((e) => e.seq)).toEqual([2])
    // No duplicates across increments.
    const seenSeqs = new Set<number>()
    for (const batch of flushCalls) {
      for (const entry of batch) {
        expect(seenSeqs.has(entry.seq)).toBe(false)
        seenSeqs.add(entry.seq)
      }
    }
    vi.useRealTimers()
  })

  it("WatermarkExcludesAlreadySent_IncrementalBatchCarriesOnlyNewLines", async () => {
    vi.useFakeTimers()
    const collector = new (await import("../src/runtime/task-log.js")).TaskLogCollector()
    const drainedSeqs: Array<number[]> = []
    const flushIncremental = () => {
      const batch = collector.drain()
      if (batch === null) return
      drainedSeqs.push(batch.entries.map((e) => e.seq))
    }
    const trigger = startTaskLogFlushTriggerForTest(flushIncremental, 60, 1_000)

    collector.append("a", "1")
    collector.append("a", "2")
    await vi.advanceTimersByTimeAsync(80)
    collector.append("a", "3")
    collector.append("a", "4")
    await vi.advanceTimersByTimeAsync(80)
    collector.append("a", "5")
    await vi.advanceTimersByTimeAsync(80)
    await trigger.stop()

    expect(drainedSeqs[0]).toEqual([1, 2])
    expect(drainedSeqs[1]).toEqual([3, 4])
    expect(drainedSeqs[2]).toEqual([5])
    // Confirm no seq repeats.
    const flat = drainedSeqs.flat()
    expect(flat).toEqual([...new Set(flat)].sort((a, b) => a - b))
    vi.useRealTimers()
  })

  it("EmptyDrainProducesNoUpload_QuietPeriodSkipsNetworkRoundTrip", async () => {
    vi.useFakeTimers()
    const flushCalls = vi.fn()
    const collector = new (await import("../src/runtime/task-log.js")).TaskLogCollector()
    const flushIncremental = () => {
      const batch = collector.drain()
      if (batch === null) return
      flushCalls(batch)
    }
    const trigger = startTaskLogFlushTriggerForTest(flushIncremental, 60, 1_000)

    await vi.advanceTimersByTimeAsync(300)
    await trigger.stop()

    // The trigger fired multiple times, but the host-side upload
    // helper is invoked only when the drain returns non-null.
    // Here we count it via the wrapper that did fire — we instead
    // assert that the underlying drain returned null at every tick.
    expect(flushCalls).not.toHaveBeenCalled()
    // pendingSinceWatermark must still be 0 (no appends).
    expect(collector.pendingSinceWatermark()).toBe(0)
    vi.useRealTimers()
  })

  it("LineCountThresholdFiresEagerly_BeforeNextIntervalTick", async () => {
    vi.useFakeTimers()
    const collector = new (await import("../src/runtime/task-log.js")).TaskLogCollector()
    const drainedSeqs: Array<number[]> = []
    const flushIncremental = () => {
      const batch = collector.drain()
      if (batch === null) return
      drainedSeqs.push(batch.entries.map((e) => e.seq))
    }
    // Long interval, low threshold — the threshold path drives the fire.
    const trigger = startTaskLogFlushTriggerForTest(flushIncremental, 10_000, 3)
    const listener = () => trigger.noteAppend()
    collector.setAppendListener(listener)

    collector.append("a", "1")
    collector.append("a", "2")
    // Two appends is below the threshold — no eager fire yet.
    expect(drainedSeqs).toEqual([])
    collector.append("a", "3")
    // Threshold reached — eager fire.
    expect(drainedSeqs.length).toBeGreaterThanOrEqual(1)
    expect(drainedSeqs[0]).toEqual([1, 2, 3])

    collector.setAppendListener(null)
    await trigger.stop()
    vi.useRealTimers()
  })

  it("FailedIncrementalUploadIsReconciledByTerminalBatch_AuthoritativeStoreRecovers", async () => {
    vi.useFakeTimers()
    const collector = new (await import("../src/runtime/task-log.js")).TaskLogCollector()
    const uploadBatches: Array<Array<{ seq: number }>> = []
    const errors: unknown[] = []
    const uploadIncremental = async () => {
      const batch = collector.drain()
      if (batch === null) return
      try {
        throw new Error("server returned 500")
      } catch (error) {
        errors.push(error)
      }
    }
    const uploadTerminal = async () => {
      const batch = collector.flush()
      uploadBatches.push(batch.entries.map((e) => ({ seq: e.seq })))
    }

    collector.append("a", "1")
    collector.append("a", "2")
    await uploadIncremental()
    expect(errors).toHaveLength(1)

    await uploadTerminal()
    const terminalBatch = uploadBatches[0]!
    expect(terminalBatch.map((e) => e.seq)).toEqual([1, 2])
    vi.useRealTimers()
  })

  it("FlushTriggerIsStoppedBeforeTerminalFlush_NoLateIncrementalFiresAfterCompletion", async () => {
    vi.useFakeTimers()
    const collector = new (await import("../src/runtime/task-log.js")).TaskLogCollector()
    const drainedSeqs: Array<number[]> = []
    const flushIncremental = () => {
      const batch = collector.drain()
      if (batch === null) return
      drainedSeqs.push(batch.entries.map((e) => e.seq))
    }
    const trigger = startTaskLogFlushTriggerForTest(flushIncremental, 60, 1_000)
    collector.append("a", "1")
    await vi.advanceTimersByTimeAsync(80)
    // Stop BEFORE the next tick — no further fires.
    await trigger.stop()
    const beforeCount = drainedSeqs.length
    await vi.advanceTimersByTimeAsync(500)
    expect(drainedSeqs.length).toBe(beforeCount)
    vi.useRealTimers()
  })

  it("IncrementalFlushesAreSerialized_WhenTriggerFiresDuringInFlightUpload", async () => {
    const resolvers: Array<() => void> = []
    let active = 0
    let maxActive = 0
    const flushIncremental = vi.fn(() => new Promise<void>((resolve) => {
      active += 1
      maxActive = Math.max(maxActive, active)
      resolvers.push(() => {
        active -= 1
        resolve()
      })
    }))

    const trigger = startTaskLogFlushTriggerForTest(flushIncremental, 10_000, 1)

    trigger.noteAppend()
    trigger.noteAppend()
    trigger.noteAppend()

    expect(flushIncremental).toHaveBeenCalledTimes(1)
    expect(maxActive).toBe(1)

    resolvers.shift()?.()
    await vi.waitFor(() => expect(flushIncremental).toHaveBeenCalledTimes(2))
    expect(maxActive).toBe(1)

    resolvers.shift()?.()
    await trigger.stop()
    expect(active).toBe(0)
  })

  it("FlushTriggerTimingIsDrivenByFakeTimers_NoWallClock", async () => {
    vi.useFakeTimers()
    const collector = new (await import("../src/runtime/task-log.js")).TaskLogCollector()
    let drainCount = 0
    const flushIncremental = () => {
      const batch = collector.drain()
      if (batch === null) return
      drainCount += 1
    }
    const trigger = startTaskLogFlushTriggerForTest(flushIncremental, 100, 1_000)
    // No appends — every tick still calls the flush callback, which
    // is a no-op (drain returns null). After 250 ms three ticks fire.
    await vi.advanceTimersByTimeAsync(250)
    expect(drainCount).toBe(0)
    // Add a line and tick again — it gets drained.
    collector.append("a", "1")
    await vi.advanceTimersByTimeAsync(110)
    expect(drainCount).toBe(1)
    await trigger.stop()
    vi.useRealTimers()
  })
})

// Re-export the host's private trigger so these unit tests can drive
// the same code path the host uses (rather than reimplementing the
// setInterval dance). The host deliberately keeps this symbol private
// to its module — the test reaches it through a thin re-export kept
// in the host module itself for testability.
import { startTaskLogFlushTrigger as startTaskLogFlushTriggerForTest } from "../src/runtime/host.js"
