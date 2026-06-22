import { describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"

const connect = vi.fn()
const heartbeat = vi.fn()
const disconnect = vi.fn()
const poll = vi.fn()
const startSignalR = vi.fn()
const stopSignalR = vi.fn()

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = startSignalR
    stop = stopSignalR
  },
}))

vi.mock("../src/runtime/opencode-models.js", () => ({
  discoverOpencodeModels: vi.fn(async () => ({ models: ["openai/gpt-5.5"], variants: {} })),
}))

const setSessionHandlers = vi.fn()
const clearSessionHandlers = vi.fn()
const acpShutdown = vi.fn()

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    key(_workflowRunId: string, _sessionName: string) { return `${_workflowRunId}:${_sessionName}` }
    get(_key: string) { return undefined }
    set() {}
    has() { return false }
    delete() {}
  },
  createSharedAcpConnection: vi.fn(async () => ({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers,
    clearSessionHandlers,
    shutdown: acpShutdown,
  })),
}))

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    close() {}
  },
  createSharedAcpConnection: vi.fn(async () => ({
    async resume() { return null },
    async shutdown() {},
  })),
}))

describe("RunnerHost", () => {
  it("RunnerRegistration_ReportsConfiguredWorkflowSlots", async () => {
    vi.clearAllMocks()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 3,
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(connect).toHaveBeenCalledWith(
      expect.objectContaining({
        projectId: "project-1",
        coderModels: ["openai/gpt-5.5"],
        maxWorkflowSlots: 3,
      }),
      expect.any(AbortSignal),
    )
  })

  it("RunnerShutdown_UnregistersRunner", async () => {
    vi.clearAllMocks()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    controller.abort()
    await expect(run).resolves.toBeUndefined()

    expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))
    expect(stopSignalR).toHaveBeenCalled()
  })

  it("RunnerConnection_WhenSignalRFails_DoesNotPollAndRetriesCleanly", async () => {
    vi.clearAllMocks()
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue(null)
    let resolveSecondSignalR!: () => void
    const secondSignalR = new Promise<void>((resolve) => {
      resolveSecondSignalR = resolve
    })
    startSignalR
      .mockRejectedValueOnce(new Error("signalr unavailable"))
      .mockReturnValueOnce(secondSignalR)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      maxConcurrentWorkflows: 1,
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
    })

    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(startSignalR).toHaveBeenCalledTimes(2))
    expect(poll).not.toHaveBeenCalled()
    expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))

    resolveSecondSignalR()
    await vi.waitFor(() => expect(poll).toHaveBeenCalled(), { timeout: 10_000 })
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })
})
