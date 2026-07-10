import { afterEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { isUnderRunnerRoot, resolveWorkspaceQuery, resolveSessionTarget, RunnerSignalRClient, type CancelAgentSessionPayload, type ReceiveFollowupPayload, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"
import type { CommandResult } from "../src/system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"

interface CapturedBuilder {
  url?: string
  reconnectPolicy?: number[]
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

const builders: CapturedBuilder[] = []

afterEach(async () => {
  setRunnerSignalRGitRunnerForTest(null)
  setRunnerSignalRExistsCheckerForTest(null)
})

function findHandler(name: string): (...args: unknown[]) => unknown {
  const conn = builders.at(-1)!.connection
  const call = conn.on.mock.calls.find(([event]) => event === name)
  const handler = call?.[1]
  if (typeof handler !== "function") throw new Error(`handler not registered: ${name}`)
  return (...args) => handler(...args)
}

function runOk(stdout = ""): CommandResult {
  return { exitCode: 0, stdout, stderr: "" }
}

function runFail(stderr = ""): CommandResult {
  return { exitCode: 1, stdout: "", stderr }
}

interface FakeConnection {
  state: signalR.HubConnectionState
  connectionId: string | null
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
  invoke: ReturnType<typeof vi.fn>
  on: ReturnType<typeof vi.fn>
  onreconnected: ((cb: (id?: string) => void) => void) | undefined
  _reconnectHandler?: (connectionId?: string) => void
}

function makeFakeConnection(): FakeConnection {
  const conn: FakeConnection = {
    state: signalR.HubConnectionState.Disconnected,
    connectionId: null,
    start: vi.fn(),
    stop: vi.fn(),
    invoke: vi.fn(),
    on: vi.fn(),
    onreconnected: undefined,
  }
  conn.start.mockImplementation(async () => {
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = `conn-${Math.random().toString(36).slice(2, 10)}`
  })
  conn.stop.mockImplementation(async () => {
    conn.state = signalR.HubConnectionState.Disconnected
    conn.connectionId = null
  })
  conn.onreconnected = ((cb: (id?: string) => void) => {
    conn._reconnectHandler = cb
  }) as FakeConnection["onreconnected"]
  return conn
}

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      private _url?: string
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      private _connection: FakeConnection = makeFakeConnection()
      withUrl(url: string) {
        this._url = url
        builders.push({ url, handlers: this._handlers, connection: this._connection })
        return this
      }
      withAutomaticReconnect(reconnectPolicy: number[]) {
        const builder = builders.at(-1)
        if (builder) builder.reconnectPolicy = reconnectPolicy
        return this
      }
      build() {
        this._connection.on.mockImplementation((evt: string, h: (...args: unknown[]) => unknown) => {
          this._handlers.set(evt, h)
          return this._connection
        })
        return this._connection as unknown as signalR.HubConnection
      }
    },
    HubConnectionState: {
      Disconnected: "Disconnected",
      Connecting: "Connecting",
      Connected: "Connected",
      Disconnecting: "Disconnecting",
      Reconnecting: "Reconnecting",
    },
  }
})

function lastBuilder(): CapturedBuilder {
  const builder = builders.at(-1)
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
}

describe("RunnerSignalRClient workspace queries", () => {
  it("WorkspaceQuery_UsesExplicitWorkspaceAndBaseBranch", () => {
    const query = resolveWorkspaceQuery({
      workspacePath: "/tmp/mohist/workspaces/issue-25",
      branch: "mohist/run-wr-25",
      baseBranch: "master",
    })

    expect(query).toEqual({
      workDir: "/tmp/mohist/workspaces/issue-25",
      baseBranch: "master",
      head: "mohist/run-wr-25",
    })
  })

  it("WorkspaceQuery_RejectsMissingBaseBranchInsteadOfGuessingMain", () => {
    const query = resolveWorkspaceQuery({
      workspacePath: "/tmp/mohist/workspaces/issue-25",
      branch: "mohist/run-wr-25",
    })

    expect(query).toBeNull()
  })

  it("WorkspaceQuery_RejectsMissingHeadInsteadOfFallingBackToMoIssue", () => {
    // The legacy `mo/issue-{N}` worktree branch is no longer materialized by
    // the runner; the dispatch must supply the per-run head ref.
    const query = resolveWorkspaceQuery({
      issueNumber: 25,
      workspacePath: "/tmp/mohist/workspaces/issue-25",
      baseBranch: "master",
    })

    expect(query).toBeNull()
  })

  it("WorkspaceRemoval_OnlyAllowsPathsUnderRunnerRoot", () => {
    expect(isUnderRunnerRoot("/tmp/mohist/projects", "/tmp/mohist/projects/app/workspaces/issue-1")).toBe(true)
    expect(isUnderRunnerRoot("/tmp/mohist/projects", "/tmp/mohist/projects")).toBe(true)
    expect(isUnderRunnerRoot("/tmp/mohist/projects", "/tmp/mohist/other/issue-1")).toBe(false)
  })

  it("GetWorkspaceStatus_WhenFetchFails_ReturnsExistingWorkspaceWithRebaseState", async () => {
    builders.length = 0
    const calls: Array<{ command: string; timeoutMs: number | undefined }> = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args, _cwd, _signal, _env, options) => {
      calls.push({ command: args.join(" "), timeoutMs: options?.timeoutMs })
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runOk("headSha\n")
        case "rebase --show-current-patch":
          return runOk("patch\n")
        case "diff --name-only --diff-filter=U":
          return runOk("packages/runner/src/server/runner-signalr.ts\n")
        case "fetch origin master":
          return runFail("fatal: unable to access origin")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/runner-root", null)
    const handler = findHandler("GetWorkspaceStatus")
    const status = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(status).toEqual({
      exists: true,
      branch: "mohist/run-wr-1",
      baseBranch: "master",
      rebaseInProgress: true,
      conflictingFiles: ["packages/runner/src/server/runner-signalr.ts"],
      reason: "fetch_failed",
    })
    expect(calls.map((call) => call.command)).toEqual([
      "rev-parse --is-inside-work-tree",
      "rev-parse --verify refs/heads/mohist/run-wr-1",
      "rebase --show-current-patch",
      "diff --name-only --diff-filter=U",
      "fetch origin master",
    ])
    expect(calls.find((call) => call.command === "fetch origin master")?.timeoutMs).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    expect(calls.filter((call) => call.command !== "fetch origin master").every((call) => call.timeoutMs === undefined)).toBe(true)
    expect(calls.map((call) => call.command)).not.toContain("rev-list --left-right --count origin/master...mohist/run-wr-1")
  })

  it("GetWorkspaceStatus_WhenFetchSucceeds_ReportsAheadBehindFromOriginBase", async () => {
    builders.length = 0
    const calls: Array<{ command: string; timeoutMs: number | undefined }> = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args, _cwd, _signal, _env, options) => {
      calls.push({ command: args.join(" "), timeoutMs: options?.timeoutMs })
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runOk("headSha\n")
        case "rebase --show-current-patch":
          return runFail("fatal: no rebase in progress")
        case "fetch origin master":
          return runOk("")
        case "rev-list --left-right --count origin/master...mohist/run-wr-1":
          return runOk("3\t2\n")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/runner-root", null)
    const handler = findHandler("GetWorkspaceStatus")
    const status = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(status).toMatchObject({
      exists: true,
      branch: "mohist/run-wr-1",
      baseBranch: "master",
      ahead: 2,
      behind: 3,
      rebaseInProgress: false,
      conflictingFiles: [],
    })
    expect(calls.map((call) => call.command)).toEqual([
      "rev-parse --is-inside-work-tree",
      "rev-parse --verify refs/heads/mohist/run-wr-1",
      "rebase --show-current-patch",
      "fetch origin master",
      "rev-list --left-right --count origin/master...mohist/run-wr-1",
    ])
    expect(calls.find((call) => call.command === "fetch origin master")?.timeoutMs).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    expect(calls.filter((call) => call.command !== "fetch origin master").every((call) => call.timeoutMs === undefined)).toBe(true)
  })
})

