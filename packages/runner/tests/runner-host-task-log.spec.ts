import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerHost, startTaskLogFlushTrigger } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/server/session-target.js"
import type { GitRunner } from "../src/runtime/git-probe.js"
import { deferred, type Deferred } from "./support/deferred.js"
import { capturedLogs } from "./support/logger-test.js"
import { FakeTerminalTaskLogDeliveryStore } from "./support/terminal-task-log-delivery.js"
import { installReadyOpenCodeRuntimeFactory } from "./support/opencode-runtime-factory.js"
import { createDefaultRunnerTestResources, withTestRunnerResources } from "./support/test-resources.js"
import { taskLogWork as workWith } from "./support/runner-host-task-log-fixture.js"

const nonGitRunner: GitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: "",
  stderr: "not a git repository",
  combinedOutput: "not a git repository",
})

type TaskLogMock = ReturnType<typeof vi.fn>
type TaskLogMocks = Record<
  "connect" | "heartbeat" | "disconnect" | "poll" | "report" | "uploadTaskLog" | "fetchConfig" |
  "startSignalR" | "stopSignalR" | "getConnectionId" | "probeLiveness" | "blockingAction" | "forceReconnect",
  TaskLogMock
>

interface TaskLogTestState {
  readonly mocks: TaskLogMocks
}

const taskLogMockStorage = new AsyncLocalStorage<TaskLogTestState>()

function currentTaskLogTestState(): TaskLogTestState {
  const state = taskLogMockStorage.getStore()
  if (!state) throw new Error("runner host task log test context is not active")
  return state
}

function scopedMock(name: keyof TaskLogMocks): TaskLogMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, "_isMockFunction", { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentTaskLogTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentTaskLogTestState().mocks[name], property)
      return typeof value === "function" ? value.bind(currentTaskLogTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentTaskLogTestState().mocks[name], property, value)
    },
  }) as unknown as TaskLogMock
}

const connect = scopedMock("connect")
const heartbeat = scopedMock("heartbeat")
const disconnect = scopedMock("disconnect")
const poll = scopedMock("poll")
const report = scopedMock("report")
const uploadTaskLog = scopedMock("uploadTaskLog")
const fetchConfig = scopedMock("fetchConfig")
const startSignalR = scopedMock("startSignalR")
const stopSignalR = scopedMock("stopSignalR")
const getConnectionId = scopedMock("getConnectionId")
const probeLiveness = scopedMock("probeLiveness")
const blockingAction = scopedMock("blockingAction")
const forceReconnect = scopedMock("forceReconnect")

function createTaskLogMocks(): TaskLogMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => ({})),
    uploadTaskLog: vi.fn(async () => ({ status: "changed", accepted: 0, truncated: false })),
    fetchConfig: vi.fn(async () => null),
    startSignalR: vi.fn(async () => undefined),
    stopSignalR: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => "conn-1"),
    probeLiveness: vi.fn(async () => true),
    blockingAction: vi.fn(async (_inputs: unknown, { log }: { log?: { write: (source: string, text: string) => void } }) => {
      log?.write("action:rebase", "rebasing commit a1b2c3")
      log?.write("action:rebase", "Auto-merging src/lib/rebase.ts")
      return { output: { rebase: "ok" } }
    }),
    forceReconnect: vi.fn(async () => undefined),
  }
}

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

vi.mock("../src/actions/registry.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/actions/registry.js")>()
  const definition = (name: string) => ({
    manifest: {
      name,
      inputs: {
        workId: { types: ["string"] as const },
      },
      outputs: [],
      errors: [{ code: "action-failed", description: "The test Action failed" }],
    },
    run: blockingAction as never,
  })
  return {
    ...actual,
    createDefaultRegistry: () => new actual.ActionRegistry([
      definition("test/block"),
      definition("test/log"),
    ]),
  }
})

vi.mock("../src/runtime/workspace.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/runtime/workspace.js")>()
  return { ...actual, WorkspaceManager: class {
    async prepare() { return { path: "/virtual/mohist-runner-host-task-log", branch: "main", changeDir: null } }
    async verify() { return { path: "/virtual/mohist-runner-host-task-log", branch: "main", changeDir: null } }
  } }
})

function buildHost() {
  return new RunnerHost({
    serverUrl: "https://runner.test",
    runnerId: "runner-test",
    runnerRoot: "/virtual/mohist-runner-host-task-log",
    pollIntervalMs: 1,
    heartbeatIntervalMs: 60_000,
    dispatchLivenessProbeIntervalMs: 60_000,
  })
}

