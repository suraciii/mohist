import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import { deferred } from "./support/deferred.js"
import { setExecutorGitRunnerForTest, type GitRunner } from "../src/runtime/git-probe.js"
import { UnexpectedConsoleRecorder } from "./support/unexpected-console.js"
import {
  clearOpenCodeRuntimeFactoryForTest,
  installFakeOpenCodeRuntimeFactory,
  installReadyOpenCodeRuntimeFactory,
} from "./support/opencode-runtime-factory.js"

// Wire-level coverage for the OpenCodeRuntime lifecycle the runner host
// owns in T-003:
//   - connectRunner/initializeSharedConnection start the OpenCode
//     server + client and load the catalog via the runtime; the
//     RunnerRegistration reports `coderModels`/`coderModelVariants`
//     sourced from the runtime (no CLI discovery).
//   - pollOnce is skipped while runtime.ready() is false; awaitingAck
//     reports still drain.
//   - On a simulated server exit the runner stops claiming,
//     in-flight Workflow turns fail without auto-replay, and
//     claiming resumes only after health + catalog re-pass.
//   - The transitional AgentJob readiness gate is honoured —
//     AgentJob work, still on ACP until #410, is also paused when
//     the OpenCode runtime is not ready (one shared gate per design
//     D3).
//   - The runtime handle reaches ActionContext for Workflow work;
//     the AgentJob path still receives the ACP connection.

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000

const nonGitRunner: GitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: "",
  stderr: "not a git repository",
  combinedOutput: "not a git repository",
})

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
  setSessionHandlers: vi.fn(),
  clearSessionHandlers: vi.fn(),
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
  setSessionHandlers,
  clearSessionHandlers,
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

vi.mock("../src/actions/registry.js", () => ({
  createDefaultRegistry: () => ({
    resolve: (uses?: string | null) => uses === "test/block" || uses === "test/observe" ? blockingAction : undefined,
  }),
}))

vi.mock("../src/runtime/acp-connection.js", () => ({
  AcpSessionManager: class {
    workflowKey(workflowRunId: string, sessionName: string) { return `workflow:${workflowRunId}:${sessionName}` }
    genericKey(sessionId: string) { return `generic:${sessionId}` }
    get() { return undefined }
    set() {}
    has() { return false }
    delete() {}
  },
  createSharedAcpConnection: (...args: unknown[]) => createSharedAcpConnection(...args),
}))

vi.mock("../src/runtime/workspace.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/runtime/workspace.js")>()
  class FakeWorkspaceManager {
    async prepare() {
      return { path: "/tmp/mohist-runner-host-opencode-runtime", branch: "main", changeDir: null }
    }
    async verify() {
      return { path: "/tmp/mohist-runner-host-opencode-runtime", branch: "main", changeDir: null }
    }
  }
  return {
    ...actual,
    WorkspaceManager: FakeWorkspaceManager,
  }
})

beforeEach(() => {
  vi.useFakeTimers()
  setExecutorGitRunnerForTest(nonGitRunner)
  clearOpenCodeRuntimeFactoryForTest()
  blockingAction.mockReset()
  connect.mockReset().mockResolvedValue(undefined)
  heartbeat.mockReset().mockResolvedValue(undefined)
  disconnect.mockReset().mockResolvedValue(undefined)
  poll.mockReset().mockResolvedValue([])
  report.mockReset().mockResolvedValue({})
  uploadTaskLog.mockReset().mockResolvedValue({ accepted: 0, truncated: false })
  startSignalR.mockReset().mockResolvedValue(undefined)
  stopSignalR.mockReset().mockResolvedValue(undefined)
  getConnectionId.mockReset().mockReturnValue("conn-1")
  probeLiveness.mockReset().mockResolvedValue(true)
  forceReconnect.mockReset().mockResolvedValue(undefined)
  createSharedAcpConnection.mockResolvedValue({
    connection: { prompt: vi.fn(), cancel: vi.fn(), newSession: vi.fn(), resumeSession: vi.fn(), setSessionConfigOption: vi.fn(), closeSession: vi.fn() },
    processPid: 99999,
    setSessionHandlers,
    clearSessionHandlers,
    shutdown: shutdownSharedAcpConnection,
  })
  shutdownSharedAcpConnection.mockResolvedValue(undefined)
  acpShutdown.mockResolvedValue(undefined)
})

