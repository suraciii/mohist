import { beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"

const mocks = vi.hoisted(() => ({
  connect: vi.fn(),
  heartbeat: vi.fn(),
  disconnect: vi.fn(),
  poll: vi.fn(),
  report: vi.fn(),
  uploadTaskLog: vi.fn(),
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
    getLastCleanupPolicy = () => null
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

describe("RunnerHost task-log best-effort flush (T-003)", () => {
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
    poll.mockResolvedValueOnce(workWith()).mockImplementation(async () => null)

    const controller = new AbortController()
    const host = buildHost()
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(uploadTaskLog).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()

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
    uploadTaskLog.mockRejectedValueOnce(new Error("server returned 500"))
    report.mockResolvedValue({})
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    poll.mockResolvedValueOnce(workWith({ workflowRunId: "wf-fail", workId: "work-fail", agentJobId: "aj-fail" })).mockImplementation(async () => null)
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
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(uploadTaskLog).toHaveBeenCalledTimes(1)
    expect(report).toHaveBeenCalledTimes(1)
    expect(errorSpy.mock.calls.some((call) => call.some((arg) => typeof arg === "string" && arg.includes("task-log upload failed")))).toBe(true)

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
    poll.mockResolvedValueOnce(workWith({ workflowRunId: "wf-pending", workId: "work-pending", agentJobId: "aj-pending" })).mockImplementation(async () => null)
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    const controller = new AbortController()
    const host = buildHost()
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(uploadTaskLog).toHaveBeenCalledTimes(1)
    expect(report).toHaveBeenCalledTimes(1)
    expect(errorSpy.mock.calls.some((call) => call.some((arg) => typeof arg === "string" && arg.includes("task-log upload failed")))).toBe(true)

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
    poll.mockResolvedValueOnce(workWith({ workflowRunId: "wf-verdict", workId: "work-verdict", agentJobId: "aj-verdict" })).mockImplementation(async () => null)

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
    await vi.waitFor(() => expect(report).toHaveBeenCalled(), { timeout: 5_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    const reportCall = report.mock.calls[0] as [unknown, { status: string; message: string }]
    expect(reportCall[1].status).toBe("failed")
    expect(reportCall[1].message).toBe("boom")
  })
})
