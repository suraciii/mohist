import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import { defaultWorkspaceRegistryFilePath } from "../src/runtime/workspace-registry.js"
import { clearOpenCodeRuntimeFactoryForTest, installReadyOpenCodeRuntimeFactory } from "./support/opencode-runtime-factory.js"

const installReadyRuntimeFactory = installReadyOpenCodeRuntimeFactory

// Lifecycle coverage for the convergence backstop wiring:
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
const FIXED_TIME = "2026-07-01T00:00:00.000Z"

// Capture the onReconnected callback that RunnerHost passes into the
// RunnerSignalRClient constructor. Each new RunnerSignalRClient instance
// overwrites this slot with its most-recently registered callback.
let capturedOnReconnected: ((connectionId: string) => void) | null = null

const forceReconnect = vi.fn(async () => undefined)
const createSharedAcpConnection = vi.fn()
const shutdownSharedAcpConnection = vi.fn()
const registryTransitions = vi.hoisted(() => ({
  markEligible: vi.fn(),
  remove: vi.fn(),
}))

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
  connected: EventQueue<void>
  statusQueries: EventQueue<string[]>
  polls: EventQueue<void>
}

interface RegistryEvents {
  eligible: EventQueue<string>
  removed: EventQueue<string>
}

type StatusResponder = (workflowRunIds: string[]) => Record<string, string> | Promise<Record<string, string>>

let registryEvents: RegistryEvents

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

vi.mock("../src/runtime/workspace-registry.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/runtime/workspace-registry.js")>()

  return {
    ...actual,
    WorkspaceRegistry: class extends actual.WorkspaceRegistry {
      async markEligible(workflowRunId: string) {
        const entry = await super.markEligible(workflowRunId)
        registryTransitions.markEligible(workflowRunId)
        return entry
      }

      async remove(workflowRunId: string) {
        const removed = await super.remove(workflowRunId)
        registryTransitions.remove(workflowRunId)
        return removed
      }
    },
  }
})

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
  installReadyRuntimeFactory()
  registryEvents = {
    eligible: eventQueue<string>(),
    removed: eventQueue<string>(),
  }
  registryTransitions.markEligible.mockReset().mockImplementation((workflowRunId: string) => registryEvents.eligible.push(workflowRunId))
  registryTransitions.remove.mockReset().mockImplementation((workflowRunId: string) => registryEvents.removed.push(workflowRunId))
  createSharedAcpConnection.mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers: vi.fn(),
    clearSessionHandlers: vi.fn(),
    shutdown: shutdownSharedAcpConnection,
  })
  shutdownSharedAcpConnection.mockResolvedValue(undefined)
})

function configureHost(statusResponder: StatusResponder = async () => ({})): HostEvents {
  const events: HostEvents = {
    connected: eventQueue<void>(),
    statusQueries: eventQueue<string[]>(),
    polls: eventQueue<void>(),
  }

  vi.clearAllMocks()
  capturedOnReconnected = null
  connect.mockReset().mockImplementation(async () => {
    events.connected.push()
  })
  heartbeat.mockReset().mockResolvedValue(undefined)
  disconnect.mockReset().mockResolvedValue(undefined)
  poll.mockReset().mockImplementation(async () => {
    events.polls.push()
    return []
  })
  startSignalR.mockReset().mockResolvedValue(undefined)
  stopSignalR.mockReset().mockResolvedValue(undefined)
  getConnectionId.mockReset().mockReturnValue("conn-1")
  probeLiveness.mockReset().mockResolvedValue(true)
  workflowRunsStatus.mockReset().mockImplementation(async (workflowRunIds: string[]) => {
    events.statusQueries.push([...workflowRunIds])
    return await statusResponder([...workflowRunIds])
  })
  fetchConfig.mockReset().mockResolvedValue(null)
  forceReconnect.mockReset().mockResolvedValue(undefined)
  createSharedAcpConnection.mockReset().mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers: vi.fn(),
    clearSessionHandlers: vi.fn(),
    shutdown: shutdownSharedAcpConnection,
  })
  shutdownSharedAcpConnection.mockReset().mockResolvedValue(undefined)
  return events
}

async function waitForActiveStartup(events: HostEvents): Promise<string[]> {
  const workflowRunIds = await events.statusQueries.next()
  await events.polls.next()
  return workflowRunIds
}