describe("RunnerSignalRClient handshake", () => {
  it("IncludesBuildGitHashInQueryStringWhenProvided", () => {
    builders.length = 0
    const hash = "abcdef1234567890abcdef1234567890abcdef12"
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", hash)
    const last = builders.at(-1)
    expect(last?.url).toBe(`http://localhost:3456/hubs/runner?runnerId=runner-1&buildGitHash=${hash}`)
  })

  it("OmitsBuildGitHashWhenNull", () => {
    builders.length = 0
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const last = builders.at(-1)
    expect(last?.url).toBe("http://localhost:3456/hubs/runner?runnerId=runner-1")
  })

  it("OmitsBuildGitHashWhenNotProvided", () => {
    builders.length = 0
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects")
    const last = builders.at(-1)
    expect(last?.url).toBe("http://localhost:3456/hubs/runner?runnerId=runner-1")
  })

  it("ConfiguresFixedAutomaticReconnectIntervals", () => {
    builders.length = 0
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const last = builders.at(-1)
    expect(last?.reconnectPolicy).toEqual([0, 2000, 5000, 10000, 30000])
  })
})

describe("RunnerSignalRClient liveness + reconnect", () => {
  it("GetConnectionId_IsNullBeforeStart", () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    expect(client.getConnectionId()).toBeNull()
  })

  it("GetConnectionId_IsAssignedAfterStart", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    await client.start()
    const id = client.getConnectionId()
    expect(id).not.toBeNull()
    expect(id).toMatch(/^conn-/)
    await client.stop()
  })

  it("ProbeLiveness_ReturnsFalse_WhenNotConnected", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    // state is Disconnected (never started)
    const result = await client.probeLiveness(new AbortController().signal)
    expect(result).toBe(false)
  })

  it("ProbeLiveness_ReturnsTrue_OnSuccessfulPing", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-1"
    conn.invoke.mockResolvedValue("conn-1")
    const result = await client.probeLiveness(new AbortController().signal)
    expect(result).toBe(true)
    expect(conn.invoke).toHaveBeenCalledWith("Ping")
  })

  it("ProbeLiveness_ReturnsFalse_OnInvokeRejection", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-1"
    conn.invoke.mockRejectedValue(new Error("invoke failed"))
    const result = await client.probeLiveness(new AbortController().signal)
    expect(result).toBe(false)
  })

  it("ProbeLiveness_ReturnsFalse_OnTimeout", async () => {
    vi.useFakeTimers()
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-1"
    let resolveInvoke: (value: unknown) => void = () => undefined
    conn.invoke.mockReturnValue(new Promise((resolve) => {
      resolveInvoke = resolve
    }))
    // Re-construct with a tiny probe timeout
    const tight = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null, { probeTimeoutMs: 5 })
    const tightConn = builders.at(-1)!.connection
    tightConn.state = signalR.HubConnectionState.Connected
    tightConn.connectionId = "conn-1"
    let resolveTight: (value: unknown) => void = () => undefined
    tightConn.invoke.mockReturnValue(new Promise((resolve) => {
      resolveTight = resolve
    }))
    const probe = tight.probeLiveness(new AbortController().signal)
    await vi.advanceTimersByTimeAsync(5)
    const result = await probe
    expect(result).toBe(false)
    // Drain the pending invokes so vitest doesn't keep the promises alive.
    resolveInvoke("late")
    resolveTight("late")
    void client
    vi.useRealTimers()
  })

  it("ProbeLiveness_ReturnsFalse_OnAbortSignal", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null, { probeTimeoutMs: 5_000 })
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-1"
    let resolveInvoke: (value: unknown) => void = () => undefined
    conn.invoke.mockReturnValue(new Promise((resolve) => {
      resolveInvoke = resolve
    }))
    const ac = new AbortController()
    const probePromise = client.probeLiveness(ac.signal)
    ac.abort()
    const result = await probePromise
    expect(result).toBe(false)
    resolveInvoke("late")
  })

  it("ForceReconnect_StopsThenStarts_WhenConnected", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    await client.forceReconnect(new AbortController().signal)
    expect(conn.stop).toHaveBeenCalled()
    expect(conn.start).toHaveBeenCalled()
    expect(client.getConnectionId()).not.toBe("conn-old")
  })

  it("ForceReconnect_NotifiesCallbackAfterManualReconnect", async () => {
    builders.length = 0
    const seen: string[] = []
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null, {
      onReconnected: (id) => seen.push(id),
    })
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"

    await client.forceReconnect(new AbortController().signal)

    expect(conn.stop).toHaveBeenCalled()
    expect(conn.start).toHaveBeenCalled()
    expect(seen).toEqual([client.getConnectionId()])
    expect(seen[0]).toMatch(/^conn-/)
  })

  it("ForceReconnect_StartsDirectly_WhenDisconnected", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const conn = builders.at(-1)!.connection
    // initial state is Disconnected
    await client.forceReconnect(new AbortController().signal)
    expect(conn.stop).not.toHaveBeenCalled()
    expect(conn.start).toHaveBeenCalled()
  })

  it("ForceReconnect_SwallowsStopError_AndStillStarts", async () => {
    builders.length = 0
    const client = new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    conn.stop.mockRejectedValueOnce(new Error("stop failed"))
    await expect(client.forceReconnect(new AbortController().signal)).resolves.toBeUndefined()
    expect(conn.start).toHaveBeenCalled()
  })

  it("OnReconnected_FiresHostCallback_WithNewConnectionId", async () => {
    builders.length = 0
    const seen: string[] = []
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null, {
      onReconnected: (id) => seen.push(id),
    })
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    expect(conn.onreconnected).toBeDefined()
    // Simulate SignalR auto-reconnect completing
    conn.connectionId = "conn-new"
    conn._reconnectHandler?.("conn-new")
    expect(seen).toEqual(["conn-new"])
  })

  it("OnReconnected_UsesConnectionConnectionId_WhenCallbackArgMissing", async () => {
    builders.length = 0
    const seen: string[] = []
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null, {
      onReconnected: (id) => seen.push(id),
    })
    const conn = builders.at(-1)!.connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    conn.connectionId = "conn-new"
    conn._reconnectHandler?.()
    expect(seen).toEqual(["conn-new"])
  })
})