function it(name: string, body: () => Promise<void>): void {
  vitestIt(name, async () => {
    const resources = createDefaultRunnerTestResources()
    resources.gitRunner = nonGitRunner
    await taskLogMockStorage.run({ mocks: createTaskLogMocks() }, async () => {
      await withTestRunnerResources(async () => {
        installReadyOpenCodeRuntimeFactory(resources)
        vi.useFakeTimers()
        try {
          await body()
        } finally {
          vi.useRealTimers()
        }
      }, resources)
    })
  })
}

describe("RunnerHost flushes task logs before reporting work", () => {
  it("UploadsIncrementalLogBeforeWorkCompletes", async () => {
    const actionStarted = deferred<void>()
    const uploadStarted = deferred<void>()
    const reportStarted = deferred<void>()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockImplementation(async () => {
      uploadStarted.resolve()
      return { status: "changed", accepted: 1, truncated: false }
    })
    report.mockImplementation(async () => {
      reportStarted.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workId: "work-live", agentJobId: "aj-live" })]).mockImplementation(async () => [])
    const release = deferred()
    blockingAction.mockImplementationOnce(async (_inputs: unknown, { log }: { log?: { write: (source: string, text: string) => void } }) => {
      log?.write("action:test", "line before completion")
      actionStarted.resolve()
      await release.promise
      return { output: { ok: true } }
    })

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-host-task-log-live",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
      taskLogFlushLineThreshold: 1,
      taskLogFlushIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    await actionStarted.promise
    await uploadStarted.promise
    expect(report).not.toHaveBeenCalled()

    release.resolve()
    await reportStarted.promise
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("RoutesConcurrentIncrementalUploadsToEachWorkItemCollector", async () => {
    const actionsStarted = new Map<string, Deferred<void>>([
      ["work-A", deferred<void>()],
      ["work-B", deferred<void>()],
    ])
    const uploadsStarted = new Map<string, Deferred<void>>([
      ["work-A", deferred<void>()],
      ["work-B", deferred<void>()],
    ])
    const reportsStarted = new Map<string, Deferred<void>>([
      ["work-A", deferred<void>()],
      ["work-B", deferred<void>()],
    ])
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockImplementation(async (_ownerId: string, workId: string) => {
      uploadsStarted.get(workId)?.resolve()
      return { status: "changed", accepted: 1, truncated: false }
    })
    report.mockImplementation(async (work: { workId: string }) => {
      reportsStarted.get(work.workId)?.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    // Dispatch both works in a single poll so they start concurrently.
    // Using one batch removes the dependency on a second poll cycle and
    // the race between timer advancement and action microtasks.
    poll
      .mockResolvedValueOnce([
        workWith({ workId: "work-B", agentJobId: "aj-B", actionWorkId: "work-B" }),
        workWith({ workId: "work-A", agentJobId: "aj-A", actionWorkId: "work-A" }),
      ])
      .mockImplementation(async () => [])
    const releases = new Map<string, Deferred<void>>()
    const gate = deferred()
    blockingAction.mockImplementation(async (inputs: { workId: string }, { log }: { log?: { write: (source: string, text: string) => void } }) => {
      const { workId } = inputs
      const release = deferred()
      releases.set(workId, release)
      log?.write("action:test", `line for ${workId}`)
      actionsStarted.get(workId)?.resolve()
      // Block on a shared gate so the test can observe both incremental
      // flushes before either work completes.
      await gate.promise
      await release.promise
      return { output: { ok: true } }
    })

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-host-task-log-concurrent",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
      taskLogFlushLineThreshold: 1,
      taskLogFlushIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)

    await Promise.all([...actionsStarted.values()].map(({ promise }) => promise))
    await Promise.all([...uploadsStarted.values()].map(({ promise }) => promise))
    expect(new Set(uploadTaskLog.mock.calls.map((call) => call[1]))).toEqual(new Set(["work-A", "work-B"]))

    for (const call of uploadTaskLog.mock.calls) {
      const [, workId, batch] = call as [string, string, { entries: Array<{ text: string }> }]
      const workLines = batch.entries.filter((entry) => entry.text.startsWith("line for "))
      for (const entry of workLines) {
        expect(entry.text).toBe(`line for ${workId}`)
      }
    }

    gate.resolve()
    releases.get("work-A")!.resolve()
    releases.get("work-B")!.resolve()
    await Promise.all([...reportsStarted.values()].map(({ promise }) => promise))
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("FlushesCapturedLogViaUploadTaskLogBeforeReport", async () => {
    const uploadStarted = deferred<void>()
    const reportStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockImplementation(async () => {
      uploadStarted.resolve()
      return { status: "changed", accepted: 2, truncated: false }
    })
    report.mockImplementation(async () => {
      reportStarted.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith()]).mockImplementation(async () => [])

    const controller = new AbortController()
    const host = buildHost()
    const run = host.run(controller.signal)
    await uploadStarted.promise
    await reportStarted.promise
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    const uploadCall = uploadTaskLog.mock.calls[0] as [string, string, { entries: Array<{ source: string; text: string }>; truncated: boolean }]
    expect(uploadCall[0]).toBe("wf-336")
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

  it("FailedTerminalUploadIsDurable_ReportStillSucceeds", async () => {
    vi.clearAllMocks()
    const reportStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog
      .mockRejectedValueOnce(new Error("server returned 500"))
      .mockResolvedValue({ status: "changed", accepted: 1, truncated: false })
    report.mockImplementation(async () => {
      reportStarted.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workflowRunId: "wf-fail", workId: "work-fail", agentJobId: "aj-fail" })]).mockImplementation(async () => [])

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-host-task-log-fail",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    await reportStarted.promise
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(uploadTaskLog).toHaveBeenCalled()
    expect(report).toHaveBeenCalledTimes(1)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "WARN", message: "terminal task-log delivery will recover" }),
    ]))
  })

  it("PendingTerminalUploadIsTimedOut_ReportStillSucceeds", async () => {
    vi.clearAllMocks()
    const uploadStarted = deferred<void>()
    const pendingUpload = deferred<void>()
    const reportStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockImplementationOnce(() => {
      uploadStarted.resolve()
      return pendingUpload.promise
    })
    report.mockImplementation(async () => {
      reportStarted.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workflowRunId: "wf-pending", workId: "work-pending", agentJobId: "aj-pending" })]).mockImplementation(async () => [])

    const controller = new AbortController()
    const host = buildHost()
    const run = host.run(controller.signal)
    await uploadStarted.promise
    await vi.advanceTimersByTimeAsync(250)
    await reportStarted.promise
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(uploadTaskLog).toHaveBeenCalled()
    expect(report).toHaveBeenCalledTimes(1)
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "WARN", message: "terminal task-log delivery will recover" }),
    ]))
  })

  it("ReportCarriesTheVerdict_WhenLogUploadSucceeds", async () => {
    const reportStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockResolvedValue({ status: "changed", accepted: 1, truncated: false })
    report.mockImplementation(async () => {
      reportStarted.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    blockingAction.mockImplementationOnce(async ({ log }: { signal: AbortSignal; log?: { write: (source: string, text: string) => void } }) => {
      log?.write("action:rebase", "final line")
      return { error: { code: "action-failed", message: "boom" } }
    })
    poll.mockResolvedValueOnce([workWith({ workflowRunId: "wf-verdict", workId: "work-verdict", agentJobId: "aj-verdict" })]).mockImplementation(async () => [])

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-host-task-log-verdict",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    await reportStarted.promise
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    const reportCall = report.mock.calls[0] as [unknown, { status: string; message: string }]
    expect(reportCall[1].status).toBe("failed")
    expect(reportCall[1].message).toBe("boom")
  })

  it("PersistsTerminalSnapshotBeforeUploadAndRemovesItOnlyAfterAck", async () => {
    vi.clearAllMocks()
    const store = new FakeTerminalTaskLogDeliveryStore()
    const uploadStarted = deferred<void>()
    const reportStarted = deferred<void>()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    uploadTaskLog.mockImplementation(async (_ownerId: string, workId: string, batch: { entries: Array<{ text: string }> }, _signal: AbortSignal, _ownerKind: string, terminal: boolean) => {
      expect(terminal).toBe(true)
      expect(workId).toBe("work-durable")
      expect(store.records.size).toBe(1)
      expect(batch.entries.length).toBeGreaterThan(0)
      uploadStarted.resolve()
      return { status: "changed", accepted: batch.entries.length, truncated: false }
    })
    report.mockImplementation(async () => {
      reportStarted.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce([workWith({ workflowRunId: "wf-durable", workId: "work-durable" })]).mockImplementation(async () => [])

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-host-task-log-durable",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }, undefined, { terminalTaskLogDelivery: store })
    const run = host.run(controller.signal)
    await uploadStarted.promise
    await reportStarted.promise
    expect(store.records.size).toBe(0)
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("ConflictIsTerminalForOneWorkAndDoesNotBlockAnotherPendingDelivery", async () => {
    vi.clearAllMocks()
    const store = new FakeTerminalTaskLogDeliveryStore()
    await store.load()
    const batch = { entries: [{ seq: 1, timestamp: new Date("2026-08-11T00:00:00.000Z"), source: "action", text: "terminal" }], truncated: false }
    await store.putPending({ identity: { ownerKind: "workflow", ownerId: "wf-conflict", workId: "work-conflict" }, batch })
    await store.putPending({ identity: { ownerKind: "workflow", ownerId: "wf-other", workId: "work-other" }, batch })
    const allUploads = deferred<void>()
    let calls = 0
    uploadTaskLog.mockImplementation(async (_ownerId: string, workId: string) => {
      calls += 1
      if (calls === 2) allUploads.resolve()
      if (workId === "work-conflict") {
        const error = new Error("sealed content differs") as Error & { status: number; code: string }
        error.status = 409
        error.code = "terminal_snapshot_conflict"
        throw error
      }
      return { status: "duplicate", accepted: 1, truncated: false }
    })
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValue([])

    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-host-task-log-conflict",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }, undefined, { terminalTaskLogDelivery: store })
    const run = host.run(controller.signal)
    await allUploads.promise
    await Promise.resolve()
    await Promise.resolve()
    expect(calls).toBe(2)
    expect(store.records.get("workflow:wf-conflict:work-conflict")?.state).toBe("failed")
    expect(store.records.has("workflow:wf-other:work-other")).toBe(false)
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })
})

describe("TaskLogCollector incremental flush through RunnerHost", () => {
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
      return { status: "changed", accepted: batch.entries.length, truncated: false }
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
    const trigger = startTaskLogFlushTrigger(flushIncremental, 60, 1_000)

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
    const trigger = startTaskLogFlushTrigger(flushIncremental, 60, 1_000)

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
    const trigger = startTaskLogFlushTrigger(flushIncremental, 60, 1_000)

    await vi.advanceTimersByTimeAsync(300)
    await trigger.stop()

    // The trigger fired multiple times, but the host-side upload
    // helper is invoked only when the drain returns non-null.
    // Here we count it via the wrapper that did fire — we instead
    // assert that the underlying drain returned null at every tick.
    expect(flushCalls).not.toHaveBeenCalled()
    // pendingSinceWatermark must still be 0 (no appends).
    expect(collector.pendingSinceWatermark()).toBe(0)
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
    const trigger = startTaskLogFlushTrigger(flushIncremental, 10_000, 3)
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
    const trigger = startTaskLogFlushTrigger(flushIncremental, 60, 1_000)
    collector.append("a", "1")
    await vi.advanceTimersByTimeAsync(80)
    // Stop BEFORE the next tick — no further fires.
    await trigger.stop()
    const beforeCount = drainedSeqs.length
    await vi.advanceTimersByTimeAsync(500)
    expect(drainedSeqs.length).toBe(beforeCount)
  })

  it("IncrementalFlushesAreSerialized_WhenTriggerFiresDuringInFlightUpload", async () => {
    vi.useFakeTimers()
    const secondFlushStarted = deferred<void>()
    const releases: Deferred<void>[] = []
    let active = 0
    let maxActive = 0
    const flushIncremental = vi.fn(() => {
      active += 1
      maxActive = Math.max(maxActive, active)
      if (flushIncremental.mock.calls.length === 2) secondFlushStarted.resolve()
      const release = deferred<void>()
      releases.push(release)
      return release.promise.finally(() => {
        active -= 1
      })
    })

    const trigger = startTaskLogFlushTrigger(flushIncremental, 10_000, 1)

    trigger.noteAppend()
    trigger.noteAppend()
    trigger.noteAppend()

    expect(flushIncremental).toHaveBeenCalledTimes(1)
    expect(maxActive).toBe(1)

    releases.shift()?.resolve()
    await secondFlushStarted.promise
    expect(maxActive).toBe(1)

    releases.shift()?.resolve()
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
    const trigger = startTaskLogFlushTrigger(flushIncremental, 100, 1_000)
    // No appends — every tick still calls the flush callback, which
    // is a no-op (drain returns null). After 250 ms three ticks fire.
    await vi.advanceTimersByTimeAsync(250)
    expect(drainCount).toBe(0)
    // Add a line and tick again — it gets drained.
    collector.append("a", "1")
    await vi.advanceTimersByTimeAsync(110)
    expect(drainCount).toBe(1)
    await trigger.stop()
  })
})
