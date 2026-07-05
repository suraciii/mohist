import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import { defaultWorkspaceRegistryFilePath } from "../src/runtime/workspace-registry.js"

// Lifecycle coverage for the convergence backstop wiring (T-003):
//   - On startup (after the first SignalR connect) the runner fires a
//     single convergence pass against the server.
//   - On SignalR reconnect (onReconnected callback) the runner fires
//     another convergence pass.
//   - On a periodic timer (cleanupConvergenceIntervalMs) the runner
//     keeps firing convergence passes.
//
// The runner must NEVER enumerate or query workflow runs that have no
// active registry entry on this runner — the queries must be sourced
// exclusively from registry.list().filter(phase === "active").

const connect = vi.fn()
const heartbeat = vi.fn()
const disconnect = vi.fn()
const poll = vi.fn()
const startSignalR = vi.fn()
const stopSignalR = vi.fn()
const getConnectionId = vi.fn(() => "conn-1")
const probeLiveness = vi.fn(async () => true)
const workflowRunsStatus = vi.fn()
const fetchConfig = vi.fn(async () => null)

// Capture the onReconnected callback that RunnerHost passes into the
// RunnerSignalRClient constructor. Each new RunnerSignalRClient instance
// overwrites this slot with its most-recently registered callback.
let capturedOnReconnected: ((connectionId: string) => void) | null = null

const forceReconnect = vi.fn(async () => undefined)
const createSharedAcpConnection = vi.fn()
const shutdownSharedAcpConnection = vi.fn()

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
    workflowRunsStatus = workflowRunsStatus
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
    constructor(_serverUrl: string, _runnerId: string, _runnerRoot: string, _buildGitHash: string | null, options: { onReconnected?: (id: string) => void } = {}) {
      capturedOnReconnected = options.onReconnected ?? null
    }
  },
}))

vi.mock("../src/runtime/opencode-models.js", () => ({
  discoverOpencodeModels: vi.fn(async () => ({ models: ["openai/gpt-5.5"], variants: {} })),
}))

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    key(_workflowRunId: string, _sessionName: string) { return `${_workflowRunId}:${_sessionName}` }
    get(_key: string) { return undefined }
    set() {}
    has() { return false }
    delete() {}
  },
  createSharedAcpConnection: (...args: unknown[]) => createSharedAcpConnection(...args),
}))

beforeEach(() => {
  createSharedAcpConnection.mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers: vi.fn(),
    clearSessionHandlers: vi.fn(),
    shutdown: shutdownSharedAcpConnection,
  })
  shutdownSharedAcpConnection.mockResolvedValue(undefined)
})

