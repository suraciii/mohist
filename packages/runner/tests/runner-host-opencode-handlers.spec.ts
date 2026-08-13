import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerHost } from "../src/runtime/host.js"
import { deferred } from "./support/deferred.js"
import type { PiRuntime } from "../src/runtime/pi/index.js"
import type { RunnerFileSystem } from "../src/system/filesystem.js"
import type { RunnerLogger } from "../src/system/logger.js"
import type { ExternalProcessPolicy } from "../src/system/process-policy.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { createLoggerCapture } from "./support/logger-test.js"
import { withTestRunnerResources } from "./support/test-resources.js"
import type { OpenCodeRuntimeTestResources } from "./support/opencode-runtime-factory.js"
import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeResult,
} from "../src/runtime/opencode/index.js"

type HandlerMock = ReturnType<typeof vi.fn>
type HandlerMocks = Record<
  "connect" | "heartbeat" | "disconnect" | "poll" | "report" | "uploadTaskLog" | "fetchConfig" |
  "workflowAgentSessionRuntimeEvents" | "agentSessionRuntimeEvents" | "startSignalR" | "stopSignalR" |
  "getConnectionId" | "probeLiveness" | "forceReconnect",
  HandlerMock
>

interface CapturedSignalROptions {
  followupTargetResolver: unknown
  agentSessionRuntimeEventOutbox: unknown
  openCodeRuntime: unknown
}

interface HandlerTestResources extends OpenCodeRuntimeTestResources {
  fileSystem: RunnerFileSystem
  logger: RunnerLogger
  externalProcessPolicy: ExternalProcessPolicy
  piRuntimeFactory: () => PiRuntime
}

interface HandlerTestState {
  readonly mocks: HandlerMocks
  capturedSignalROptions: CapturedSignalROptions | null
}

const handlerTestStorage = new AsyncLocalStorage<HandlerTestState>()

function currentHandlerTestState(): HandlerTestState {
  const state = handlerTestStorage.getStore()
  if (!state) throw new Error("runner host opencode handler test context is not active")
  return state
}

function scopedMock(name: keyof HandlerMocks): HandlerMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, "_isMockFunction", { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentHandlerTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentHandlerTestState().mocks[name], property)
      return typeof value === "function" ? value.bind(currentHandlerTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentHandlerTestState().mocks[name], property, value)
    },
  }) as unknown as HandlerMock
}

const connect = scopedMock("connect")
const heartbeat = scopedMock("heartbeat")
const disconnect = scopedMock("disconnect")
const poll = scopedMock("poll")
const report = scopedMock("report")
const uploadTaskLog = scopedMock("uploadTaskLog")
const fetchConfig = scopedMock("fetchConfig")
const workflowAgentSessionRuntimeEvents = scopedMock("workflowAgentSessionRuntimeEvents")
const agentSessionRuntimeEvents = scopedMock("agentSessionRuntimeEvents")
const startSignalR = scopedMock("startSignalR")
const stopSignalR = scopedMock("stopSignalR")
const getConnectionId = scopedMock("getConnectionId")
const probeLiveness = scopedMock("probeLiveness")
const forceReconnect = scopedMock("forceReconnect")

function createHandlerMocks(): HandlerMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => ({})),
    uploadTaskLog: vi.fn(async () => ({ status: "changed", accepted: 0, truncated: false })),
    fetchConfig: vi.fn(async () => null),
    workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
    startSignalR: vi.fn(async () => undefined),
    stopSignalR: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => "conn-1"),
    probeLiveness: vi.fn(async () => true),
    forceReconnect: vi.fn(async () => undefined),
  }
}

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
      currentHandlerTestState().capturedSignalROptions = {
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

function installStubRuntimeFactory(resources: OpenCodeRuntimeTestResources, _options: StubRuntimeOptions = {}): OpenCodeRuntime {
  const ready = _options.ready ?? true
  const followupImpl = _options.followup ?? (() => ({
    ok: true as const,
    value: { facts: { runtimeSessionId: "ses_runtime", workDir: "/work" }, diagnostics: [] },
    diagnostics: [],
  }))
  const cancelImpl = _options.cancel ?? (() => ({
    ok: true as const,
    value: { facts: { runtimeSessionId: "ses_runtime", workDir: "/work", cancelled: true, stopConfirmed: true }, diagnostics: [] },
    diagnostics: [],
  }))
  const stub: OpenCodeRuntime = {
    ready: () => ready,
    diagnostic: () => null,
    setWorkOwners() {},
    canPollWhileCold: () => !ready,
    async start() {
      return { ok: true, value: { ready: true, diagnostic: null, ownership: { ownerIds: [], idleSince: null, activeOperations: 0, generation: null } }, diagnostics: [] }
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
  resources.openCodeRuntimeFactory = () => stub
  return stub
}

function hostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    serverUrl: "https://runner.test",
    runnerId: "runner-test",
    projectId: "project-1",
    runnerRoot: "/virtual/mohist-runner-host-opencode-handlers",
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
  function it(name: string, body: (resources: HandlerTestResources, state: HandlerTestState) => Promise<void>): void {
    vitestIt(name, async () => {
      const resources: HandlerTestResources = {
        fileSystem: new MemoryFileSystem(),
        logger: createLoggerCapture(),
        externalProcessPolicy: {
          assertAllowed(label) { throw new Error(`external process forbidden in handler test: ${label}`) },
          register() {},
        },
        piRuntimeFactory: () => ({
          start: async () => ({ ok: true, value: { ready: true, diagnostic: null, catalog: { models: [] } }, diagnostics: [] }),
          ready: () => true,
          diagnostic: () => null,
          catalog: () => ({ models: [] }),
          shutdown: async () => {},
        } as never),
      }
      const state: HandlerTestState = { mocks: createHandlerMocks(), capturedSignalROptions: null }
      await handlerTestStorage.run(state, async () => {
        vi.useFakeTimers()
        try {
          await withTestRunnerResources(async () => await body(resources, state), resources)
        } finally {
          vi.useRealTimers()
        }
      })
    })
  }

  it("constructs RunnerSignalRClient with the OpenCodeRuntime handle via an accessor", async (resources, state) => {
    const stub = installStubRuntimeFactory(resources)
    await startHost()

    expect(state.capturedSignalROptions).not.toBeNull()
    const accessor = state.capturedSignalROptions?.openCodeRuntime as
      | OpenCodeRuntime
      | (() => OpenCodeRuntime | null)
      | null
    expect(typeof accessor).toBe("function")
    const resolvedRuntime = (accessor as () => OpenCodeRuntime | null)()
    expect(resolvedRuntime).toBe(stub)
  })

  it("the OpenCodeRuntime accessor returns the live runtime handle (not a snapshot)", async (resources, state) => {
    const stub = installStubRuntimeFactory(resources)
    await startHost()

    const accessor = state.capturedSignalROptions?.openCodeRuntime as () => OpenCodeRuntime | null
    expect(accessor()).toBe(stub)
  })

  it("passes the followup target resolver and runtime-event outbox through to RunnerSignalRClient", async (resources, state) => {
    installStubRuntimeFactory(resources)
    await startHost()

    expect(state.capturedSignalROptions).not.toBeNull()
    expect(typeof state.capturedSignalROptions?.followupTargetResolver).toBe("function")
    expect(state.capturedSignalROptions?.agentSessionRuntimeEventOutbox).not.toBeNull()
  })
})
