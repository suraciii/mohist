import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import { deferred } from "./support/deferred.js"
import { clearOpenCodeRuntimeFactoryForTest } from "./support/opencode-runtime-factory.js"
import { setOpenCodeRuntimeFactoryForTest } from "../src/runtime/opencode/index.js"
import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeResult,
} from "../src/runtime/opencode/index.js"

const mocks = vi.hoisted(() => ({
  connect: vi.fn(),
  heartbeat: vi.fn(),
  disconnect: vi.fn(),
  poll: vi.fn(),
  report: vi.fn(),
  uploadTaskLog: vi.fn(),
  fetchConfig: vi.fn(async () => null),
  workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
  agentSessionRuntimeEvents: vi.fn(async () => undefined),
  startSignalR: vi.fn(),
  stopSignalR: vi.fn(),
  getConnectionId: vi.fn(() => "conn-1"),
  probeLiveness: vi.fn(async () => true),
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
  forceReconnect,
} = mocks

interface CapturedSignalROptions {
  followupTargetResolver: unknown
  agentSessionRuntimeEventOutbox: unknown
  openCodeRuntime: unknown
}

let capturedSignalROptions: CapturedSignalROptions | null = null

vi.mock("../src/server/connection.js", () => ({
  ServerConnection: class {
    connect = mocks.connect
    heartbeat = mocks.heartbeat
    disconnect = mocks.disconnect
    poll = mocks.poll
    report = mocks.report
    uploadTaskLog = mocks.uploadTaskLog
    fetchConfig = mocks.fetchConfig
    workflowAgentSessionRuntimeEvents = mocks.workflowAgentSessionRuntimeEvents
    agentSessionRuntimeEvents = mocks.agentSessionRuntimeEvents
  },
}))

vi.mock("../src/server/runner-signalr.js", () => ({
  RunnerSignalRClient: class {
    start = mocks.startSignalR
    stop = mocks.stopSignalR
    getConnectionId = mocks.getConnectionId
    probeLiveness = mocks.probeLiveness
    forceReconnect = mocks.forceReconnect
    constructor(
      _serverUrl: string,
      _runnerId: string,
      _runnerRoot: string,
      _buildGitHash: string | null,
      options: {
        followupTargetResolver?: unknown
        agentSessionRuntimeEventOutbox?: unknown
        openCodeRuntime?: unknown
      } = {},
    ) {
      capturedSignalROptions = {
        followupTargetResolver: options.followupTargetResolver ?? null,
        agentSessionRuntimeEventOutbox: options.agentSessionRuntimeEventOutbox ?? null,
        openCodeRuntime: options.openCodeRuntime ?? null,
      }
    }
  },
}))

vi.mock("../src/actions/registry.js", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../src/actions/registry.js")>()
  return {
    ...actual,
    createDefaultRegistry: () => new actual.ActionRegistry([]),
  }
})


interface StubRuntimeOptions {
  ready?: boolean
  followup?: (request: RuntimeFollowupRequest) => RuntimeResult<RuntimeFollowupResult>
  cancel?: (request: RuntimeCancelRequest) => RuntimeResult<RuntimeCancelResult>
}

function installStubRuntimeFactory(_options: StubRuntimeOptions = {}): OpenCodeRuntime {
  const ready = _options.ready ?? true
  const followupImpl = _options.followup ?? (() => ({
    ok: true as const,
    value: { facts: { runtimeSessionId: "ses_runtime", workDir: "/work" }, diagnostics: [] },
    diagnostics: [],
  }))
  const cancelImpl = _options.cancel ?? (() => ({
    ok: true as const,
    value: { facts: { runtimeSessionId: "ses_runtime", workDir: "/work", cancelled: true }, diagnostics: [] },
    diagnostics: [],
  }))
  const stub: OpenCodeRuntime = {
    ready: () => ready,
    diagnostic: () => null,
    async start() {
      return { ok: true, value: { ready: true, diagnostic: null }, diagnostics: [] }
    },
    async shutdown() {
      // noop
    },
    async followup(request: RuntimeFollowupRequest) {
      return followupImpl(request)
    },
    async cancel(request: RuntimeCancelRequest) {
      return cancelImpl(request)
    },
  } as unknown as OpenCodeRuntime
  setOpenCodeRuntimeFactoryForTest(() => stub)
  return stub
}

beforeEach(() => {
  vi.useFakeTimers()
  capturedSignalROptions = null
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
  workflowAgentSessionRuntimeEvents.mockReset().mockResolvedValue(undefined)
  agentSessionRuntimeEvents.mockReset().mockResolvedValue(undefined)
})

afterEach(() => {
  clearOpenCodeRuntimeFactoryForTest()
})

function hostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    serverUrl: "http://localhost:3456",
    runnerId: "runner-test",
    projectId: "project-1",
    runnerRoot: "/tmp/mohist-runner-host-opencode-handlers",
    pollIntervalMs: 10,
    heartbeatIntervalMs: 60_000,
    dispatchLivenessProbeIntervalMs: 60_000,
  }
}

async function startHost(): Promise<RunnerHost> {
  const connected = deferred<void>()
  connect.mockImplementation(async () => {
    connected.resolve()
  })
  const host = new RunnerHost(hostOptions())
  const controller = new AbortController()
  const run = host.run(controller.signal)
  await connected.promise
  // Drop the dangling run promise on test exit.
  controller.abort()
  void run.catch(() => undefined)
  return host
}

describe("RunnerHost wires OpenCodeRuntime into SignalR followup/cancel handlers", () => {
  it("constructs RunnerSignalRClient with the OpenCodeRuntime handle via an accessor", async () => {
    const stub = installStubRuntimeFactory()
    await startHost()

    expect(capturedSignalROptions).not.toBeNull()
    const accessor = capturedSignalROptions?.openCodeRuntime as
      | OpenCodeRuntime
      | (() => OpenCodeRuntime | null)
      | null
    expect(typeof accessor).toBe("function")
    const resolvedRuntime = (accessor as () => OpenCodeRuntime | null)()
    expect(resolvedRuntime).toBe(stub)
  })

  it("the OpenCodeRuntime accessor returns the live runtime handle (not a snapshot)", async () => {
    const stub = installStubRuntimeFactory()
    await startHost()

    const accessor = capturedSignalROptions?.openCodeRuntime as () => OpenCodeRuntime | null
    expect(accessor()).toBe(stub)
  })

  it("passes the followup target resolver and runtime-event outbox through to RunnerSignalRClient", async () => {
    installStubRuntimeFactory()
    await startHost()

    expect(capturedSignalROptions).not.toBeNull()
    expect(typeof capturedSignalROptions?.followupTargetResolver).toBe("function")
    expect(capturedSignalROptions?.agentSessionRuntimeEventOutbox).not.toBeNull()
  })
})