describe("RunnerHost convergence wiring (T-003)", () => {
  let root: string

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-host-convergence-"))
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  function defaultOptions() {
    return {
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: root,
      pollIntervalMs: 60_000,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }
  }

  async function seedActiveEntry(workflowRunId: string, workspacePath: string) {
    const filePath = defaultWorkspaceRegistryFilePath(root)
    const file = {
      version: 1,
      entries: {
        [workflowRunId]: {
          issueId: "issue-1",
          issueNumber: 1,
          workflowRunId,
          workspacePath,
          phase: "active",
          materializedAt: new Date().toISOString(),
          terminalAt: null,
        },
      },
    }
    const { mkdir } = await import("node:fs/promises")
    await mkdir(join(root, ".mohist/runner-state"), { recursive: true })
    await writeFile(filePath, JSON.stringify(file))
  }

  it("Startup_RunsOneConvergencePass_WithActiveEntriesFromRegistry", async () => {
    vi.clearAllMocks()
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    workflowRunsStatus.mockResolvedValue({ "wr-1": "Completed" })
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(workflowRunsStatus).toHaveBeenCalled(), { timeout: 5_000 })

    const lastCall = workflowRunsStatus.mock.calls.at(-1)!
    expect(lastCall[0]).toEqual(["wr-1"])
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Startup_ConvergenceTransitionsServerReportedTerminalToEligible", async () => {
    vi.clearAllMocks()
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    workflowRunsStatus.mockResolvedValue({ "wr-1": "Completed" })
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(workflowRunsStatus).toHaveBeenCalled(), { timeout: 5_000 })
    // Wait for the transition to land on disk.
    await vi.waitFor(async () => {
      const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
      expect(onDisk.entries["wr-1"].phase).toBe("eligible")
    }, { timeout: 5_000 })

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Startup_OnlyActiveEntriesAreQueried_NeverEligible", async () => {
    vi.clearAllMocks()
    const wsPathA = join(root, "mohist-local/workspaces/issue-1")
    const wsPathB = join(root, "mohist-local/workspaces/issue-2")
    // Seed one active + one eligible directly via the file.
    const filePath = defaultWorkspaceRegistryFilePath(root)
    const { mkdir } = await import("node:fs/promises")
    await mkdir(join(root, ".mohist/runner-state"), { recursive: true })
    await writeFile(filePath, JSON.stringify({
      version: 1,
      entries: {
        "wr-active": {
          issueId: "issue-1",
          issueNumber: 1,
          workflowRunId: "wr-active",
          workspacePath: wsPathA,
          phase: "active",
          materializedAt: new Date().toISOString(),
          terminalAt: null,
        },
        "wr-eligible": {
          issueId: "issue-2",
          issueNumber: 2,
          workflowRunId: "wr-eligible",
          workspacePath: wsPathB,
          phase: "eligible",
          materializedAt: new Date().toISOString(),
          terminalAt: new Date().toISOString(),
        },
      },
    }))
    workflowRunsStatus.mockResolvedValue({ "wr-active": "Running" })
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(workflowRunsStatus).toHaveBeenCalled(), { timeout: 5_000 })

    const queried = workflowRunsStatus.mock.calls.at(-1)![0] as string[]
    expect(queried).toEqual(["wr-active"])
    expect(queried).not.toContain("wr-eligible")
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("OnReconnected_RunsAnotherConvergencePass", async () => {
    vi.clearAllMocks()
    capturedOnReconnected = null
    const wsPath1 = join(root, "mohist-local/workspaces/issue-1")
    const wsPath2 = join(root, "mohist-local/workspaces/issue-2")
    // Seed TWO active entries. The startup convergence marks wr-1
    // eligible (server reports it terminal). wr-2 stays active so the
    // reconnect convergence has something to query and pick up.
    const filePath = defaultWorkspaceRegistryFilePath(root)
    const { mkdir } = await import("node:fs/promises")
    await mkdir(join(root, ".mohist/runner-state"), { recursive: true })
    await writeFile(filePath, JSON.stringify({
      version: 1,
      entries: {
        "wr-1": {
          issueId: "issue-1", issueNumber: 1, workflowRunId: "wr-1",
          workspacePath: wsPath1, phase: "active",
          materializedAt: new Date().toISOString(), terminalAt: null,
        },
        "wr-2": {
          issueId: "issue-2", issueNumber: 2, workflowRunId: "wr-2",
          workspacePath: wsPath2, phase: "active",
          materializedAt: new Date().toISOString(), terminalAt: null,
        },
      },
    }))
    workflowRunsStatus
      .mockResolvedValueOnce({ "wr-1": "Completed", "wr-2": "Running" })
      .mockResolvedValueOnce({ "wr-1": "Completed", "wr-2": "Completed" })
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)
    getConnectionId.mockReturnValue("conn-A")

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())

    // Wait for the startup convergence call.
    await vi.waitFor(() => expect(workflowRunsStatus.mock.calls.length).toBeGreaterThanOrEqual(1), { timeout: 5_000 })
    const startupCalls = workflowRunsStatus.mock.calls.length

    // Simulate a SignalR reconnect landing: by the time onreconnected
    // fires, getConnectionId() already returns the new id.
    expect(capturedOnReconnected).toBeTypeOf("function")
    expect(capturedOnReconnected).not.toBeNull()
    getConnectionId.mockReturnValue("conn-AFTER")
    capturedOnReconnected!("conn-AFTER")

    await vi.waitFor(() => expect(workflowRunsStatus.mock.calls.length).toBeGreaterThan(startupCalls), { timeout: 5_000 })

    // After both convergence passes, both wr-1 and wr-2 are eligible.
    // Wait for the second convergence's markEligible persistence to land
    // on disk (the convergence is detached via `void`).
    await vi.waitFor(async () => {
      const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
      expect(onDisk.entries["wr-1"].phase).toBe("eligible")
      expect(onDisk.entries["wr-2"].phase).toBe("eligible")
    }, { timeout: 5_000 })

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("PeriodicTimer_FiresConvergenceRepeatedly_AfterInterval", async () => {
    vi.clearAllMocks()
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    workflowRunsStatus.mockResolvedValue({ "wr-1": "Running" })
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      // Short interval so the test does not need to wait long.
      cleanupConvergenceIntervalMs: 30,
      // Keep the worker pool idle.
      pollIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(workflowRunsStatus).toHaveBeenCalled(), { timeout: 5_000 })
    const startupCalls = workflowRunsStatus.mock.calls.length

    // Wait long enough for at least 3 additional periodic ticks.
    await vi.waitFor(() => expect(workflowRunsStatus.mock.calls.length).toBeGreaterThanOrEqual(startupCalls + 3), { timeout: 5_000 })

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("PeriodicTimer_IsClearedOnShutdown_NoLeakAcrossReconnectLoops", async () => {
    vi.clearAllMocks()
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    workflowRunsStatus.mockResolvedValue({ "wr-1": "Running" })
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 20,
      pollIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(workflowRunsStatus.mock.calls.length).toBeGreaterThanOrEqual(1), { timeout: 5_000 })
    const callsAtShutdown = workflowRunsStatus.mock.calls.length

    controller.abort()
    await expect(run).resolves.toBeUndefined()

    await new Promise((r) => setTimeout(r, 80))
    expect(workflowRunsStatus.mock.calls.length).toBe(callsAtShutdown)
  })

  it("Convergence_NeverQueriesWorkflowRunsOutsideTheRegistry", async () => {
    vi.clearAllMocks()
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-mine", wsPath)
    workflowRunsStatus.mockResolvedValue({ "wr-mine": "Running" })
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 20,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(workflowRunsStatus.mock.calls.length).toBeGreaterThanOrEqual(1), { timeout: 5_000 })

    for (const call of workflowRunsStatus.mock.calls) {
      const ids = call[0] as string[]
      expect(ids).toContain("wr-mine")
      expect(ids).not.toContain("wr-other-runner")
      expect(ids).not.toContain("wr-not-mine")
    }

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Convergence_ServerForgotRun_DropsRegistryEntry", async () => {
    vi.clearAllMocks()
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-forgotten", wsPath)
    workflowRunsStatus.mockResolvedValue({}) // server drops wr-forgotten
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(workflowRunsStatus).toHaveBeenCalled(), { timeout: 5_000 })
    await vi.waitFor(async () => {
      const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
      expect(onDisk.entries["wr-forgotten"]).toBeUndefined()
    }, { timeout: 5_000 })

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Convergence_OnServerError_LogsAndContinues_DoesNotBlockWorkerPool", async () => {
    vi.clearAllMocks()
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    workflowRunsStatus.mockRejectedValue(new Error("network blip"))
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(workflowRunsStatus).toHaveBeenCalled(), { timeout: 5_000 })
    // Worker pool is still ticking: heartbeat fires per its interval.
    await vi.waitFor(() => expect(poll).toHaveBeenCalled(), { timeout: 5_000 })

    // The registry entry is still active.
    const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
    expect(onDisk.entries["wr-1"].phase).toBe("active")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("EmptyRegistry_StartupConvergence_DoesNotCallServer", async () => {
    vi.clearAllMocks()
    poll.mockResolvedValue(null)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    probeLiveness.mockResolvedValue(true)

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await vi.waitFor(() => expect(connect).toHaveBeenCalled())
    await vi.waitFor(() => expect(poll).toHaveBeenCalled(), { timeout: 5_000 })
    expect(workflowRunsStatus).not.toHaveBeenCalled()

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })
})
