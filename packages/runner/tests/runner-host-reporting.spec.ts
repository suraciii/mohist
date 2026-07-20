import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/server/session-target.js"
import { deferred } from "./support/deferred.js"
import { clearOpenCodeRuntimeFactoryForTest, installReadyOpenCodeRuntimeFactory } from "./support/opencode-runtime-factory.js"

const installReadyRuntimeFactory = installReadyOpenCodeRuntimeFactory

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000
const AWAITING_ACK_RETRY_INTERVAL_MS = 5_000

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
} = mocks

let capturedOnReconnected: ((connectionId: string) => void) | null = null
let capturedFollowupTargetResolver: ((target: SessionTarget) => { runtimeSessionId: string; workDir: string; projectId: string } | null) | null = null

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

beforeEach(() => {
  vi.useFakeTimers()
  installReadyRuntimeFactory()
  capturedOnReconnected = null
  capturedFollowupTargetResolver = null
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
  it("PollBody_CarriesInFlightAndAwaitingAck_Keys", async () => {
    vi.clearAllMocks()
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    const secondPollStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockImplementation(async () => {
      reportStarted.resolve()
      await reportRelease.promise
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const held = {
      workflowRunId: "wr-held",
      workId: "work-held",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    }
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollIndex += 1
      if (pollIndex === 1) return [held]
      secondPollStarted.resolve()
      return []
    })
    blockingAction.mockResolvedValue({ status: "success", message: "ok" })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const run = host.run(controller.signal)

    try {
      await reportStarted.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await secondPollStarted.promise

      const bodies = poll.mock.calls
        .filter((calls) => calls.length > 1 && calls[1])
        .map((calls) => calls[1] as { inFlight: string[]; awaitingAck: string[] })
      expect(bodies.some((body) => body.awaitingAck.includes("workflow:wr-held:work-held"))).toBe(true)

      controller.abort()
      reportRelease.resolve()
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      reportRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it("ReDispatchedWork_ReportedOnce_NotPerRedelivery", async () => {
    vi.clearAllMocks()
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    const pollCalls = [deferred<void>(), deferred<void>(), deferred<void>(), deferred<void>()]
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockImplementation(async () => {
      reportStarted.resolve()
      await reportRelease.promise
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const same = {
      workflowRunId: "wr-dup",
      workId: "work-dup",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    }
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollCalls[pollIndex]?.resolve()
      pollIndex += 1
      return pollIndex <= 3 ? [same] : []
    })
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    blockingAction.mockResolvedValue({ status: "success", message: "ok" })
    const run = host.run(controller.signal)

    try {
      await reportStarted.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[1]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[2]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[3]!.promise

      const reportsForDup = report.mock.calls.filter((calls) => calls[0]?.workId === "work-dup")
      expect(reportsForDup.length).toBeLessThanOrEqual(1)

      controller.abort()
      reportRelease.resolve()
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      reportRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it("AwaitingAck_RetriesReportUntilAcked", async () => {
    vi.clearAllMocks()
    const firstReport = deferred<void>()
    const secondReport = deferred<void>()
    const thirdReport = deferred<void>()
    const firstFailureLogged = deferred<void>()
    const secondFailureLogged = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    const firstFailure = new Error("first transient")
    const secondFailure = new Error("second transient")
    let attempt = 0
    report.mockImplementation(async () => {
      attempt += 1
      if (attempt === 1) {
        firstReport.resolve()
        throw firstFailure
      }
      if (attempt === 2) {
        secondReport.resolve()
        throw secondFailure
      }
      thirdReport.resolve()
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const work = {
      workflowRunId: "wr-retry",
      workId: "work-retry",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    }
    poll.mockResolvedValueOnce([work]).mockResolvedValue([])
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    blockingAction.mockResolvedValue({ status: "success", message: "ok" })
    const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation((message: unknown) => {
      if (message === "first report for work work-retry failed; will retry") firstFailureLogged.resolve()
      if (message === "retry report for work work-retry failed (attempt 2)") secondFailureLogged.resolve()
    })
    const run = host.run(controller.signal)
    try {
      await firstReport.promise
      await firstFailureLogged.promise
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await secondReport.promise
      await secondFailureLogged.promise
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await thirdReport.promise

      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(uploadTaskLog).toHaveBeenCalledTimes(1)
      expect(warningSpy).toHaveBeenCalledTimes(2)
      expect(warningSpy).toHaveBeenNthCalledWith(
        1,
        "first report for work work-retry failed; will retry",
        firstFailure,
      )
      expect(warningSpy).toHaveBeenNthCalledWith(
        2,
        "retry report for work work-retry failed (attempt 2)",
        secondFailure,
      )
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      warningSpy.mockRestore()
    }
  })
})