type AnyFn = (...args: any[]) => any

interface MockServerConnection {
  workflowAgentSessionRuntimeEvents: AnyFn
  agentSessionRuntimeEvents?: AnyFn
}

interface MockConnection {
  prompt: AnyFn
  cancel?: AnyFn
}

function buildClient(opts: {
  resolver?: AnyFn | null
  serverConnection?: MockServerConnection | null
}) {
  builders.length = 0
  const defaultServerConnection: MockServerConnection = {
    workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
  }
  const serverConnection = opts.serverConnection === undefined ? defaultServerConnection : opts.serverConnection
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const client = new RunnerSignalRClient(
    "http://localhost:3456",
    "runner-1",
    "/tmp/mohist/projects",
    null,
    {
      serverConnection: serverConnection as never,
      followupTargetResolver: resolver as never,
    },
  )
  return client
}

function emitFollowup(builder: CapturedBuilder, payload: ReceiveFollowupPayload | null | undefined) {
  const handler = builder.handlers.get("ReceiveFollowup")
  if (!handler) throw new Error("ReceiveFollowup handler was not registered")
  handler(payload)
}

async function flush() {
  await new Promise((resolve) => setImmediate(resolve))
}

describe("RunnerSignalRClient ReceiveFollowup handler", () => {
  it("Followup_FireAndForgetPromptCallsConnectionPromptWithoutAwait", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "add a logout button" })
    await flush()

    expect(prompt).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledWith({
      sessionId: "acp-1",
      prompt: [{ type: "text", text: "add a logout button" }],
    })
  })

  it("Followup_ReturnsImmediatelyWithoutAwaitingPromptResolution", async () => {
    let resolvePrompt!: (value: unknown) => void
    const prompt = vi.fn(() => new Promise((resolve) => { resolvePrompt = resolve }))
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ship it" })
    await flush()
    expect(prompt).toHaveBeenCalledTimes(1)
    resolvePrompt(undefined)
    await flush()
  })

  it("Followup_PromptsEvenWhenRuntimeEventsEmitIsStillPending", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(() => new Promise(() => undefined))
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ship while event is pending" })
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledWith({
      sessionId: "acp-1",
      prompt: [{ type: "text", text: "ship while event is pending" }],
    })
  })

  it("Followup_EmitsSessionInputEventBeforeCallingPrompt", async () => {
    const callOrder: string[] = []
    const prompt = vi.fn(async () => {
      callOrder.push("prompt")
    })
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => {
      callOrder.push("session.input")
    })
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "fix the typo" })
    await flush()
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(callOrder).toEqual(["session.input", "prompt"])
  })

  it("Followup_TagsEventWithPromptKindFollowup", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "tag me" })
    await flush()

    expect(runtimeEvents).toHaveBeenCalledWith(
      "proj-1",
      "wr-1",
      "work-1",
      expect.objectContaining({
        runtimeEvents: [
          expect.objectContaining({
            type: "session.input",
            payload: expect.objectContaining({
              kind: "followup",
              text: "tag me",
              role: "user",
              acpSessionId: "acp-1",
              source: "followup",
            }),
          }),
        ],
      }),
      expect.any(AbortSignal),
    )
  })

  it("Followup_DropsWhenResolverReturnsNullAndDoesNotThrow", async () => {
    const prompt = vi.fn(async () => undefined)
    const runtimeEvents = vi.fn(async () => undefined)
    const resolver = vi.fn(() => null)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsWhenResolverThrows", async () => {
    const prompt = vi.fn(async () => undefined)
    const runtimeEvents = vi.fn(async () => undefined)
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "ignored" })).not.toThrow()
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
    expect(errorSpy).toHaveBeenCalled()
    errorSpy.mockRestore()
  })

  it("Followup_CatchesPromptRejectionAndLogsWithoutThrowing", async () => {
    const prompt = vi.fn(async () => { throw new Error("opencode crashed") })
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "boom" })).not.toThrow()
    await flush()
    await flush()

    expect(prompt).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith("followup connection.prompt rejected:", expect.stringContaining("opencode crashed"))
    errorSpy.mockRestore()
  })

  it("Followup_ContinuesToPromptEvenIfRuntimeEventsEmitFails", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => { throw new Error("server unreachable") })
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "keep going" })
    await flush()
    await flush()

    expect(runtimeEvents).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith("failed to emit followup session.input event:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("Followup_DropsPayloadWhenTextIsMissing", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsPayloadWhenResolverIsNull", async () => {
    const prompt = vi.fn(async () => undefined)
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver: null, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })

  it("Followup_DropsPayloadWhenServerConnectionIsNull", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    emitFollowup(builder, { workflowRunId: "wr-1", sessionName: "work-1", text: "noop" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
  })

  it("Followup_DropsNullOrUndefinedPayload", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))
    const runtimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = { workflowAgentSessionRuntimeEvents: runtimeEvents }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, null)
    emitFollowup(builder, undefined)
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(runtimeEvents).not.toHaveBeenCalled()
  })
})

