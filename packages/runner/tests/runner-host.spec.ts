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
  discoverOpencodeModels: vi.fn(async () => ["openai/gpt-5.5"]),
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
})