describe("RunnerHost converges active workflow runs", () => {
  let root: string

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-host-convergence-"))
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
  })

  afterEach(async () => {
    clearOpenCodeRuntimeFactoryForTest()
    vi.useRealTimers()
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
        version: 2,
      entries: {
        [workflowRunId]: {
          issueNumber: 1,
          workflowRunId,
          workspacePath,
          projectId: "project-1",
          repositoryName: "main",
          baseBranch: "main",
          runBranch: `mohist/run-${workflowRunId}`,
          remoteFingerprint: "fingerprint",
          remoteIdentityVersion: "git-remote-url/v1",
          phase: "active",
          materializedAt: FIXED_TIME,
          terminalAt: null,
        },
      },
    }
    const { mkdir } = await import("node:fs/promises")
    await mkdir(join(root, ".mohist/runner-state"), { recursive: true })
    await writeFile(filePath, JSON.stringify(file))
  }

  it("Startup_RunsOneConvergencePass_WithActiveEntriesFromRegistry", async () => {
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    const events = configureHost(async () => ({ "wr-1": "Completed" }))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)

    expect(await waitForActiveStartup(events)).toEqual(["wr-1"])
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Startup_ConvergenceTransitionsServerReportedTerminalToEligible", async () => {
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    const events = configureHost(async () => ({ "wr-1": "Completed" }))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await waitForActiveStartup(events)
    expect(await registryEvents.eligible.next()).toBe("wr-1")
    const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
    expect(onDisk.entries["wr-1"].phase).toBe("eligible")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Startup_OnlyActiveEntriesAreQueried_NeverEligible", async () => {
    const wsPathA = join(root, "mohist-local/workspaces/issue-1")
    const wsPathB = join(root, "mohist-local/workspaces/issue-2")
    // Seed one active + one eligible directly via the file.
    const filePath = defaultWorkspaceRegistryFilePath(root)
    const { mkdir } = await import("node:fs/promises")
    await mkdir(join(root, ".mohist/runner-state"), { recursive: true })
    await writeFile(filePath, JSON.stringify({
      version: 2,
      entries: {
        "wr-active": {
          issueNumber: 1,
          workflowRunId: "wr-active",
          workspacePath: wsPathA,
          projectId: "project-1", repositoryName: "main", baseBranch: "main", runBranch: "mohist/run-wr-active", remoteFingerprint: "fingerprint", remoteIdentityVersion: "git-remote-url/v1",
          phase: "active",
          materializedAt: FIXED_TIME,
          terminalAt: null,
        },
        "wr-eligible": {
          issueNumber: 2,
          workflowRunId: "wr-eligible",
          workspacePath: wsPathB,
          projectId: "project-1", repositoryName: "main", baseBranch: "main", runBranch: "mohist/run-wr-eligible", remoteFingerprint: "fingerprint", remoteIdentityVersion: "git-remote-url/v1",
          phase: "eligible",
          materializedAt: FIXED_TIME,
          terminalAt: FIXED_TIME,
        },
      },
    }))
    const events = configureHost(async () => ({ "wr-active": "Running" }))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)

    const queried = await waitForActiveStartup(events)
    expect(queried).toEqual(["wr-active"])
    expect(queried).not.toContain("wr-eligible")
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("OnReconnected_RunsAnotherConvergencePass", async () => {
    const wsPath1 = join(root, "mohist-local/workspaces/issue-1")
    const wsPath2 = join(root, "mohist-local/workspaces/issue-2")
    // Seed TWO active entries. The startup convergence marks wr-1
    // eligible (server reports it terminal). wr-2 stays active so the
    // reconnect convergence has something to query and pick up.
    const filePath = defaultWorkspaceRegistryFilePath(root)
    const { mkdir } = await import("node:fs/promises")
    await mkdir(join(root, ".mohist/runner-state"), { recursive: true })
    await writeFile(filePath, JSON.stringify({
      version: 2,
      entries: {
        "wr-1": {
          issueNumber: 1, workflowRunId: "wr-1",
          workspacePath: wsPath1, phase: "active",
          projectId: "project-1", repositoryName: "main", baseBranch: "main", runBranch: "mohist/run-wr-1", remoteFingerprint: "fingerprint", remoteIdentityVersion: "git-remote-url/v1",
          materializedAt: FIXED_TIME, terminalAt: null,
        },
        "wr-2": {
          issueNumber: 2, workflowRunId: "wr-2",
          workspacePath: wsPath2, phase: "active",
          projectId: "project-1", repositoryName: "main", baseBranch: "main", runBranch: "mohist/run-wr-2", remoteFingerprint: "fingerprint", remoteIdentityVersion: "git-remote-url/v1",
          materializedAt: FIXED_TIME, terminalAt: null,
        },
      },
    }))
    const responses: Array<Record<string, string>> = [
      { "wr-1": "Completed", "wr-2": "Running" },
      { "wr-2": "Completed" },
    ]
    const events = configureHost(async () => {
      const response = responses.shift()
      if (!response) throw new Error("unexpected convergence query")
      return response
    })
    getConnectionId.mockReturnValue("conn-A")

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    expect(await waitForActiveStartup(events)).toEqual(["wr-1", "wr-2"])
    expect(await registryEvents.eligible.next()).toBe("wr-1")

    // Simulate a SignalR reconnect landing: by the time onreconnected
    // fires, getConnectionId() already returns the new id.
    expect(capturedOnReconnected).toBeTypeOf("function")
    expect(capturedOnReconnected).not.toBeNull()
    getConnectionId.mockReturnValue("conn-AFTER")
    capturedOnReconnected!("conn-AFTER")

    expect(await events.statusQueries.next()).toEqual(["wr-2"])
    expect(await registryEvents.eligible.next()).toBe("wr-2")
    const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
    expect(onDisk.entries["wr-1"].phase).toBe("eligible")
    expect(onDisk.entries["wr-2"].phase).toBe("eligible")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("PeriodicTimer_FiresConvergenceRepeatedly_AfterInterval", async () => {
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    const events = configureHost(async () => ({ "wr-1": "Running" }))
    const convergenceIntervalMs = 1_000

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: convergenceIntervalMs,
      pollIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    expect(await waitForActiveStartup(events)).toEqual(["wr-1"])

    for (let tick = 0; tick < 3; tick += 1) {
      const query = events.statusQueries.next()
      await vi.advanceTimersByTimeAsync(convergenceIntervalMs)
      expect(await query).toEqual(["wr-1"])
    }
    expect(events.statusQueries.count).toBe(4)

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("PeriodicTimer_IsClearedOnShutdown_NoLeakAcrossReconnectLoops", async () => {
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    const events = configureHost(async () => ({ "wr-1": "Running" }))
    const convergenceIntervalMs = 1_000

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: convergenceIntervalMs,
      pollIntervalMs: 60_000,
    })
    const run = host.run(controller.signal)
    await waitForActiveStartup(events)
    const callsAtShutdown = events.statusQueries.count

    controller.abort()
    await expect(run).resolves.toBeUndefined()

    await vi.advanceTimersByTimeAsync(convergenceIntervalMs * 4)
    expect(events.statusQueries.count).toBe(callsAtShutdown)
  })

  it("Convergence_NeverQueriesWorkflowRunsOutsideTheRegistry", async () => {
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-mine", wsPath)
    const events = configureHost(async () => ({ "wr-mine": "Running" }))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 20,
    })
    const run = host.run(controller.signal)

    const ids = await waitForActiveStartup(events)
    expect(ids).toContain("wr-mine")
    expect(ids).not.toContain("wr-other-runner")
    expect(ids).not.toContain("wr-not-mine")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Convergence_ServerForgotRun_DropsRegistryEntry", async () => {
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-forgotten", wsPath)
    const events = configureHost(async () => ({}))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    expect(await waitForActiveStartup(events)).toEqual(["wr-forgotten"])
    expect(await registryEvents.removed.next()).toBe("wr-forgotten")
    const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
    expect(onDisk.entries["wr-forgotten"]).toBeUndefined()

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Convergence_OnServerError_LogsAndContinues_DoesNotBlockWorkerPool", async () => {
    const wsPath = join(root, "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    const failure = new Error("network blip")
    const events = configureHost(async () => {
      throw failure
    })

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    try {
      const run = host.run(controller.signal)
      expect(await waitForActiveStartup(events)).toEqual(["wr-1"])

      // The registry entry is still active.
      const onDisk = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(root), "utf8"))
      expect(onDisk.entries["wr-1"].phase).toBe("active")

      controller.abort()
      await expect(run).resolves.toBeUndefined()
      expect(errorSpy).toHaveBeenCalledOnce()
      expect(errorSpy).toHaveBeenCalledWith("workspace cleanup convergence query failed:", failure)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it("EmptyRegistry_StartupConvergence_DoesNotCallServer", async () => {
    const events = configureHost()

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await events.connected.next()
    await events.polls.next()
    expect(events.statusQueries.count).toBe(0)

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })
})