// Issue-129 T-004: generic (non-workflow) AgentSession followup path.
// The handler must branch on `target.kind` and dispatch the runtime
// event + prompt() through the *generic* server connection methods,
// look the resolver up under the `generic:` AcpSessionManager key,
// and silently drop unknown sessions.
describe("RunnerSignalRClient ReceiveFollowup handler (generic session target, T-004)", () => {
  function genericPayload(text: string): ReceiveFollowupPayload {
    return {
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-session-1" },
      text,
    }
  }

  it("GenericFollowup_LocatesSessionByGenericKey_AndCallsConnectionPrompt", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("generic")
      expect((target as { sessionId: string }).sessionId).toBe("gen-session-1")
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("add a logout route"))
    await flush()

    expect(prompt).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledWith({
      sessionId: "acp-1",
      prompt: [{ type: "text", text: "add a logout route" }],
    })
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(1)
  })

  it("GenericFollowup_EmitsSessionInputViaAgentSessionRuntimeEventsEndpoint", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("kind tag"))
    await flush()

    expect(agentSessionRuntimeEvents).toHaveBeenCalledWith(
      "proj-1",
      "gen-session-1",
      expect.objectContaining({
        runtimeEvents: [
          expect.objectContaining({
            type: "session.input",
            payload: expect.objectContaining({
              kind: "followup",
              text: "kind tag",
              role: "user",
              acpSessionId: "acp-1",
              source: "followup",
            }),
          }),
        ],
      }),
      expect.any(AbortSignal),
    )
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_ContinuesToPromptEvenIfAgentSessionRuntimeEventsEmitFails", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => { throw new Error("server unreachable") })
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, genericPayload("keep going"))
    await flush()
    await flush()

    expect(agentSessionRuntimeEvents).toHaveBeenCalledTimes(1)
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith("failed to emit followup session.input event:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("GenericFollowup_DropsUnknownSessionWithoutThrowing", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => null)
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    expect(() => emitFollowup(builder, genericPayload("ignored"))).not.toThrow()
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_DropsWhenTargetSessionIdMissing", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: { kind: "generic", projectId: "proj-1" },
      text: "no sessionId",
    })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("GenericFollowup_DropsWhenTextMissing", async () => {
    const prompt = vi.fn(async () => undefined)
    const resolver = vi.fn(() => ({ connection: { prompt } as never, sessionId: "acp-1", projectId: "proj-1" }))
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, { ...genericPayload(""), text: "" })
    await flush()

    expect(prompt).not.toHaveBeenCalled()
    expect(workflowRuntimeEvents).not.toHaveBeenCalled()
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
  })

  it("WorkflowFollowup_StillUsesWorkflowRuntimeEventsEndpoint_WhenTargetShapeCarriesIt", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("workflow")
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    emitFollowup(builder, {
      target: {
        kind: "workflow",
        projectId: "proj-1",
        workflowRunId: "wr-1",
        sessionName: "work-1",
      },
      text: "tag me",
    })
    await flush()

    expect(workflowRuntimeEvents).toHaveBeenCalledTimes(1)
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
    expect(prompt).toHaveBeenCalledTimes(1)
  })

  it("WorkflowFollowup_LegacyTopLevelFields_StillResolveToWorkflowTarget", async () => {
    const prompt = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("workflow")
      if (target.kind === "workflow") {
        expect(target.workflowRunId).toBe("wr-legacy")
        expect(target.sessionName).toBe("work-legacy")
      }
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-legacy" }
    })
    const workflowRuntimeEvents = vi.fn(async () => undefined)
    const agentSessionRuntimeEvents = vi.fn(async () => undefined)
    const serverConnection: MockServerConnection = {
      workflowAgentSessionRuntimeEvents: workflowRuntimeEvents,
      agentSessionRuntimeEvents,
    }

    buildClient({ resolver, serverConnection })
    const builder = lastBuilder()

    // Older server builds only populate top-level workflowRunId/sessionName
    // and emit no `target` field. The handler must still resolve them
    // (the workflowRunId/sessionName fallback inside `resolveSessionTarget`).
    emitFollowup(builder, { workflowRunId: "wr-legacy", sessionName: "work-legacy", text: "legacy ok" })
    await flush()

    expect(workflowRuntimeEvents).toHaveBeenCalledTimes(1)
    expect(workflowRuntimeEvents).toHaveBeenCalledWith(
      "proj-legacy",
      "wr-legacy",
      "work-legacy",
      expect.objectContaining({
        runtimeEvents: [
          expect.objectContaining({
            type: "session.input",
            payload: expect.objectContaining({ kind: "followup", text: "legacy ok" }),
          }),
        ],
      }),
      expect.any(AbortSignal),
    )
    expect(agentSessionRuntimeEvents).not.toHaveBeenCalled()
    expect(prompt).toHaveBeenCalledTimes(1)
  })
})

