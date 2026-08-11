import { describe, expect, it as vitestIt, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { isUnderRunnerRoot, resolveWorkspaceQuery, RunnerSignalRClient } from "../src/server/runner-signalr.js"
import type { CommandResult } from "../src/system/process.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import type { AgentSessionRuntimeEventOutbox } from "../src/server/runtime-event-outbox.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../src/system/filesystem.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { currentSignalRTestState, withSignalRTestResources } from "./support/signalr-test-resources.js"

interface CapturedBuilder {
  url?: string
  accessTokenFactory?: () => string
  reconnectPolicy?: number[]
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

type SignalRResources = {
  fileSystem: RunnerFileSystem
  signalRGitRunner?: NonNullable<RunnerResourceContext["signalRGitRunner"]>
  signalRExistsChecker?: (path: string) => boolean
}

function it(name: string, body: (resources: SignalRResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: SignalRResources = { fileSystem: new MemoryFileSystem() }
    await withSignalRTestResources(resources, async () => await body(resources))
  })
}

function findHandler(name: string): (...args: unknown[]) => unknown {
  const conn = currentBuilder().connection
  const call = conn.on.mock.calls.find(([event]) => event === name)
  const handler = call?.[1]
  if (typeof handler !== "function") throw new Error(`handler not registered: ${name}`)
  return (...args) => handler(...args)
}

function currentBuilder(): CapturedBuilder {
  const builder = currentSignalRTestState().builders.at(-1) as CapturedBuilder | undefined
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
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
    conn.connectionId = `conn-${++currentSignalRTestState().nextConnectionId}`
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
      withUrl(url: string, options?: { accessTokenFactory?: () => string }) {
        this._url = url
        currentSignalRTestState().builders.push({ url, accessTokenFactory: options?.accessTokenFactory, handlers: this._handlers, connection: this._connection })
        return this
      }
      withAutomaticReconnect(reconnectPolicy: number[]) {
        const builder = currentSignalRTestState().builders.at(-1) as CapturedBuilder | undefined
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

describe("RunnerSignalRClient workspace queries", () => {
  it("WorkspaceQuery_ParsesIdentityLessRequestButHandlersRejectIt", () => {
    const query = resolveWorkspaceQuery({
      workspacePath: "/virtual/workspaces/issue-25",
      branch: "mohist/run-wr-25",
      baseBranch: "master",
    })

    expect(query).toEqual({
      workDir: "/virtual/workspaces/issue-25",
      baseBranch: "master",
      head: "mohist/run-wr-25",
    })
  })

  it("WorkspaceQuery_RejectsMissingBaseBranchInsteadOfGuessingMain", () => {
    const query = resolveWorkspaceQuery({
      workspacePath: "/virtual/workspaces/issue-25",
      branch: "mohist/run-wr-25",
    })

    expect(query).toBeNull()
  })

  it("WorkspaceQuery_RejectsMissingHeadInsteadOfFallingBackToMoIssue", () => {
    // The legacy `mo/issue-{N}` worktree branch is no longer materialized by
    // the runner; the dispatch must supply the per-run head ref.
    const query = resolveWorkspaceQuery({
      issueNumber: 25,
      workspacePath: "/virtual/workspaces/issue-25",
      baseBranch: "master",
    })

    expect(query).toBeNull()
  })

  it("IssueWorkspaceQuery_RejectsPartialIdentity", () => {
    expect(resolveWorkspaceQuery({
      workflowRunId: "wr-1",
      projectId: "project-1",
      issueNumber: 1,
      repositoryName: "web",
      workspacePath: "/runner/workspaces/run-hash",
      branch: "mohist/run-wr-1",
      baseBranch: "develop",
    })).toBeNull()
  })

  it("IssueWorkspaceQuery_CarriesCompleteIdentity", () => {
    const query = resolveWorkspaceQuery({
      workflowRunId: "wr-1",
      gitUrl: "https://example.test/web.git",
      workspacePath: "/runner/workspaces/run-hash",
      branch: "mohist/run-wr-1",
      baseBranch: "develop",
    })
    expect(query).toMatchObject({ workDir: "/runner/workspaces/run-hash", baseBranch: "develop", head: "mohist/run-wr-1", identity: { workflowRunId: "wr-1", gitUrl: "https://example.test/web.git" } })
  })

  it("WorkspaceRemoval_OnlyAllowsPathsUnderRunnerRoot", () => {
    expect(isUnderRunnerRoot("/virtual/projects", "/virtual/projects/app/workspaces/issue-1")).toBe(true)
    expect(isUnderRunnerRoot("/virtual/projects", "/virtual/projects")).toBe(false)
    expect(isUnderRunnerRoot("/virtual/projects", "/virtual/other/issue-1")).toBe(false)
  })

  it("GetWorkspaceStatus_RejectsIdentityLessRequestBeforeGit", async (resources) => {
    currentSignalRTestState().builders.length = 0
    const calls: Array<{ command: string; timeoutMs: number | undefined }> = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args, _cwd, _signal, _env, options) => {
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
    }

    new RunnerSignalRClient("https://runner.test", "runner-1", "/runner-root", null)
    const handler = findHandler("GetWorkspaceStatus")
    const status = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(status).toEqual({ exists: false, reason: "workspace_identity_mismatch" })
    expect(calls).toEqual([])
  })

  it("GetWorkspaceStatus_RejectsIdentityLessRequestWithoutFetching", async (resources) => {
    currentSignalRTestState().builders.length = 0
    const calls: Array<{ command: string; timeoutMs: number | undefined }> = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args, _cwd, _signal, _env, options) => {
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
    }

    new RunnerSignalRClient("https://runner.test", "runner-1", "/runner-root", null)
    const handler = findHandler("GetWorkspaceStatus")
    const status = await handler({
      workspacePath: "/runner-root/workspace",
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(status).toEqual({ exists: false, reason: "workspace_identity_mismatch" })
    expect(calls).toEqual([])
  })
})

describe("RunnerSignalRClient handshake", () => {
  it("IncludesBuildGitHashInQueryStringWhenProvided", () => {
    currentSignalRTestState().builders.length = 0
    const hash = "abcdef1234567890abcdef1234567890abcdef12"
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", hash)
    const last = currentBuilder()
    expect(last?.url).toBe(`https://runner.test/hubs/runner?runnerId=runner-1&buildGitHash=${hash}`)
  })

  it("OmitsBuildGitHashWhenNull", () => {
    currentSignalRTestState().builders.length = 0
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const last = currentBuilder()
    expect(last?.url).toBe("https://runner.test/hubs/runner?runnerId=runner-1")
  })

  it("OmitsBuildGitHashWhenNotProvided", () => {
    currentSignalRTestState().builders.length = 0
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects")
    const last = currentBuilder()
    expect(last?.url).toBe("https://runner.test/hubs/runner?runnerId=runner-1")
  })

  it("ConfiguresFixedAutomaticReconnectIntervals", () => {
    currentSignalRTestState().builders.length = 0
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const last = currentBuilder()
    expect(last?.reconnectPolicy).toEqual([0, 2000, 5000, 10000, 30000])
  })

  it("PresentsTheMachineCredentialAsTheAccessToken", () => {
    currentSignalRTestState().builders.length = 0
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      credential: "moh_runner_abc",
    })
    const last = currentBuilder()
    expect(last?.accessTokenFactory?.()).toBe("moh_runner_abc")
  })

  it("OmitsTheAccessTokenWithoutACredential", () => {
    currentSignalRTestState().builders.length = 0
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const last = currentBuilder()
    expect(last?.accessTokenFactory?.()).toBe("")
  })
})

describe("RunnerSignalRClient liveness + reconnect", () => {
  function recoveryOutbox(recover = vi.fn(async () => {})): AgentSessionRuntimeEventOutbox {
    return {
      ready: () => false,
      load: async () => {},
      recover,
      enqueueBeforeExecution: async () => {},
      enqueueProducedFact: async () => {},
      enqueueProducedFactBatch: async () => {},
      kick: async () => {},
      stop: async () => {},
      snapshot: () => [],
    }
  }

  it("GetConnectionId_IsNullBeforeStart", () => {
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    expect(client.getConnectionId()).toBeNull()
  })

  it("GetConnectionId_IsAssignedAfterStart", async () => {
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    await client.start()
    const id = client.getConnectionId()
    expect(id).not.toBeNull()
    expect(id).toMatch(/^conn-/)
    await client.stop()
  })

  it("Start_RecoversRuntimeEventOutbox", async () => {
    currentSignalRTestState().builders.length = 0
    const recover = vi.fn(async () => {})
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      agentSessionRuntimeEventOutbox: recoveryOutbox(recover),
    })

    await client.start()

    expect(recover).toHaveBeenCalledTimes(1)
    await client.stop()
  })

  it("Disconnect_DoesNotStopRuntimeEventOutbox", async () => {
    currentSignalRTestState().builders.length = 0
    const stopOutbox = vi.fn(async () => {})
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      agentSessionRuntimeEventOutbox: {
        ...recoveryOutbox(),
        stop: stopOutbox,
      },
    })

    await client.disconnect()

    expect(currentBuilder().connection.stop).toHaveBeenCalledTimes(1)
    expect(stopOutbox).not.toHaveBeenCalled()
  })

  it("ProbeLiveness_ReturnsFalse_WhenNotConnected", async () => {
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    // state is Disconnected (never started)
    const result = await client.probeLiveness(new AbortController().signal)
    expect(result).toBe(false)
  })

  it("ProbeLiveness_ReturnsTrue_OnSuccessfulPing", async () => {
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-1"
    conn.invoke.mockResolvedValue("conn-1")
    const result = await client.probeLiveness(new AbortController().signal)
    expect(result).toBe(true)
    expect(conn.invoke).toHaveBeenCalledWith("Ping")
  })

  it("ProbeLiveness_ReturnsFalse_OnInvokeRejection", async () => {
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-1"
    conn.invoke.mockRejectedValue(new Error("invoke failed"))
    const result = await client.probeLiveness(new AbortController().signal)
    expect(result).toBe(false)
  })

  it("ProbeLiveness_ReturnsFalse_OnTimeout", async () => {
    vi.useFakeTimers()
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-1"
    let resolveInvoke: (value: unknown) => void = () => undefined
    conn.invoke.mockReturnValue(new Promise((resolve) => {
      resolveInvoke = resolve
    }))
    // Re-construct with a tiny probe timeout
    const tight = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, { probeTimeoutMs: 5 })
    const tightConn = currentBuilder().connection
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
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, { probeTimeoutMs: 5_000 })
    const conn = currentBuilder().connection
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
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    await client.forceReconnect(new AbortController().signal)
    expect(conn.stop).toHaveBeenCalled()
    expect(conn.start).toHaveBeenCalled()
    expect(client.getConnectionId()).not.toBe("conn-old")
  })

  it("ForceReconnect_NotifiesCallbackAfterManualReconnect", async () => {
    currentSignalRTestState().builders.length = 0
    const seen: string[] = []
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      onReconnected: (id) => seen.push(id),
    })
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"

    await client.forceReconnect(new AbortController().signal)

    expect(conn.stop).toHaveBeenCalled()
    expect(conn.start).toHaveBeenCalled()
    expect(seen).toEqual([client.getConnectionId()])
    expect(seen[0]).toMatch(/^conn-/)
  })

  it("ForceReconnect_RecoversRuntimeEventOutbox", async () => {
    currentSignalRTestState().builders.length = 0
    const recover = vi.fn(async () => {})
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      agentSessionRuntimeEventOutbox: recoveryOutbox(recover),
    })
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected

    await client.forceReconnect(new AbortController().signal)

    expect(recover).toHaveBeenCalledTimes(1)
  })

  it("ForceReconnect_StartsDirectly_WhenDisconnected", async () => {
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const conn = currentBuilder().connection
    // initial state is Disconnected
    await client.forceReconnect(new AbortController().signal)
    expect(conn.stop).not.toHaveBeenCalled()
    expect(conn.start).toHaveBeenCalled()
  })

  it("ForceReconnect_SwallowsStopError_AndStillStarts", async () => {
    currentSignalRTestState().builders.length = 0
    const client = new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null)
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    conn.stop.mockRejectedValueOnce(new Error("stop failed"))
    await expect(client.forceReconnect(new AbortController().signal)).resolves.toBeUndefined()
    expect(conn.start).toHaveBeenCalled()
  })

  it("OnReconnected_FiresHostCallback_WithNewConnectionId", async () => {
    currentSignalRTestState().builders.length = 0
    const seen: string[] = []
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      onReconnected: (id) => seen.push(id),
    })
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    expect(conn.onreconnected).toBeDefined()
    // Simulate SignalR auto-reconnect completing
    conn.connectionId = "conn-new"
    conn._reconnectHandler?.("conn-new")
    expect(seen).toEqual(["conn-new"])
  })

  it("OnReconnected_RecoversRuntimeEventOutbox", async () => {
    currentSignalRTestState().builders.length = 0
    const recover = vi.fn(async () => {})
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      agentSessionRuntimeEventOutbox: recoveryOutbox(recover),
    })
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-new"

    conn._reconnectHandler?.("conn-new")
    await Promise.resolve()

    expect(recover).toHaveBeenCalledTimes(1)
  })

  it("OnReconnected_UsesConnectionConnectionId_WhenCallbackArgMissing", async () => {
    currentSignalRTestState().builders.length = 0
    const seen: string[] = []
    new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
      onReconnected: (id) => seen.push(id),
    })
    const conn = currentBuilder().connection
    conn.state = signalR.HubConnectionState.Connected
    conn.connectionId = "conn-old"
    conn.connectionId = "conn-new"
    conn._reconnectHandler?.()
    expect(seen).toEqual(["conn-new"])
  })
})
