import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import { setOpencodeModelDiscoveryForTest } from "../src/runtime/opencode-models.js"
import type { SessionTarget } from "../src/server/session-target.js"
import type { FollowupTargetResolution } from "../src/server/session-target.js"
import { deferred } from "./support/deferred.js"
import { clearOpenCodeRuntimeFactoryForTest, installReadyOpenCodeRuntimeFactory } from "./support/opencode-runtime-factory.js"

const installReadyRuntimeFactory = installReadyOpenCodeRuntimeFactory

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000

const mocks = vi.hoisted(() => ({
  connect: vi.fn(),
  heartbeat: vi.fn(),
  disconnect: vi.fn(),
  poll: vi.fn(),
  report: vi.fn(),
  uploadTaskLog: vi.fn(),
  fetchConfig: vi.fn(async () => null),
  workflowAgentSessionRuntimeEvents: vi.fn(),
  agentSessionRuntimeEvents: vi.fn(),
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
  workflowAgentSessionRuntimeEvents,
  agentSessionRuntimeEvents,
  startSignalR,
  stopSignalR,
  getConnectionId,
  probeLiveness,
  blockingAction,
  forceReconnect,
} = mocks

let capturedOnReconnected: ((connectionId: string) => void) | null = null
let capturedFollowupTargetResolver: ((target: SessionTarget) => FollowupTargetResolution | Promise<FollowupTargetResolution>) | null = null

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
    report = report
    uploadTaskLog = uploadTaskLog
    fetchConfig = fetchConfig
    workflowAgentSessionRuntimeEvents = workflowAgentSessionRuntimeEvents
    agentSessionRuntimeEvents = agentSessionRuntimeEvents
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

vi.mock("../src/actions/registry.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/actions/registry.js")>()
  return {
    ...actual,
    createDefaultRegistry: () => new actual.ActionRegistry([{
      manifest: {
        name: "test/block",
        inputs: {},
        outputs: [],
        errors: [{ code: "action-failed", description: "The test Action failed" }],
      },
      run: blockingAction,
    }]),
  }
})

beforeEach(() => {
  vi.useFakeTimers()
  installReadyRuntimeFactory()
  setOpencodeModelDiscoveryForTest(async () => ({ models: ["openai/gpt-5.5"], variants: {} }))
  capturedOnReconnected = null
  capturedFollowupTargetResolver = null
  uploadTaskLog.mockResolvedValue({ accepted: 0, truncated: false })
  blockingAction.mockImplementation(async ({ signal }: { signal: AbortSignal }) => {
    const aborted = deferred<{ error: { code: string; message: string } }>()
    if (signal.aborted) {
      aborted.resolve({ error: { code: "action-failed", message: "aborted" } })
    } else {
      signal.addEventListener("abort", () => aborted.resolve({ error: { code: "action-failed", message: "aborted" } }), { once: true })
    }
    return aborted.promise
  })
})

afterEach(() => {
  clearOpenCodeRuntimeFactoryForTest()
})