describe("resolveSessionTarget (T-004)", () => {
  it("PrefersTargetField_WhenPresent", () => {
    const payload: ReceiveFollowupPayload = {
      workflowRunId: "wr-ignored",
      sessionName: "name-ignored",
      target: { kind: "generic", projectId: "proj-1", sessionId: "gen-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toEqual({
      kind: "generic",
      projectId: "proj-1",
      sessionId: "gen-1",
    })
  })

  it("ReturnsNull_WhenGenericTargetMissingSessionId", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "generic", projectId: "proj-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("ReturnsNull_WhenWorkflowTargetMissingSessionName", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("FallsBackToLegacyWorkflowTopLevelFields_WhenNoTarget", () => {
    const payload: ReceiveFollowupPayload = {
      workflowRunId: "wr-1",
      sessionName: "work-1",
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toEqual({
      kind: "workflow",
      projectId: "",
      workflowRunId: "wr-1",
      sessionName: "work-1",
    })
  })

  it("ReturnsNull_WhenNoTargetAndNoLegacyFields", () => {
    const payload: ReceiveFollowupPayload = { text: "x" }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it("ReturnsNull_OnUnknownTargetKind", () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: "weird" as unknown as "workflow", projectId: "proj-1" },
      text: "x",
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })
})

// Issue-129 T-005: server→runner `CancelAgentSession` SignalR
// invocation. The handler is distinct from the fire-and-forget
// `ReceiveFollowup`: it MUST return a `{ state: ... }` reply that the
// HTTP endpoint mirrors verbatim, so the API can never pretend success
// (design D6). The handler is registered with `connection.on(...)`; the
// signalR client returns the handler's resolved value back to the server.
function emitCancel(builder: CapturedBuilder, payload: CancelAgentSessionPayload | null | undefined): Promise<unknown> {
  const handler = builder.handlers.get("CancelAgentSession")
  if (!handler) throw new Error("CancelAgentSession handler was not registered")
  return Promise.resolve(handler(payload))
}

describe("RunnerSignalRClient CancelAgentSession handler (T-005)", () => {
  function genericCancelPayload(sessionId: string): CancelAgentSessionPayload {
    return {
      target: { kind: "generic", projectId: "proj-1", sessionId },
    }
  }

  it("CancellableSession_ResolverHits_ConnectionCancelInvokedAndRepliesCancelled", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn((target: SessionTarget) => {
      expect(target.kind).toBe("generic")
      if (target.kind === "generic") {
        expect(target.sessionId).toBe("gen-session-1")
        expect(target.projectId).toBe("proj-1")
      }
      return { connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }
    })

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "cancelled" })
    expect(cancel).toHaveBeenCalledTimes(1)
    expect(cancel).toHaveBeenCalledWith({ sessionId: "acp-1" })
  })

  it("UnknownSession_ResolverReturnsNull_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => null)

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("unknown"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })

  it("NoResolverRegistered_RepliesNotCancellableAndDoesNotCallCancel", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }

    buildClient({ resolver: null, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })

  it("NoCancelMethodOnConnection_RepliesNotCancellable", async () => {
    // Defensive: the current ACP SDK defines `cancel` on every
    // ClientSideConnection, but the handler must report honestly if a
    // future / custom connection omits the method.
    const connection: MockConnection = { prompt: vi.fn() /* no cancel */ }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
  })

  it("ConnectionCancelRejects_RepliesNotCancellableAndLogs", async () => {
    const cancel = vi.fn(async () => {
      throw new Error("transport dropped")
    })
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenCalledWith(
      "cancel connection.cancel rejected:",
      expect.stringContaining("transport dropped"),
    )
    errorSpy.mockRestore()
  })

  it("ResolverThrows_RepliesNotCancellableAndLogs", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => { throw new Error("resolver boom") })
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, genericCancelPayload("gen-session-1"))) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
    expect(errorSpy).toHaveBeenCalledWith("cancel target resolver threw:", expect.any(Error))
    errorSpy.mockRestore()
  })

  it("NullOrMissingPayload_RepliesNotCancellable", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const replyFromNull = (await emitCancel(builder, null)) as { state: string }
    const replyFromMissing = (await emitCancel(builder, undefined)) as { state: string }
    const replyFromNoTarget = (await emitCancel(builder, { target: undefined as unknown as never })) as { state: string }

    expect(replyFromNull).toEqual({ state: "not-cancellable" })
    expect(replyFromMissing).toEqual({ state: "not-cancellable" })
    expect(replyFromNoTarget).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })

  it("WorkflowShapedTarget_RepliesNotCancellable", async () => {
    // The product cancel endpoint only addresses generic sessions today;
    // a `workflow` target through this method is treated as
    // not-cancellable (the issue-scoped session lifecycle has no cancel
    // surface) rather than being misrouted to the followup code path.
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wr-1", sessionName: "work-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })

  it("GenericTargetWithoutSessionId_RepliesNotCancellable", async () => {
    const cancel = vi.fn(async () => undefined)
    const connection: MockConnection = { prompt: vi.fn(), cancel }
    const resolver = vi.fn(() => ({ connection: connection as never, sessionId: "acp-1", projectId: "proj-1" }))

    buildClient({ resolver, serverConnection: null })
    const builder = lastBuilder()

    const reply = (await emitCancel(builder, {
      target: { kind: "generic", projectId: "proj-1" },
    })) as { state: string }

    expect(reply).toEqual({ state: "not-cancellable" })
    expect(cancel).not.toHaveBeenCalled()
  })
})

