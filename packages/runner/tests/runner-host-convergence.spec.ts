import { AsyncLocalStorage } from "node:async_hooks"
import { join } from "node:path"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import { defaultWorkspaceRegistryFilePath } from "../src/runtime/workspace-registry.js"
import type { DefaultRunnerTestResources } from "./support/test-resources.js"
import { withDefaultRunnerTestResources } from "./support/test-resources.js"
import { capturedLogs } from "./support/logger-test.js"

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

const FIXED_TIME = "2026-07-01T00:00:00.000Z"

// Capture the onReconnected callback that RunnerHost passes into the
// RunnerSignalRClient constructor. Each new RunnerSignalRClient instance
// overwrites this slot with its most-recently registered callback.
type ConvergenceMock = ReturnType<typeof vi.fn>
type ConvergenceMocks = Record<
  "connect" | "heartbeat" | "disconnect" | "poll" | "startSignalR" | "stopSignalR" |
  "getConnectionId" | "probeLiveness" | "workflowRunsStatus" | "listAgentSessionsForReconcile" |
  "reconcileMissingAgentSession" | "reconcileAgentSessionRuntimeEvents" | "fetchConfig" | "forceReconnect",
  ConvergenceMock
>

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

interface ConvergenceTestState {
  readonly resources: DefaultRunnerTestResources
  readonly mocks: ConvergenceMocks
  readonly root: string
  readonly registryEvents: RegistryEvents
  registryMarkEligible: (workflowRunId: string) => void
  registryRemove: (workflowRunId: string) => void
  capturedOnReconnected: ((connectionId: string) => void) | null
}

const convergenceTestStorage = new AsyncLocalStorage<ConvergenceTestState>()

function currentConvergenceTestState(): ConvergenceTestState {
  const state = convergenceTestStorage.getStore()
  if (!state) throw new Error("convergence test resource context is not active")
  return state
}

function scopedMock(name: keyof ConvergenceMocks): ConvergenceMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, "_isMockFunction", { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentConvergenceTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentConvergenceTestState().mocks[name], property)
      return typeof value === "function" ? value.bind(currentConvergenceTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentConvergenceTestState().mocks[name], property, value)
    },
  }) as unknown as ConvergenceMock
}

const connect = scopedMock("connect")
const heartbeat = scopedMock("heartbeat")
const disconnect = scopedMock("disconnect")
const poll = scopedMock("poll")
const startSignalR = scopedMock("startSignalR")
const stopSignalR = scopedMock("stopSignalR")
const getConnectionId = scopedMock("getConnectionId")
const probeLiveness = scopedMock("probeLiveness")
const workflowRunsStatus = scopedMock("workflowRunsStatus")
const listAgentSessionsForReconcile = scopedMock("listAgentSessionsForReconcile")
const reconcileMissingAgentSession = scopedMock("reconcileMissingAgentSession")
const reconcileAgentSessionRuntimeEvents = scopedMock("reconcileAgentSessionRuntimeEvents")
const fetchConfig = scopedMock("fetchConfig")
const forceReconnect = scopedMock("forceReconnect")

function createConvergenceMocks(): ConvergenceMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    startSignalR: vi.fn(async () => undefined),
    stopSignalR: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => "conn-1"),
    probeLiveness: vi.fn(async () => true),
    workflowRunsStatus: vi.fn(async () => ({})),
    listAgentSessionsForReconcile: vi.fn(async () => []),
    reconcileMissingAgentSession: vi.fn(async () => undefined),
    reconcileAgentSessionRuntimeEvents: vi.fn(async () => []),
    fetchConfig: vi.fn(async () => null),
    forceReconnect: vi.fn(async () => undefined),
  }
}

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
    workflowRunsStatus = workflowRunsStatus
    listAgentSessionsForReconcile = listAgentSessionsForReconcile
    reconcileMissingAgentSession = reconcileMissingAgentSession
    reconcileAgentSessionRuntimeEvents = reconcileAgentSessionRuntimeEvents
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
      currentConvergenceTestState().capturedOnReconnected = options.onReconnected ?? null
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
        currentConvergenceTestState().registryMarkEligible(workflowRunId)
        return entry
      }

      async remove(workflowRunId: string) {
        const removed = await super.remove(workflowRunId)
        currentConvergenceTestState().registryRemove(workflowRunId)
        return removed
      }
    },
  }
})

