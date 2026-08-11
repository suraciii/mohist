import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import type { SessionTarget } from "../src/server/session-target.js"
import { deferred } from "./support/deferred.js"
import { capturedLogs } from "./support/logger-test.js"
import { withDefaultRunnerTestResources, type DefaultRunnerTestResources } from "./support/test-resources.js"

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000
const HEARTBEAT_INTERVAL_MS = 10
const SELF_CHECK_INTERVAL_MS = 10

type LivenessMock = ReturnType<typeof vi.fn>
type LivenessMocks = Record<
  "connect" | "heartbeat" | "disconnect" | "poll" | "report" | "uploadTaskLog" | "fetchConfig" |
  "listAgentSessionsForReconcile" | "reconcileMissingAgentSession" | "reconcileAgentSessionRuntimeEvents" |
  "startSignalR" | "stopSignalR" | "getConnectionId" | "probeLiveness" | "blockingAction" | "forceReconnect",
  LivenessMock
>

interface LivenessTestState {
  readonly resources: DefaultRunnerTestResources
  readonly mocks: LivenessMocks
  onReconnected: ((connectionId: string) => void) | null
  followupTargetResolver: ((target: SessionTarget) => { runtimeSessionId: string; workDir: string; projectId: string } | null) | null
}

const livenessTestStorage = new AsyncLocalStorage<LivenessTestState>()

function currentLivenessTestState(): LivenessTestState {
  const state = livenessTestStorage.getStore()
  if (!state) throw new Error("runner host liveness test context is not active")
  return state
}

function scopedMock(name: keyof LivenessMocks): LivenessMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, "_isMockFunction", { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentLivenessTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentLivenessTestState().mocks[name], property)
      return typeof value === "function" ? value.bind(currentLivenessTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentLivenessTestState().mocks[name], property, value)
    },
  }) as unknown as LivenessMock
}

const connect = scopedMock("connect")
const heartbeat = scopedMock("heartbeat")
const disconnect = scopedMock("disconnect")
const poll = scopedMock("poll")
const report = scopedMock("report")
const uploadTaskLog = scopedMock("uploadTaskLog")
const fetchConfig = scopedMock("fetchConfig")
const listAgentSessionsForReconcile = scopedMock("listAgentSessionsForReconcile")
const reconcileMissingAgentSession = scopedMock("reconcileMissingAgentSession")
const reconcileAgentSessionRuntimeEvents = scopedMock("reconcileAgentSessionRuntimeEvents")
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
    listAgentSessionsForReconcile = listAgentSessionsForReconcile
    reconcileMissingAgentSession = reconcileMissingAgentSession
    reconcileAgentSessionRuntimeEvents = reconcileAgentSessionRuntimeEvents
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = startSignalR
    stop = stopSignalR
    getConnectionId = getConnectionId
    probeLiveness = probeLiveness
    forceReconnect = forceReconnect
    constructor(_serverUrl: string, _runnerId: string, _runnerRoot: string, _buildGitHash: string | null, options: { onReconnected?: (id: string) => void; followupTargetResolver?: (target: SessionTarget) => { runtimeSessionId: string; workDir: string; projectId: string } | null } = {}) {
      currentLivenessTestState().onReconnected = options.onReconnected ?? null
      currentLivenessTestState().followupTargetResolver = options.followupTargetResolver ?? null
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
      run: blockingAction as never,
    }]),
  }
})

function createLivenessMocks(): LivenessMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => undefined),
    uploadTaskLog: vi.fn(async () => ({ status: "changed", accepted: 0, truncated: false })),
    fetchConfig: vi.fn(async () => null),
    listAgentSessionsForReconcile: vi.fn(async () => []),
    reconcileMissingAgentSession: vi.fn(async () => undefined),
    reconcileAgentSessionRuntimeEvents: vi.fn(async () => []),
    startSignalR: vi.fn(async () => undefined),
    stopSignalR: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => "conn-1"),
    probeLiveness: vi.fn(async () => true),
    blockingAction: vi.fn(async ({ signal }: { signal: AbortSignal }) => {
      const aborted = deferred<{ error: { code: string; message: string } }>()
      if (signal.aborted) {
        aborted.resolve({ error: { code: "action-failed", message: "aborted" } })
      } else {
        signal.addEventListener("abort", () => aborted.resolve({ error: { code: "action-failed", message: "aborted" } }), { once: true })
      }
      return aborted.promise
    }),
    forceReconnect: vi.fn(async () => undefined),
  }
}