describe("RunnerHost", () => {
  it("treats 'opencode' (any casing) as the configured runtime", () => {
    // Issue-410 T-004 retired the SessionCommand handler. The runner
    // host no longer wires a sessionCommandHandler on the SignalR
    // client; the only runtime the runner drives end-to-end is
    // OpenCode, regardless of casing in the wire field.
    expect(installReadyOpenCodeRuntimeFactory).toBe(installReadyRuntimeFactory)
    expect(clearOpenCodeRuntimeFactoryForTest).toBeInstanceOf(Function)
  })

  it("RunnerRegistration_DoesNotReportWorkflowSlots", async () => {
    vi.clearAllMocks()
    const connected = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
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
      await connected.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(connect).toHaveBeenCalledWith(
        expect.objectContaining({
          projectId: "project-1",
          coderModels: ["openai/gpt-5.5"],
        }),
        expect.any(AbortSignal),
      )
      expect(Object.keys(connect.mock.calls[0][0]).sort()).toEqual([
        "actionCatalog",
        "buildGitHash",
        "capabilities",
        "coderModelVariants",
        "coderModels",
        "connectionId",
        "projectId",
      ])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("WorkerPool_PollsUntilServerReturnsNoWorkWithoutLocalConcurrencyCap", async () => {
    vi.clearAllMocks()
    const pollCalls = [deferred<void>(), deferred<void>(), deferred<void>(), deferred<void>()]
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const work = (id: string) => ({
      workflowRunId: "",
      workId: `work-${id}`,
      workType: "task",
      uses: "test/block",
      ownerKind: "agent-job",
      agentJobId: `job-${id}`,
      variables: { workspace: { path: "/tmp/mohist-runner-test" } },
    })
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollCalls[pollIndex]?.resolve()
      pollIndex += 1
      if (pollIndex === 4) {
        controller.abort()
        return []
      }
      return [work(String(pollIndex))]
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

    const run = host.run(controller.signal)
    try {
      await pollCalls[0]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[1]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[2]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[3]!.promise
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("WorkerPool_PollFailure_RetriesWithoutRestartingRunner", async () => {
    vi.clearAllMocks()
    const firstPollStarted = deferred<void>()
    const retryPollStarted = deferred<void>()
    const failureLogged = deferred<void>()
    const pollFailure = new Error("server unavailable")
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    poll
      .mockImplementationOnce(async () => {
        firstPollStarted.resolve()
        throw pollFailure
      })
      .mockImplementationOnce(async () => {
        retryPollStarted.resolve()
        controller.abort()
        return []
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
    const warningSpy = vi.spyOn(console, "warn").mockImplementation((message: unknown) => {
      if (typeof message === "string" && message.includes("runner poll failed; retrying")) failureLogged.resolve()
    })
    const run = host.run(controller.signal)

    try {
      await firstPollStarted.promise
      await failureLogged.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await retryPollStarted.promise
      await expect(run).resolves.toBeUndefined()

      expect(connect).toHaveBeenCalledTimes(1)
      expect(startSignalR).toHaveBeenCalledTimes(1)
      expect(warningSpy).toHaveBeenCalledWith(expect.stringContaining("runner poll failed; retrying"), pollFailure)
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      warningSpy.mockRestore()
    }
  })

  it("WorkerPool_PollTimeout_AbortsAttemptAndContinuesPolling", async () => {
    vi.clearAllMocks()
    const firstPollStarted = deferred<void>()
    const retryPollStarted = deferred<void>()
    const pollAbort = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    poll
      .mockImplementationOnce((signal: AbortSignal) => new Promise((_, reject) => {
        firstPollStarted.resolve()
        signal.addEventListener("abort", () => {
          pollAbort.resolve()
          reject(signal.reason)
        }, { once: true })
      }))
      .mockImplementationOnce(async () => {
        retryPollStarted.resolve()
        controller.abort()
        return []
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
    const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
    const run = host.run(controller.signal)

    try {
      await firstPollStarted.promise
      await vi.advanceTimersByTimeAsync(10_000 + POLL_INTERVAL_MS + 1)
      await pollAbort.promise
      await retryPollStarted.promise
      await expect(run).resolves.toBeUndefined()

      expect(warningSpy).toHaveBeenCalledWith(expect.stringContaining("runner poll failed; retrying"), expect.any(Error))
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      warningSpy.mockRestore()
    }
  })

  it("RunnerShutdown_UnregistersRunner", async () => {
    vi.clearAllMocks()
    const connected = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await connected.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))
      expect(stopSignalR).toHaveBeenCalled()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it("RunnerConnection_WhenSignalRFails_DoesNotPollAndRetriesCleanly", async () => {
    vi.clearAllMocks()
    const firstSignalRStarted = deferred<void>()
    const secondSignalRStarted = deferred<void>()
    const secondSignalRRelease = deferred<void>()
    const disconnectedAfterFailure = deferred<void>()
    const firstPollStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-1")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockImplementation(async () => {
      disconnectedAfterFailure.resolve()
    })
    poll.mockImplementation(async () => {
      firstPollStarted.resolve()
      return []
    })
    const signalRUnavailable = new Error("signalr unavailable")
    startSignalR
      .mockImplementationOnce(async () => {
        firstSignalRStarted.resolve()
        throw signalRUnavailable
      })
      .mockImplementationOnce(async () => {
        secondSignalRStarted.resolve()
        await secondSignalRRelease.promise
      })
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const errorSpy = vi.spyOn(console, "error").mockClear().mockImplementation(() => undefined)
    const run = host.run(controller.signal)
    try {
      await firstSignalRStarted.promise
      await disconnectedAfterFailure.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await secondSignalRStarted.promise
      expect(poll).not.toHaveBeenCalled()
      expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))

      secondSignalRRelease.resolve()
      await firstPollStarted.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(errorSpy).toHaveBeenCalledTimes(1)
      expect(errorSpy).toHaveBeenNthCalledWith(
        1,
        `runner connection failed; retrying in ${POLL_INTERVAL_MS}ms`,
        signalRUnavailable,
      )
    } finally {
      secondSignalRRelease.resolve()
      controller.abort()
      await run.catch(() => undefined)
      errorSpy.mockRestore()
    }
  })

  it("GenericFollowupResolver_ProjectsBindingIntoRuntimeTarget", async () => {
    vi.clearAllMocks()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { openCodeRuntime: { ready(): boolean } | null }
    host.openCodeRuntime = { ready: () => true }

    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-from-payload",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    })

    expect(resolved).toEqual({
      runtimeSessionId: "runtime-1",
      workDir: "/tmp/work",
      projectId: "project-from-payload",
    })
  })

  it("GenericFollowupResolver_StartupBeforeRuntimeReady_ResolvesBindingOnly", () => {
    new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    })

    // Issue-461 D1: the resolver is binding-only. The caller (follow-up handler
    // or worker claim) gates admission on runtime readiness; the resolver
    // never inspects runtime or outbox state.
    expect(resolved).toEqual({
      runtimeSessionId: "runtime-1",
      workDir: "/tmp/work",
      projectId: "project-1",
    })
  })

  it("ClaimReadiness_RequiresHealthyRuntimeEventOutbox", () => {
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as {
      openCodeRuntime: { ready(): boolean } | null
      agentSessionRuntimeEventOutbox: { ready(): boolean }
      isOpenCodeReadyForClaim(): boolean
    }
    host.openCodeRuntime = { ready: () => true }
    host.agentSessionRuntimeEventOutbox = { ready: () => false }

    expect(host.isOpenCodeReadyForClaim()).toBe(false)

    host.agentSessionRuntimeEventOutbox = { ready: () => true }
    expect(host.isOpenCodeReadyForClaim()).toBe(true)
  })

  it("GenericFollowupResolver_NonOpencodeBinding_IsMissingTarget", () => {
    new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { openCodeRuntime: { ready(): boolean } | null }
    ;(capturedFollowupTargetResolver as unknown as { openCodeRuntime: { ready(): boolean } | null } | null)
    // legacy ACP-bound session: the runner must report missing
    // (per issue-410 D6 — Reset hint), not forward to the runtime
    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "acp",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    } as SessionTarget)

    expect(resolved).toBeNull()
  })

  it("GenericFollowupResolver_MismatchedRunnerId_IsMissingTarget", () => {
    new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "different-runner",
        workDir: "/tmp/work",
      },
    })

    expect(resolved).toBeNull()
  })

  it("GenericFollowupResolver_MissingWorkDir_IsMissingTarget", () => {
    new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: null,
      },
    })

    expect(resolved).toBeNull()
  })

  it("GenericFollowupResolver_NoBinding_IsMissingTarget", () => {
    new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    })

    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "project-1",
      sessionId: "gen-1",
    })

    expect(resolved).toBeNull()
  })

  it("GenericFollowupResolver_RejectsMismatchedConfiguredRunnerProject", async () => {
    vi.clearAllMocks()
    const host = new RunnerHost({
      serverUrl: "http://localhost:3456",
      runnerId: "runner-test",
      projectId: "runner-project",
      runnerRoot: "/tmp/mohist-runner-test",
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as { openCodeRuntime: { ready(): boolean } | null }
    host.openCodeRuntime = { ready: () => true }

    const resolved = capturedFollowupTargetResolver?.({
      kind: "generic",
      projectId: "other-project",
      sessionId: "gen-1",
      binding: {
        runtime: "opencode",
        runtimeSessionId: "runtime-1",
        runnerId: "runner-test",
        workDir: "/tmp/work",
      },
    })

    expect(resolved).toBeNull()
  })
})
