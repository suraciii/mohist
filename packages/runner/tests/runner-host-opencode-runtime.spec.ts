import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { PiRuntime } from "../src/runtime/pi/index.js"
import type { ActionDefinition } from "../src/actions/manifest.js"
import { deferred } from "./support/deferred.js"
import { capturedLogs } from "./support/logger-test.js"
import type { GitRunner } from "../src/runtime/git-probe.js"
import { UnexpectedConsoleRecorder } from "./support/unexpected-console.js"
import { FakeTerminalTaskLogDeliveryStore } from "./support/terminal-task-log-delivery.js"
import {
  installFakeOpenCodeRuntimeFactory,
  installReadyOpenCodeRuntimeFactory,
  type OpenCodeRuntimeTestResources,
} from "./support/opencode-runtime-factory.js"
import { withTestRunnerResources } from "./support/test-resources.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import type { RunnerFileSystem } from "../src/system/filesystem.js"
import type { ExternalProcessPolicy } from "../src/system/process-policy.js"
import type { RunnerLogger } from "../src/system/logger.js"
import { createLoggerCapture } from "./support/logger-test.js"

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000

const nonGitRunner: GitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: "",
  stderr: "not a git repository",
  combinedOutput: "not a git repository",
})

type HostMock = ReturnType<typeof vi.fn>
type HostMocks = Record<"connect" | "heartbeat" | "disconnect" | "poll" | "report" | "uploadTaskLog" | "fetchConfig" | "startSignalR" | "stopSignalR" | "getConnectionId" | "probeLiveness" | "blockingAction" | "forceReconnect", HostMock>

interface HostMockTestState {
  readonly mocks: HostMocks
}

const hostMockStorage = new AsyncLocalStorage<HostMockTestState>()

function currentHostMockTestState(): HostMockTestState {
  const state = hostMockStorage.getStore()
  if (!state) throw new Error("runner host mock resource context is not active")
  return state
}

function scopedMock(name: keyof HostMocks): HostMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, "_isMockFunction", { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentHostMockTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentHostMockTestState().mocks[name], property)
      return typeof value === "function" ? value.bind(currentHostMockTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentHostMockTestState().mocks[name], property, value)
    },
  }) as unknown as HostMock
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
      description: name === "test/catalog" ? "Catalog test Action" : undefined,
      inputs: name === "test/catalog" ? {
        prompt: { types: ["string", "object"] as const, required: true as const, description: "Prompt value" },
        timeout: { types: ["number"] as const, default: 30, description: "Timeout in milliseconds" },
      } : {},
      outputs: name === "test/catalog" ? [{ name: "public", description: "Public result" }] : [],
      errors: [{ code: "action-failed", description: "The test Action failed" }],
    },
    run: blockingAction,
  }) as unknown as ActionDefinition
  return {
    ...actual,
    createDefaultRegistry: () => new actual.ActionRegistry([
      definition("test/block"),
      definition("test/observe"),
      definition("test/catalog"),
    ]),
  }
})

vi.mock("../src/runtime/workspace.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/runtime/workspace.js")>()
  class FakeWorkspaceManager {
    async prepare() {
      return { path: "/virtual/mohist-runner-host-opencode-runtime", branch: "main", changeDir: null }
    }
    async verify() {
      return { path: "/virtual/mohist-runner-host-opencode-runtime", branch: "main", changeDir: null }
    }
  }
  return {
    ...actual,
    WorkspaceManager: FakeWorkspaceManager,
  }
})

function createHostMocks(): HostMocks {
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
    blockingAction: vi.fn(),
    forceReconnect: vi.fn(async () => undefined),
  }
}

interface HostTestResources extends OpenCodeRuntimeTestResources {
  fileSystem: RunnerFileSystem
  gitRunner: GitRunner
  logger: RunnerLogger
  externalProcessPolicy: ExternalProcessPolicy
  piRuntimeFactory?: () => PiRuntime
}

