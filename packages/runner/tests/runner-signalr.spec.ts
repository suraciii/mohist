import { afterEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { isUnderRunnerRoot, resolveWorkspaceQuery, resolveSessionTarget, RunnerSignalRClient, type ReceiveFollowupPayload, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { SessionTarget } from "../src/runtime/acp-connection.js"
import type { CommandResult } from "../src/system/process.js"

interface CapturedBuilder {
  url?: string
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

const builders: CapturedBuilder[] = []

afterEach(async () => {
  setRunnerSignalRGitRunnerForTest(null)
  setRunnerSignalRExistsCheckerForTest(null)
})

function findHandler(name: string): (arg: unknown) => Promise<unknown> {
  const conn = builders.at(-1)!.connection
  const call = conn.on.mock.calls.find(([event]) => event === name)
  if (!call) throw new Error(`handler not registered: ${name}`)
  return call[1] as (arg: unknown) => Promise<unknown>
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
      withAutomaticReconnect() {
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
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args, _cwd) => {
      calls.push(args.join(" "))
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
    expect(calls).toEqual([
      "rev-parse --is-inside-work-tree",
      "rev-parse --verify refs/heads/mohist/run-wr-1",
      "rebase --show-current-patch",
      "diff --name-only --diff-filter=U",
      "fetch origin master",
    ])
    expect(calls).not.toContain("rev-list --left-right --count origin/master...mohist/run-wr-1")
  })

  it("GetWorkspaceStatus_WhenFetchSucceeds_ReportsAheadBehindFromOriginBase", async () => {
    builders.length = 0
    const calls: string[] = []
    setRunnerSignalRExistsCheckerForTest(() => true)
    setRunnerSignalRGitRunnerForTest(async (_cmd, args, _cwd) => {
      calls.push(args.join(" "))
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
    expect(calls).toEqual([
      "rev-parse --is-inside-work-tree",
      "rev-parse --verify refs/heads/mohist/run-wr-1",
      "rebase --show-current-patch",
      "fetch origin master",
      "rev-list --left-right --count origin/master...mohist/run-wr-1",
    ])
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