// Issue-313 T-006: test-first coverage for the four git query handlers
// that previously had zero direct tests (GetDiff / GetCommits /
// GetCommitDiff / GetFileContent). Each handler is captured from the
// mocked SignalR connection via `findHandler`, with the git runner and
// filesystem-existence checker injected via the existing test seams
// (`setRunnerSignalRGitRunnerForTest` / `setRunnerSignalRExistsCheckerForTest`).
// These tests pin the current behaviour so T-007 can extract the handlers
// to `workspace-git-handlers.ts` without contract drift.

function buildGitOnlyClient() {
  builders.length = 0
  new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null)
  return lastBuilder()
}

describe("RunnerSignalRClient GetDiff handler (T-006)", () => {
  it("UnresolvableWorkspace_ReturnsNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    // Missing baseBranch → resolveWorkspaceQuery returns null → no git call.
    const result = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("MissingBranch_IsResolvesToNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("MissingWorkspacePath_IsResolvesToNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = await handler({
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("GitWorkTreeProbeFails_ReturnsNullAndDoesNotIssueDiffOrMergeBase", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => false)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("PathExistsButNotWorktree_ReturnsNullAndDoesNotIssueDiffOrMergeBase", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runFail("fatal: not a git repository")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual(["rev-parse --is-inside-work-tree"])
  })

  it("HeadRefMissing_ReturnsNullAndDoesNotIssueDiffOrMergeBase", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runFail("error: malformed object name")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([
      "rev-parse --is-inside-work-tree",
      "rev-parse --verify refs/heads/mohist/run-wr-1",
    ])
  })

  it("MergeBaseFails_MergeBaseFallsBackToBaseBranch", async () => {
    const numstat = "3\t1\tpackages/foo.ts\n"
    const fullDiff = [
      "diff --git a/packages/foo.ts b/packages/foo.ts",
      "index 0000..1111 100644",
      "--- a/packages/foo.ts",
      "+++ b/packages/foo.ts",
      "@@ -1,1 +1,3 @@",
      "-old",
      "+new",
      "+again",
    ].join("\n")
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runOk("headSha\n")
        case "diff master...mohist/run-wr-1 --numstat":
          return runOk(numstat)
        case "diff master...mohist/run-wr-1":
          return runOk(fullDiff)
        case "merge-base master mohist/run-wr-1":
          return runFail("fatal: no merge base")
        case "rev-list --left-right --count master...mohist/run-wr-1":
          return runOk("3\t2\n")
        case "log master...mohist/run-wr-1 --format=%H":
          return runOk("abc\ndef\n")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = (await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })) as Record<string, unknown>

    expect(result).toMatchObject({
      base: "master",
      head: "mohist/run-wr-1",
      mergeBase: "master",
      ahead: 2,
      behind: 3,
      commitCount: 2,
      totalAdditions: 3,
      totalDeletions: 1,
    })
    expect(calls).toContain("merge-base master mohist/run-wr-1")
  })

  it("CommitCountLogFails_CommitCountFallsBackToZero", async () => {
    const numstat = "5\t2\tpackages/bar.ts\n"
    const fullDiff = [
      "diff --git a/packages/bar.ts b/packages/bar.ts",
      "index 0000..1111 100644",
      "--- a/packages/bar.ts",
      "+++ b/packages/bar.ts",
    ].join("\n")
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runOk("headSha\n")
        case "diff master...mohist/run-wr-1 --numstat":
          return runOk(numstat)
        case "diff master...mohist/run-wr-1":
          return runOk(fullDiff)
        case "merge-base master mohist/run-wr-1":
          return runOk("abc123\n")
        case "rev-list --left-right --count master...mohist/run-wr-1":
          return runOk("1\t4\n")
        case "log master...mohist/run-wr-1 --format=%H":
          return runFail("fatal: bad revision")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = (await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })) as Record<string, unknown>

    expect(result).toMatchObject({
      base: "master",
      head: "mohist/run-wr-1",
      mergeBase: "abc123",
      ahead: 4,
      behind: 1,
      commitCount: 0,
      totalAdditions: 5,
      totalDeletions: 2,
    })
  })

  it("PerFileDiff_IsKeyedByTheBPath", async () => {
    const numstat = "2\t0\tsrc/foo.txt\n1\t1\tsrc/bar.txt\n"
    const fullDiff = [
      "diff --git a/src/foo.txt b/src/foo.txt",
      "index 0000..1111 100644",
      "--- a/src/foo.txt",
      "+++ b/src/foo.txt",
      "@@ -1,1 +1,3 @@",
      "-old",
      "+new",
      "+again",
      "diff --git a/src/bar.txt b/src/bar.txt",
      "index 0000..2222 100644",
      "--- a/src/bar.txt",
      "+++ b/src/bar.txt",
      "@@ -1,1 +1,1 @@",
      "-x",
      "+y",
    ].join("\n")
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runOk("headSha\n")
        case "diff master...mohist/run-wr-1 --numstat":
          return runOk(numstat)
        case "diff master...mohist/run-wr-1":
          return runOk(fullDiff)
        case "merge-base master mohist/run-wr-1":
          return runOk("mergeBaseSha\n")
        case "rev-list --left-right --count master...mohist/run-wr-1":
          return runOk("0\t2\n")
        case "log master...mohist/run-wr-1 --format=%H":
          return runOk("a\nb\n")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = (await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })) as {
      files: Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }>
      totalAdditions: number
      totalDeletions: number
      base: string
      head: string
      mergeBase: string
      ahead: number
      behind: number
      commitCount: number
    }

    expect(result.base).toBe("master")
    expect(result.head).toBe("mohist/run-wr-1")
    expect(result.mergeBase).toBe("mergeBaseSha")
    expect(result.ahead).toBe(2)
    expect(result.behind).toBe(0)
    expect(result.commitCount).toBe(2)
    expect(result.totalAdditions).toBe(3)
    expect(result.totalDeletions).toBe(1)
    expect(result.files).toHaveLength(2)
    expect(result.files[0]).toMatchObject({ file: "src/foo.txt", additions: 2, deletions: 0, isBinary: false })
    expect(result.files[0].diff).toContain("diff --git a/src/foo.txt b/src/foo.txt")
    expect(result.files[0].diff).toContain("-old")
    expect(result.files[0].diff).toContain("+new")
    expect(result.files[0].diff).not.toContain("src/bar.txt")
    expect(result.files[1]).toMatchObject({ file: "src/bar.txt", additions: 1, deletions: 1, isBinary: false })
    expect(result.files[1].diff).toContain("diff --git a/src/bar.txt b/src/bar.txt")
    expect(result.files[1].diff).toContain("-x")
    expect(result.files[1].diff).toContain("+y")
    expect(result.files[1].diff).not.toContain("src/foo.txt")
  })

  it("BinaryFile_YieldsZeroAdditionsAndDeletionsAndIsBinaryTrue", async () => {
    const numstat = "-\t-\tbin/logo.png\n1\t0\tsrc/foo.ts\n"
    const fullDiff = [
      "diff --git a/bin/logo.png b/bin/logo.png",
      "index 0000..1111 100644",
      "Binary files a/bin/logo.png and b/bin/logo.png differ",
      "diff --git a/src/foo.ts b/src/foo.ts",
      "index 0000..2222 100644",
      "--- a/src/foo.ts",
      "+++ b/src/foo.ts",
      "@@ -1,1 +1,2 @@",
      "-old",
      "+new",
    ].join("\n")
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runOk("headSha\n")
        case "diff master...mohist/run-wr-1 --numstat":
          return runOk(numstat)
        case "diff master...mohist/run-wr-1":
          return runOk(fullDiff)
        case "merge-base master mohist/run-wr-1":
          return runOk("mergeBaseSha\n")
        case "rev-list --left-right --count master...mohist/run-wr-1":
          return runOk("0\t1\n")
        case "log master...mohist/run-wr-1 --format=%H":
          return runOk("a\n")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = (await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })) as {
      files: Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }>
      totalAdditions: number
      totalDeletions: number
    }

    expect(result.files).toHaveLength(2)
    expect(result.files[0]).toEqual({
      file: "bin/logo.png",
      additions: 0,
      deletions: 0,
      diff: expect.stringContaining("Binary files a/bin/logo.png and b/bin/logo.png differ"),
      isBinary: true,
    })
    expect(result.files[1]).toMatchObject({ file: "src/foo.ts", additions: 1, deletions: 0, isBinary: false })
    expect(result.totalAdditions).toBe(1)
    expect(result.totalDeletions).toBe(0)
  })
})