function configureHost(statusResponder: StatusResponder = async () => ({})): HostEvents {
  const events: HostEvents = {
    connected: eventQueue<void>(),
    statusQueries: eventQueue<string[]>(),
    polls: eventQueue<void>(),
  }

  currentConvergenceTestState().capturedOnReconnected = null
  currentConvergenceTestState().registryMarkEligible = (workflowRunId) => currentConvergenceTestState().registryEvents.eligible.push(workflowRunId)
  currentConvergenceTestState().registryRemove = (workflowRunId) => currentConvergenceTestState().registryEvents.removed.push(workflowRunId)
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
  listAgentSessionsForReconcile.mockReset().mockResolvedValue([])
  reconcileMissingAgentSession.mockReset()
  reconcileAgentSessionRuntimeEvents.mockReset().mockResolvedValue([])
  fetchConfig.mockReset().mockResolvedValue(null)
  forceReconnect.mockReset().mockResolvedValue(undefined)
  return events
}

async function waitForActiveStartup(events: HostEvents): Promise<string[]> {
  const workflowRunIds = await events.statusQueries.next()
  await events.polls.next()
  return workflowRunIds
}

describe("RunnerHost converges active workflow runs", () => {
  function testRoot(): string {
    return currentConvergenceTestState().root
  }

  function it(name: string, body: () => Promise<void>): void {
    vitestIt(name, async () => {
      await withDefaultRunnerTestResources(async (resources) => {
        const state: ConvergenceTestState = {
          resources,
          mocks: createConvergenceMocks(),
          root: "/virtual/runner-host-convergence",
          registryEvents: {
            eligible: eventQueue<string>(),
            removed: eventQueue<string>(),
          },
          registryMarkEligible: () => {},
          registryRemove: () => {},
          capturedOnReconnected: null,
        }
        await convergenceTestStorage.run(state, async () => {
          vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] })
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
      runnerId: "runner-test",
      projectId: "project-1",
      runnerRoot: testRoot(),
      pollIntervalMs: 60_000,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }
  }

  async function seedActiveEntry(workflowRunId: string, workspacePath: string) {
    const filePath = defaultWorkspaceRegistryFilePath(testRoot())
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
    await currentConvergenceTestState().resources.fileSystem.ensureDir(join(testRoot(), ".mohist/runner-state"))
    await currentConvergenceTestState().resources.fileSystem.writeText(filePath, JSON.stringify(file))
  }

  it("Startup_RunsOneConvergencePass_WithActiveEntriesFromRegistry", async () => {
    const wsPath = join(testRoot(), "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    const events = configureHost(async () => ({ "wr-1": "Completed" }))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)

    expect(await waitForActiveStartup(events)).toEqual(["wr-1"])
    expect(listAgentSessionsForReconcile).toHaveBeenCalledOnce()
    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Startup_ConvergenceTransitionsServerReportedTerminalToEligible", async () => {
    const wsPath = join(testRoot(), "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-1", wsPath)
    const events = configureHost(async () => ({ "wr-1": "Completed" }))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    await waitForActiveStartup(events)
    expect(await currentConvergenceTestState().registryEvents.eligible.next()).toBe("wr-1")
    const onDisk = JSON.parse(await currentConvergenceTestState().resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(onDisk.entries["wr-1"].phase).toBe("eligible")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Startup_OnlyActiveEntriesAreQueried_NeverEligible", async () => {
    const wsPathA = join(testRoot(), "mohist-local/workspaces/issue-1")
    const wsPathB = join(testRoot(), "mohist-local/workspaces/issue-2")
    // Seed one active + one eligible directly via the file.
    const filePath = defaultWorkspaceRegistryFilePath(testRoot())
    await currentConvergenceTestState().resources.fileSystem.ensureDir(join(testRoot(), ".mohist/runner-state"))
    await currentConvergenceTestState().resources.fileSystem.writeText(filePath, JSON.stringify({
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
    const wsPath1 = join(testRoot(), "mohist-local/workspaces/issue-1")
    const wsPath2 = join(testRoot(), "mohist-local/workspaces/issue-2")
    // Seed TWO active entries. The startup convergence marks wr-1
    // eligible (server reports it terminal). wr-2 stays active so the
    // reconnect convergence has something to query and pick up.
    const filePath = defaultWorkspaceRegistryFilePath(testRoot())
    await currentConvergenceTestState().resources.fileSystem.ensureDir(join(testRoot(), ".mohist/runner-state"))
    await currentConvergenceTestState().resources.fileSystem.writeText(filePath, JSON.stringify({
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
    expect(await currentConvergenceTestState().registryEvents.eligible.next()).toBe("wr-1")

    // Simulate a SignalR reconnect landing: by the time onreconnected
    // fires, getConnectionId() already returns the new id.
    expect(currentConvergenceTestState().capturedOnReconnected).toBeTypeOf("function")
    expect(currentConvergenceTestState().capturedOnReconnected).not.toBeNull()
    getConnectionId.mockReturnValue("conn-AFTER")
    currentConvergenceTestState().capturedOnReconnected!("conn-AFTER")

    expect(await events.statusQueries.next()).toEqual(["wr-2"])
    expect(await currentConvergenceTestState().registryEvents.eligible.next()).toBe("wr-2")
    const onDisk = JSON.parse(await currentConvergenceTestState().resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(onDisk.entries["wr-1"].phase).toBe("eligible")
    expect(onDisk.entries["wr-2"].phase).toBe("eligible")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("PeriodicTimer_FiresConvergenceRepeatedly_AfterInterval", async () => {
    const wsPath = join(testRoot(), "mohist-local/workspaces/issue-1")
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
    const wsPath = join(testRoot(), "mohist-local/workspaces/issue-1")
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
    const wsPath = join(testRoot(), "mohist-local/workspaces/issue-1")
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
    const wsPath = join(testRoot(), "mohist-local/workspaces/issue-1")
    await seedActiveEntry("wr-forgotten", wsPath)
    const events = configureHost(async () => ({}))

    const controller = new AbortController()
    const host = new RunnerHost({
      ...defaultOptions(),
      cleanupConvergenceIntervalMs: 5 * 60_000,
    })
    const run = host.run(controller.signal)
    expect(await waitForActiveStartup(events)).toEqual(["wr-forgotten"])
    expect(await currentConvergenceTestState().registryEvents.removed.next()).toBe("wr-forgotten")
    const onDisk = JSON.parse(await currentConvergenceTestState().resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(onDisk.entries["wr-forgotten"]).toBeUndefined()

    controller.abort()
    await expect(run).resolves.toBeUndefined()
  })

  it("Convergence_OnServerError_LogsAndContinues_DoesNotBlockWorkerPool", async () => {
    const wsPath = join(testRoot(), "mohist-local/workspaces/issue-1")
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
    const run = host.run(controller.signal)
    expect(await waitForActiveStartup(events)).toEqual(["wr-1"])

    // The registry entry is still active.
    const onDisk = JSON.parse(await currentConvergenceTestState().resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(onDisk.entries["wr-1"].phase).toBe("active")

    controller.abort()
    await expect(run).resolves.toBeUndefined()
    expect(capturedLogs()).toEqual(expect.arrayContaining([
      expect.objectContaining({ level: "ERROR", message: "workspace cleanup convergence query failed", fields: { exception: failure } }),
    ]))
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