function it(name: string, body: () => Promise<void> | void): void {
  vitestIt(name, async () => {
    await withDefaultRunnerTestResources(async (resources) => {
      const state: LivenessTestState = { resources, mocks: createLivenessMocks(), onReconnected: null, followupTargetResolver: null }
      await livenessTestStorage.run(state, async () => {
        vi.useFakeTimers()
        try {
          await body()
        } finally {
          vi.useRealTimers()
        }
      })
    })
  })
}

describe("RunnerHost", () => {
  it("HeartbeatCarriesCurrentConnectionId_OnHeartbeatTick", async () => {
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const heartbeatSent = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockImplementation(async () => {
      heartbeatSent.resolve()
    })
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: HEARTBEAT_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(HEARTBEAT_INTERVAL_MS)
      await heartbeatSent.promise
      expect(heartbeat).toHaveBeenCalledWith(
        expect.objectContaining({ connectionId: "conn-A" }),
        expect.any(AbortSignal),
      )
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })

  it("SelfCheckTimer_ProbesAndForceReconnects_OnProbeFailure", async () => {
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const reconnectStarted = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValueOnce(false)
    forceReconnect.mockImplementation(async () => {
      reconnectStarted.resolve()
    })
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      await reconnectStarted.promise

      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()

      expect(capturedLogs()).toEqual(expect.arrayContaining([
        expect.objectContaining({ level: "WARN", message: "dispatch liveness probe failed; forcing reconnect", fields: { reason: "liveness" } }),
      ]))
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })

  it("SelfCheckTimer_SendsImmediateHeartbeat_WhenManualReconnectReportsNewConnection", async () => {
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const reconnectStarted = deferred<void>()
    const immediateHeartbeat = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValueOnce(false).mockResolvedValue(true)
    forceReconnect.mockImplementation(async () => {
      reconnectStarted.resolve()
      getConnectionId.mockReturnValue("conn-AFTER")
      currentLivenessTestState().onReconnected?.("conn-AFTER")
      return undefined
    })
    connect.mockResolvedValue(undefined)
    heartbeat.mockImplementation(async () => {
      immediateHeartbeat.resolve()
    })
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      await reconnectStarted.promise
      await immediateHeartbeat.promise
      expect(heartbeat).toHaveBeenCalledWith(
        expect.objectContaining({ connectionId: "conn-AFTER" }),
        expect.any(AbortSignal),
      )
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()

      expect(capturedLogs()).toEqual(expect.arrayContaining([
        expect.objectContaining({ level: "WARN", message: "dispatch liveness probe failed; forcing reconnect", fields: { reason: "liveness" } }),
      ]))
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })

  it("SelfCheckTimer_DoesNotReconnect_OnProbeSuccess", async () => {
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      expect(probeLiveness).toHaveBeenCalledTimes(2)
      expect(forceReconnect).not.toHaveBeenCalled()
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })

  it("OnReconnected_InvokesImmediateHeartbeatOnce", async () => {
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    const immediateHeartbeat = deferred<void>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockImplementation(async () => {
      immediateHeartbeat.resolve()
    })
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      expect(currentLivenessTestState().onReconnected).toBeTypeOf("function")
      getConnectionId.mockReturnValue("conn-AFTER")
      currentLivenessTestState().onReconnected!("conn-AFTER")

      await immediateHeartbeat.promise
      const lastHeartbeat = heartbeat.mock.calls.at(-1)!
      const lastState = lastHeartbeat[0] as { connectionId?: string }
      expect(lastState.connectionId).toBe("conn-AFTER")
      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })

  it("SelfCheckTimer_ClearedOnShutdown_NoLeakAcrossReconnectLoops", async () => {
    const pollStarted = deferred<void>()
    const pollRelease = deferred<[]>()
    getConnectionId.mockReturnValue("conn-A")
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockImplementationOnce(async () => {
      pollStarted.resolve()
      return pollRelease.promise
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: "https://runner.test",
      runnerId: "runner-test",
      runnerRoot: "/virtual/mohist-runner-test",
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: SELF_CHECK_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await pollStarted.promise
      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS)
      expect(probeLiveness).toHaveBeenCalledTimes(1)

      controller.abort()
      pollRelease.resolve([])
      await expect(run).resolves.toBeUndefined()
      const probeCountAtShutdown = probeLiveness.mock.calls.length

      await vi.advanceTimersByTimeAsync(SELF_CHECK_INTERVAL_MS * 2)
      expect(probeLiveness.mock.calls.length).toBe(probeCountAtShutdown)
    } finally {
      controller.abort()
      pollRelease.resolve([])
      await run.catch(() => undefined)
    }
  })
})