describe("RunnerSignalRClient GetCommits handler (T-006)", () => {
  it("UnresolvableWorkspace_ReturnsNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommits")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("GitWorkTreeProbeFails_ReturnsNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => false)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommits")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("PathExistsButNotWorktree_ReturnsNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runFail("fatal: not a git repository")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommits")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual(["rev-parse --is-inside-work-tree"])
  })

  it("ParsesCommitsFromTabSeparatedLog_WithFileTotalsAndMergeBaseFallback", async () => {
    const log = [
      "abc123\tabc\tsubject 1\tAlice\t2026-07-01T10:00:00+00:00",
      "def456\tdef\tsubject 2\tBob\t2026-07-02T11:00:00+00:00",
    ].join("\n")
    const numstat = "3\t1\tpackages/foo.ts\n5\t0\tpackages/bar.ts\n"
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "log master...mohist/run-wr-1 --format=%H\t%h\t%s\t%an\t%ad --date=iso":
          return runOk(log)
        case "diff master...mohist/run-wr-1 --numstat":
          return runOk(numstat)
        case "merge-base master mohist/run-wr-1":
          return runFail("fatal: no merge base")
        case "rev-list --left-right --count master...mohist/run-wr-1":
          return runOk("3\t2\n")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommits")

    const result = (await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })) as {
      base: string
      head: string
      mergeBase: string
      ahead: number
      behind: number
      filesChanged: number
      totalAdditions: number
      totalDeletions: number
      commits: Array<{ hash: string; shortHash: string; message: string; author: string; date: string; files: string[] }>
    }

    expect(result).toMatchObject({
      base: "master",
      head: "mohist/run-wr-1",
      mergeBase: "master",
      ahead: 2,
      behind: 3,
      filesChanged: 2,
      totalAdditions: 8,
      totalDeletions: 1,
    })
    expect(result.commits).toHaveLength(2)
    expect(result.commits[0]).toEqual({
      hash: "abc123",
      shortHash: "abc",
      message: "subject 1",
      author: "Alice",
      date: "2026-07-01T10:00:00+00:00",
      files: [],
    })
    expect(result.commits[1]).toEqual({
      hash: "def456",
      shortHash: "def",
      message: "subject 2",
      author: "Bob",
      date: "2026-07-02T11:00:00+00:00",
      files: [],
    })
    expect(calls).toContain("merge-base master mohist/run-wr-1")
  })

  it("EmptyLog_ReturnsCommitsEmptyArrayButStillReportsTotals", async () => {
    const numstat = ""
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "log master...mohist/run-wr-1 --format=%H\t%h\t%s\t%an\t%ad --date=iso":
          return runOk("")
        case "diff master...mohist/run-wr-1 --numstat":
          return runOk(numstat)
        case "merge-base master mohist/run-wr-1":
          return runOk("mergeBaseSha\n")
        case "rev-list --left-right --count master...mohist/run-wr-1":
          return runOk("0\t0\n")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommits")

    const result = (await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })) as { commits: unknown[]; filesChanged: number }

    expect(result.commits).toEqual([])
    expect(result.filesChanged).toBe(0)
  })

  it("ShortLogLines_AreDropped", async () => {
    // Real-world: parseCommits skips lines with fewer than 5 tab fields.
    const log = [
      "abc123\tabc\tsubject 1\tAlice\t2026-07-01T10:00:00+00:00",
      "too-short",
      "def456\tdef\tsubject 2", // only 3 fields
      "ghi789", // only 1 field
    ].join("\n")
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "log master...mohist/run-wr-1 --format=%H\t%h\t%s\t%an\t%ad --date=iso":
          return runOk(log)
        case "diff master...mohist/run-wr-1 --numstat":
          return runOk("")
        case "merge-base master mohist/run-wr-1":
          return runOk("mergeBaseSha\n")
        case "rev-list --left-right --count master...mohist/run-wr-1":
          return runOk("0\t1\n")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommits")

    const result = (await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })) as { commits: Array<{ hash: string }> }

    expect(result.commits).toHaveLength(1)
    expect(result.commits[0].hash).toBe("abc123")
  })
})