function it(name: string, body: (resources: HostTestResources) => Promise<void>): void {
  vitestIt(name, async () => {
    const resources: HostTestResources = {
      fileSystem: new MemoryFileSystem(),
      gitRunner: nonGitRunner,
      logger: createLoggerCapture(),
      externalProcessPolicy: {
        assertAllowed(label) {
          throw new Error(`external process forbidden in runner host test: ${label}`)
        },
        register() {},
      },
      piRuntimeFactory: () => ({
        start: async () => ({ ok: true, value: { ready: true, diagnostic: null, catalog: { models: [] } }, diagnostics: [] }),
        ready: () => true,
        diagnostic: () => null,
        catalog: () => ({ models: [] }),
        createSession: async () => ({ ok: true, value: { runtimeSessionId: "/virtual/pi-session", workDir: "/virtual" }, diagnostics: [] }),
        runTurn: async () => ({ ok: true, value: { facts: { finalAssistantText: null, runtimeSessionId: "/virtual/pi-session", workDir: "/virtual" }, diagnostics: [] }, diagnostics: [] }),
        shutdown: async () => {},
      } as never),
    }
    await withTestRunnerResources(async () => {
      await hostMockStorage.run({ mocks: createHostMocks() }, async () => {
        vi.useFakeTimers()
        try {
          installReadyOpenCodeRuntimeFactory(resources)
          await body(resources)
        } finally {
          vi.useRealTimers()
        }
      })
    }, resources)
  })
}

function hostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    serverUrl: "https://runner.test",
    runnerId: "runner-test",
    projectId: "project-1",
    runnerRoot: "/virtual/mohist-runner-host-opencode-runtime",
    pollIntervalMs: POLL_INTERVAL_MS,
    heartbeatIntervalMs: QUIET_INTERVAL_MS,
    dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
  }
}

function hostWithFakeTerminalDelivery(): RunnerHost {
  return new RunnerHost(hostOptions(), undefined, { terminalTaskLogDelivery: new FakeTerminalTaskLogDeliveryStore() })
}

function workflowVariables(): Record<string, unknown> {
  return {
    repository: { gitUrl: "https://example.com/repo.git", baseBranch: "main" },
    issue: { number: 1 },
    workspace: { path: "/virtual/mohist-runner-host-opencode-runtime" },
    mohist: { runId: "wr-test" },
  }
}

function expectedActionCatalog() {
  const error = { code: "action-failed", description: "The test Action failed" }
  return {
    actions: [
      { name: "test/block", inputs: [], outputs: [], errors: [error] },
      {
        name: "test/catalog",
        description: "Catalog test Action",
        inputs: [
          { name: "prompt", types: ["string", "object"], required: true, description: "Prompt value" },
          { name: "timeout", types: ["number"], required: false, default: 30, description: "Timeout in milliseconds" },
        ],
        outputs: [{ name: "public", description: "Public result" }],
        errors: [error],
      },
      { name: "test/observe", inputs: [], outputs: [], errors: [error] },
    ],
    tombstones: [],
  }
}