afterEach(() => {
  setExecutorGitRunnerForTest(null)
  clearOpenCodeRuntimeFactoryForTest()
})

function hostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    serverUrl: "http://localhost:3456",
    runnerId: "runner-test",
    projectId: "project-1",
    runnerRoot: "/tmp/mohist-runner-host-opencode-runtime",
    pollIntervalMs: POLL_INTERVAL_MS,
    heartbeatIntervalMs: QUIET_INTERVAL_MS,
    dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
  }
}

function workflowVariables(): Record<string, unknown> {
  return {
    repository: { gitUrl: "https://example.com/repo.git", baseBranch: "main" },
    issue: { number: 1 },
    workspace: { path: "/tmp/mohist-runner-host-opencode-runtime" },
    mohist: { runId: "wr-test" },
  }
}

describe("RunnerHost wires the OpenCodeRuntime lifecycle", () => {
  it("ready-claim: connectRunner/initializeSharedConnection start the OpenCode server + client and load the catalog via the runtime", async () => {
    installFakeOpenCodeRuntimeFactory({
      catalog: {
        models: [
          { providerID: "openai", modelID: "gpt-5", variants: ["low", "high"] },
          { providerID: "anthropic", modelID: "claude-sonnet-4", variants: [] },
        ],
        fetchedAt: 0,
      },
    })
    const connected = deferred<void>()
    connect.mockImplementation(async () => { connected.resolve() })
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions())
    const run = host.run(controller.signal)
    try {
      await connected.promise
      const connectArg = connect.mock.calls[0]?.[0] as Record<string, unknown>
      expect(connectArg?.coderModels).toEqual(["openai/gpt-5", "anthropic/claude-sonnet-4"])
      expect(connectArg?.coderModelVariants).toEqual({ "openai/gpt-5": ["low", "high"] })
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("RunnerRegistration reports the catalog sourced from the runtime on every heartbeat", async () => {
    installFakeOpenCodeRuntimeFactory({
      catalog: {
        models: [{ providerID: "openai", modelID: "gpt-5", variants: ["low"] }],
        fetchedAt: 0,
      },
    })
    const connected = deferred<void>()
    connect.mockImplementation(async () => { connected.resolve() })
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions())
    const run = host.run(controller.signal)
    try {
      await connected.promise
      // Drive a heartbeat tick to confirm the registration body keeps
      // carrying the runtime-sourced catalog.
      await vi.advanceTimersByTimeAsync(QUIET_INTERVAL_MS + 10)
      const heartbeatBodies = heartbeat.mock.calls.map((call) => call[0] as Record<string, unknown>)
      expect(heartbeatBodies.length).toBeGreaterThan(0)
      for (const body of heartbeatBodies) {
        expect(body.coderModels).toEqual(["openai/gpt-5"])
        expect(body.coderModelVariants).toEqual({ "openai/gpt-5": ["low"] })
      }
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("not-ready-skip: when the runtime flips to not-ready mid-flight, pollOnce stops and the existing report still drains", async () => {
    // Start with a ready runtime; let the first poll dispatch and
    // capture the work item's report; then simulate a server exit
    // and confirm no further polls run until the runtime recovers.
    const installed = installFakeOpenCodeRuntimeFactory({ rebuildDelayMs: 50 })
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    let reportAttempts = 0
    report.mockImplementation(async () => {
      reportAttempts += 1
      if (reportAttempts === 1) {
        reportStarted.resolve()
        await reportRelease.promise
      }
      return {}
    })
    blockingAction.mockReset().mockResolvedValue({ status: "success", message: "ok" })
    poll.mockResolvedValueOnce([{
      workflowRunId: "wr-drain",
      workId: "work-drain",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: workflowVariables(),
    }]).mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions())
    // Capture the readiness-diagnostic warn so it doesn't trip the
    // unexpected-console recorder — the diagnostic IS the expected
    // signal the test verifies.
    const warnCalls: string[] = []
    const previousWarn = console.warn
    console.warn = (...args: unknown[]) => {
      warnCalls.push(args.map((value) => typeof value === "string" ? value : String(value)).join(" "))
    }
    const run = host.run(controller.signal)
    try {
      await reportStarted.promise
      // Flip the runtime to not-ready by simulating a server exit.
      installed.subscription.emit({ type: "server.disconnected", payload: {} })
      expect(installed.lastRuntime?.ready()).toBe(false)
      // Capture the post-flip poll count; advance time and verify it
      // stays flat (gate is closed).
      const callsBefore = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBe(callsBefore)
      // The actionable readiness diagnostic is emitted while the gate
      // is closed.
      expect(warnCalls.some((message) => /opencode runtime not ready \(server-exit\)/.test(message))).toBe(true)
      // awaitingAck drains while not-ready: the in-flight report
      // resolves and the entry leaves awaitingAck on the next loop
      // tick. The run continues without a fresh poll.
      reportRelease.resolve()
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 2)
      // After rebuildDelayMs the runtime re-passes and the gate
      // reopens. Confirm the next poll tick runs.
      const callsAfterDrain = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBeGreaterThan(callsAfterDrain)
    } finally {
      controller.abort()
      reportRelease.resolve()
      console.warn = previousWarn
      await run.catch(() => undefined)
    }
  })

  it("server-exit-rebuild-resume: in-flight Workflow turns fail without auto-replay and claiming resumes after rebuild", async () => {
    const installed = installFakeOpenCodeRuntimeFactory({ rebuildDelayMs: 50 })
    const firstPollDone = deferred<void>()
    const actionStarted = deferred<void>()
    const actionRelease = deferred<void>()
    let pollCalls = 0
    poll.mockImplementation(async () => {
      pollCalls += 1
      if (pollCalls === 1) {
        firstPollDone.resolve()
        return [{
          workflowRunId: "wr-exit",
          workId: "work-exit",
          workType: "task",
          uses: "test/observe",
          ownerKind: "workflow",
          variables: workflowVariables(),
        }]
      }
      return []
    })
    blockingAction.mockReset().mockImplementation(async () => {
      actionStarted.resolve()
      await actionRelease.promise
      return { status: "success", message: "ok" }
    })
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions())
    const previousWarn = console.warn
    console.warn = () => undefined
    const run = host.run(controller.signal)
    try {
      await firstPollDone.promise
      await actionStarted.promise
      // Mid-turn server exit: runtime goes not-ready and the in-flight
      // turn reports its result exactly once.
      installed.subscription.emit({ type: "server.disconnected", payload: {} })
      expect(installed.lastRuntime?.ready()).toBe(false)
      // Confirm the runner does not poll while not-ready.
      const callsBefore = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 2)
      expect(poll.mock.calls.length).toBe(callsBefore)
      // Let the in-flight turn settle and report once (no replay).
      actionRelease.resolve()
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(60)
      expect(installed.lastRuntime?.ready()).toBe(true)
      // After rebuild, claiming resumes.
      const callsAfterRebuild = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBeGreaterThan(callsAfterRebuild)
      const reportsForExit = report.mock.calls.filter((call) => call[0]?.workId === "work-exit")
      expect(reportsForExit.length).toBe(1)
    } finally {
      controller.abort()
      actionRelease.resolve()
      console.warn = previousWarn
      await run.catch(() => undefined)
    }
  })

  it("Workflow source receives the OpenCode runtime handle on ActionContext", async () => {
    installReadyOpenCodeRuntimeFactory({
      models: [{ providerID: "openai", modelID: "gpt-5", variants: [] }],
      fetchedAt: 0,
    })
    let observed: { acpConnection: unknown; openCodeRuntime: unknown } | null = null
    const actionStarted = deferred<void>()
    const actionRelease = deferred<void>()
    blockingAction.mockReset().mockImplementation(async (context: { openCodeRuntime?: unknown; acpConnection?: unknown }) => {
      observed = { acpConnection: context.acpConnection, openCodeRuntime: context.openCodeRuntime }
      actionStarted.resolve()
      await actionRelease.promise
      return { status: "success", message: "ok" }
    })
    poll.mockResolvedValueOnce([{
      workflowRunId: "wr-workflow",
      workId: "work-workflow",
      workType: "task",
      uses: "test/observe",
      ownerKind: "workflow",
      variables: workflowVariables(),
    }]).mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions())
    const run = host.run(controller.signal)
    try {
      await actionStarted.promise
      const observedNonNull = observed as { acpConnection: unknown; openCodeRuntime: unknown } | null
      expect(observedNonNull).not.toBeNull()
      expect(observedNonNull?.acpConnection).not.toBeNull()
      expect(observedNonNull?.openCodeRuntime).not.toBeNull()
      const runtime = observedNonNull?.openCodeRuntime as { ready: () => boolean; createSession: (...args: unknown[]) => unknown }
      expect(typeof runtime.ready).toBe("function")
      expect(typeof runtime.createSession).toBe("function")
      expect(runtime.ready()).toBe(true)
    } finally {
      controller.abort()
      actionRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it("AgentJob path receives the ACP connection but NOT the OpenCode runtime handle", async () => {
    installReadyOpenCodeRuntimeFactory({
      models: [{ providerID: "openai", modelID: "gpt-5", variants: [] }],
      fetchedAt: 0,
    })
    let observed: { acpConnection: unknown; openCodeRuntime: unknown } | null = null
    const actionStarted = deferred<void>()
    const actionRelease = deferred<void>()
    blockingAction.mockReset().mockImplementation(async (context: { openCodeRuntime?: unknown; acpConnection?: unknown }) => {
      observed = { acpConnection: context.acpConnection, openCodeRuntime: context.openCodeRuntime }
      actionStarted.resolve()
      await actionRelease.promise
      return { status: "success", message: "ok" }
    })
    poll.mockResolvedValueOnce([{
      workflowRunId: "",
      workId: "work-agent-job",
      workType: "task",
      uses: "test/observe",
      ownerKind: "agent-job",
      agentJobId: "aj-1",
      variables: { workspace: { path: "/tmp/mohist-runner-host-opencode-runtime" } },
    }]).mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions())
    const run = host.run(controller.signal)
    try {
      await actionStarted.promise
      const observedNonNull = observed as { acpConnection: unknown; openCodeRuntime: unknown } | null
      expect(observedNonNull).not.toBeNull()
      expect(observedNonNull?.acpConnection).not.toBeNull()
      // AgentJob path receives `openCodeRuntime = null` even though
      // the runner host has the runtime handle — the source-keyed
      // dispatch in `baseContext` deliberately nulls it. This is the
      // transitional AgentJob ACP path (#410) and the Workflow-only
      // OpenCodeRuntime seam (T-003).
      expect(observedNonNull?.openCodeRuntime).toBeNull()
    } finally {
      controller.abort()
      actionRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it("transitional AgentJob gating: the readiness gate pauses AgentJob claim while runtime is not ready", async () => {
    // Use a long rebuild delay so the gate stays closed throughout
    // the post-flip observation window — we want to verify pollOnce
    // is skipped during the not-ready window, not that rebuild races
    // the assertion.
    const installedHandles = installFakeOpenCodeRuntimeFactory({ rebuildDelayMs: 60_000 })
    let pollCalls = 0
    let gateFlipped = false
    poll.mockImplementation(async () => {
      pollCalls += 1
      if (gateFlipped) return []
      return [{
        workflowRunId: "",
        workId: "work-agent-job",
        workType: "task",
        uses: "test/observe",
        ownerKind: "agent-job",
        agentJobId: "aj-1",
        variables: { workspace: { path: "/tmp/mohist-runner-host-opencode-runtime" } },
      }]
    })
    blockingAction.mockReset().mockResolvedValue({ status: "success", message: "ok" })
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions())
    const previousWarn = console.warn
    console.warn = () => undefined
    const run = host.run(controller.signal)
    try {
      const runtime = await installedHandles.runtimeCreated
      expect(installedHandles.lastRuntime).toBe(runtime)
      // Drive the run loop until the first poll fires.
      for (let i = 0; i < 30 && poll.mock.calls.length === 0; i += 1) {
        await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      }
      const callsBeforeFlip = poll.mock.calls.length
      expect(callsBeforeFlip).toBeGreaterThan(0)
      // Flip the runtime to not-ready. The gate closes for both
      // Workflow AND AgentJob claims under the one-gate transitional
      // rule (design D3). The subscription lives on the fake handles
      // returned by `installFakeOpenCodeRuntimeFactory` — not on the
      // runtime instance itself, which only stores it as private state.
      installedHandles.subscription.emit({ type: "server.disconnected", payload: {} })
      expect(installedHandles.lastRuntime?.ready()).toBe(false)
      gateFlipped = true
      // Drive timers for a few intervals; with the gate closed the
      // poll mock would not be called even though it would return
      // work — proving the gate blocks AgentJob claim too.
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBe(callsBeforeFlip)
    } finally {
      controller.abort()
      console.warn = previousWarn
      await run.catch(() => undefined)
    }
  })
})