describe("RunnerSignalRClient GetCommitDiff handler (T-006)", () => {
  it("UnresolvableWorkspace_ReturnsNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommitDiff")

    const result = await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
      },
      "abc123",
    )

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("GitWorkTreeProbeFails_ReturnsNullAndDoesNotIssueShow", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => false)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommitDiff")

    const result = await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "abc123",
    )

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("SuccessfulShow_ReturnsThePatch", async () => {
    const patch = "diff --git a/foo b/foo\nindex 0000..1111 100644\n--- a/foo\n+++ b/foo\n@@ -1 +1 @@\n-old\n+new\n"
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show --format= --patch abc123":
          return runOk(patch)
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommitDiff")

    const result = (await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "abc123",
    )) as { diff: string }

    expect(result).toEqual({ diff: patch })
    expect(calls).toEqual([
      "rev-parse --is-inside-work-tree",
      "show --format= --patch abc123",
    ])
  })

  it("NonZeroExit_ReturnsNull", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show --format= --patch deadbeef":
          return runFail("fatal: bad revision")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetCommitDiff")

    const result = await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "deadbeef",
    )

    expect(result).toBeNull()
    expect(calls).toContain("show --format= --patch deadbeef")
  })
})

describe("RunnerSignalRClient GetFileContent handler (T-006)", () => {
  it("UnresolvableWorkspace_ReturnsBaseAndHeadNullAndDoesNotInvokeGit", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetFileContent")

    const result = await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
      },
      "src/foo.ts",
    )

    expect(result).toEqual({ base: null, head: null })
    expect(calls).toEqual([])
  })

  it("GitWorkTreeProbeFails_ReturnsBaseAndHeadNull", async () => {
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => false)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetFileContent")

    const result = await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "src/foo.ts",
    )

    expect(result).toEqual({ base: null, head: null })
    expect(calls).toEqual([])
  })

  it("BothSidesPresent_ReturnsBaseAndHeadStdout", async () => {
    const baseStdout = "BASE_CONTENT\n"
    const headStdout = "HEAD_CONTENT\n"
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show master:src/foo.ts":
          return runOk(baseStdout)
        case "show mohist/run-wr-1:src/foo.ts":
          return runOk(headStdout)
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetFileContent")

    const result = (await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "src/foo.ts",
    )) as { base: string | null; head: string | null }

    expect(result).toEqual({ base: baseStdout, head: headStdout })
    expect(calls).toContain("show master:src/foo.ts")
    expect(calls).toContain("show mohist/run-wr-1:src/foo.ts")
  })

  it("BaseMissing_HeadPresent_ReturnsBaseNullAndHeadStdout", async () => {
    const headStdout = "HEAD_ONLY\n"
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show master:src/foo.ts":
          return runFail("fatal: path 'src/foo.ts' does not exist in 'master'")
        case "show mohist/run-wr-1:src/foo.ts":
          return runOk(headStdout)
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetFileContent")

    const result = (await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "src/foo.ts",
    )) as { base: string | null; head: string | null }

    expect(result).toEqual({ base: null, head: headStdout })
  })

  it("BasePresent_HeadMissing_ReturnsBaseStdoutAndHeadNull", async () => {
    const baseStdout = "BASE_ONLY\n"
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show master:src/foo.ts":
          return runOk(baseStdout)
        case "show mohist/run-wr-1:src/foo.ts":
          return runFail("fatal: path 'src/foo.ts' does not exist in 'mohist/run-wr-1'")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetFileContent")

    const result = (await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "src/foo.ts",
    )) as { base: string | null; head: string | null }

    expect(result).toEqual({ base: baseStdout, head: null })
  })

  it("BothSidesMissing_ReturnsBaseAndHeadNull", async () => {
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args) => {
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show master:src/foo.ts":
          return runFail("fatal: missing on base")
        case "show mohist/run-wr-1:src/foo.ts":
          return runFail("fatal: missing on head")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetFileContent")

    const result = (await handler(
      {
        workspacePath: "/runner-root/workspace",
        branch: "mohist/run-wr-1",
        baseBranch: "master",
      },
      "src/foo.ts",
    )) as { base: string | null; head: string | null }

    expect(result).toEqual({ base: null, head: null })
  })
})