describe("RunnerHost wires the OpenCodeRuntime lifecycle", () => {
  it("keeps polling for new work after an unowned runtime has cooled down", async (resources) => {
    const installed = installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    connect.mockImplementation(async () => { connected.resolve() })
    poll.mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost({
      ...hostOptions(),
      runtimeIdleGraceMs: 50,
    })
    const run = host.run(controller.signal)
    try {
      await connected.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      expect(installed.lastRuntime?.ready()).toBe(true)
      await vi.advanceTimersByTimeAsync(50)
      expect(installed.lastRuntime?.ready()).toBe(false)
      const callsAfterCooling = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 2)
      expect(poll.mock.calls.length).toBeGreaterThan(callsAfterCooling)
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("ready-claim: starts the runtime without probing or registering a model catalog", async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    connect.mockImplementation(async () => { connected.resolve() })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await connected.promise
      const connectArg = connect.mock.calls[0]?.[0] as Record<string, unknown>
      expect(connectArg).not.toHaveProperty("coderModels")
      expect(connectArg).not.toHaveProperty("coderModelVariants")
      expect(connectArg).not.toHaveProperty("runtimeCatalogs")
      expect(connectArg?.actionCatalog).toEqual(expectedActionCatalog())
      expect(JSON.stringify(connectArg?.actionCatalog)).not.toContain("run")
      expect(JSON.stringify(connectArg?.actionCatalog)).not.toContain("private")
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("RunnerRegistration does not read runtime model catalogs", async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const piCatalog = {
      models: [
        { provider: "anthropic", id: "claude-sonnet-4", thinkingLevels: ["off"] },
        { provider: "openai", id: "gpt-5.5", thinkingLevels: ["low", "high"] },
      ],
    }
    const catalog = vi.fn(() => piCatalog)
    const piRuntime = {
      start: vi.fn(async () => ({ ok: true as const, value: { ready: true, diagnostic: null, catalog: piCatalog }, diagnostics: [] })),
      ready: () => true,
      diagnostic: () => null,
      catalog,
      shutdown: vi.fn(async () => undefined),
    } as unknown as PiRuntime
    resources.piRuntimeFactory = () => piRuntime
    const connected = deferred<void>()
    connect.mockImplementation(async () => { connected.resolve() })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await connected.promise
      const registration = connect.mock.calls[0]?.[0] as Record<string, unknown>
      expect(registration).not.toHaveProperty("coderModels")
      expect(registration).not.toHaveProperty("coderModelVariants")
      expect(registration).not.toHaveProperty("runtimeCatalogs")
      expect(catalog).not.toHaveBeenCalled()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("RunnerRegistration omits model catalogs on every heartbeat", async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    connect.mockImplementation(async () => { connected.resolve() })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await connected.promise
      // Drive a heartbeat tick to confirm the registration body keeps
      // carrying the host-owned discovered snapshot.
      await vi.advanceTimersByTimeAsync(QUIET_INTERVAL_MS + 10)
      const heartbeatBodies = heartbeat.mock.calls.map((call) => call[0] as Record<string, unknown>)
      expect(heartbeatBodies.length).toBeGreaterThan(0)
      for (const body of heartbeatBodies) {
        expect(body).not.toHaveProperty("coderModels")
        expect(body).not.toHaveProperty("coderModelVariants")
        expect(body).not.toHaveProperty("runtimeCatalogs")
        expect(body.actionCatalog).toEqual(expectedActionCatalog())
      }
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("not-ready-skip: when the runtime flips to not-ready mid-flight, pollOnce stops and the existing report still drains", async (resources) => {
    // Start with a ready runtime; let the first poll dispatch and
    // capture the work item's report; then simulate a server exit
    // and confirm no further polls run until the runtime recovers.
    const installed = installFakeOpenCodeRuntimeFactory(resources, { rebuildDelayMs: 50 })
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
    blockingAction.mockReset().mockResolvedValue({ output: { message: "ok" } })
    poll.mockResolvedValueOnce([{
      workflowRunId: "wr-drain",
      workId: "work-drain",
      workType: "task",
      uses: "test/block",
      ownerKind: "workflow",
      variables: workflowVariables(),
    }]).mockResolvedValue([])
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    // Capture the readiness-diagnostic warn so it doesn't trip the
    // unexpected-console recorder — the diagnostic IS the expected
    // signal the test verifies.
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
      expect(capturedLogs()).toEqual(expect.arrayContaining([
        expect.objectContaining({ level: "WARN", message: "runner not ready; skipping poll", fields: expect.objectContaining({ reason: expect.stringContaining("opencode runtime not ready (server-exit)") }) }),
      ]))
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
      await run.catch(() => undefined)
    }
  })

  it("server-exit-rebuild-resume: in-flight Workflow turns fail without auto-replay and claiming resumes after rebuild", async (resources) => {
    const installed = installFakeOpenCodeRuntimeFactory(resources, { rebuildDelayMs: 50 })
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
      return { output: { message: "ok" } }
    })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
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
      await run.catch(() => undefined)
    }
  })

  it("Workflow source does not receive the OpenCode runtime handle", async (resources) => {
    installReadyOpenCodeRuntimeFactory(resources)
    let observed: { openCodeRuntime: unknown } | null = null
    const actionStarted = deferred<void>()
    const actionRelease = deferred<void>()
    blockingAction.mockReset().mockImplementation(async (_inputs: unknown, context: { openCodeRuntime?: unknown }) => {
      observed = { openCodeRuntime: context.openCodeRuntime }
      actionStarted.resolve()
      await actionRelease.promise
      return { output: { message: "ok" } }
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
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await actionStarted.promise
      const observedNonNull = observed as { openCodeRuntime: unknown } | null
      expect(observedNonNull).not.toBeNull()
      expect(observedNonNull?.openCodeRuntime).toBeUndefined()
    } finally {
      controller.abort()
      actionRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it("AgentJob path drives the AgentJobExecutor, not the action registry", async (resources) => {
    installReadyOpenCodeRuntimeFactory(resources)
    // Verify the source-keyed dispatch wiring at the executor
    // boundary directly: an AgentJob ownerKind resolves through
    // the AgentJobExecutor entry instead of the action registry.
    // The full run-loop wiring is exercised by
    // `tests/agent-job-executor.spec.ts`; here we just need to
    // confirm the executor branches on owner-kind BEFORE the
    // action registry is consulted.
    let registryInvoked = false
    blockingAction.mockReset().mockImplementation(async () => {
      registryInvoked = true
      return { status: "success", message: "should-not-reach" }
    })
    // Use the WorkExecutor directly so we don't drive the run loop.
    const { WorkExecutor } = await import("../src/runtime/executor.js")
    const { AgentJobExecutor } = await import("../src/runtime/agent-job-executor.js")
    const fakeRuntime = {
      ready: () => true,
      diagnostic: () => null,
      async runTurn() {
        return {
          ok: true,
          value: {
            facts: { finalAssistantText: "agent done", runtimeSessionId: "ses_x", workDir: "/virtual/agent-job" },
            diagnostics: [],
          },
          diagnostics: [],
        }
      },
    } as never
    const executor = new WorkExecutor(
      {
        resolve: (uses?: string | null) => {
          if (uses === "test/observe") return blockingAction
          return undefined
        },
      } as never,
      {
        async prepare() {
          return { path: "/virtual/agent-job", branch: null, changeDir: null }
        },
      } as never,
      {
        async attachAgentSession() {
          return undefined
        },
        async getAgentSession() {
          return null
        },
      } as never,
      "/virtual/agent-job",
      undefined,
      fakeRuntime,
      new AgentJobExecutor({} as never, { openCode: fakeRuntime, pi: null }),
    )
    const result = await executor.execute(
      {
        workflowRunId: "",
        workId: "aj-1",
        workType: "task",
        ownerKind: "agent-job",
        agentJobId: "aj-1",
        with: { prompt: "do the agent-job thing" },
        variables: { workspace: { path: "/virtual/agent-job", branch: null, changeDir: null } },
      },
      new AbortController().signal,
    )
    expect(result.status).toBe("completed")
    expect(registryInvoked).toBe(false)
  })

  it("the readiness gate pauses AgentJob claim while runtime is not ready", async (resources) => {
    // Use a long rebuild delay so the gate stays closed throughout
    // the post-flip observation window — we want to verify pollOnce
    // is skipped during the not-ready window, not that rebuild races
    // the assertion. The poll mock returns the AgentJob dispatch
    // exactly once followed by empty arrays so the dispatch loop
    // can't tight-loop on the same work key (#410 T-001: the
    // AgentJobExecutor closes the work within a few microtasks, so
    // awaitingAck is empty before the next poll tick).
    const installedHandles = installFakeOpenCodeRuntimeFactory(resources, { rebuildDelayMs: 60_000 })
    poll.mockResolvedValueOnce([{
      workflowRunId: "",
      workId: "work-agent-job",
      workType: "task",
      uses: "test/observe",
      ownerKind: "agent-job",
      agentJobId: "aj-1",
      variables: { workspace: { path: "/virtual/mohist-runner-host-opencode-runtime" } },
    }]).mockResolvedValue([])
    blockingAction.mockReset().mockResolvedValue({ output: { message: "ok" } })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
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
      // Workflow AND AgentJob claims under the one-gate rule (design
      // D3, #410 T-001 D4). The subscription lives on the fake
      // handles returned by `installFakeOpenCodeRuntimeFactory` — not
      // on the runtime instance itself, which only stores it as
      // private state.
      installedHandles.subscription.emit({ type: "server.disconnected", payload: {} })
      expect(installedHandles.lastRuntime?.ready()).toBe(false)
      // Drive timers for a few intervals; with the gate closed the
      // poll mock would not be called even though it would return
      // work — proving the gate blocks AgentJob claim too.
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBe(callsBeforeFlip)
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })
})
