import { afterEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { isUnderRunnerRoot, normalizeMaterializePayload, resolveWorkspaceQuery, RunnerSignalRClient, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { CommandResult } from "../src/system/process.js"

interface CapturedBuilder {
  url?: string
  handlers: Array<() => void>
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
      private _handlers: Array<() => void> = []
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
    const start = Date.now()
    const result = await tight.probeLiveness(new AbortController().signal)
    const elapsed = Date.now() - start
    expect(result).toBe(false)
    expect(elapsed).toBeGreaterThanOrEqual(5)
    // Drain the pending invokes so vitest doesn't keep the promises alive.
    resolveInvoke("late")
    resolveTight("late")
    void client
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

describe("normalizeMaterializePayload", () => {
  // Regression: WorkDispatch.Variables is `string?` on the C# side, so the
  // SignalR wire format carries `variables` as a JSON-encoded string. The
  // MaterializeWorkspace handler previously passed that string straight to
  // workspaceManager.materialize, where stringAt(... ["repository","gitUrl"])
  // returned undefined (string is not an object) and every retry-time
  // re-materialization threw "Workspace requires repository.gitUrl...".
  const fullVars = {
    issue: { id: "issue_1", number: 212, title: "t", body: "" },
    repository: { name: "master", gitUrl: "https://github.com/x/y.git", baseBranch: "master" },
    project: { id: "proj_1", name: "demo" },
    mohist: { system: "mohist", runId: "wr_1" },
    workspace: { path: "/tmp/ws", branch: "mohist/run-wr_1", changeDir: "openspec/changes/issue-212" },
  }

  it("ParsesStringVariablesIntoObject_SignalRWireFormat", () => {
    const work = normalizeMaterializePayload({
      workflowRunId: "wr_1",
      workId: "T-001.1",
      workType: "task",
      stage: "build",
      variables: JSON.stringify(fullVars),
      with: JSON.stringify({ agent: { type: "opencode" } }),
    })

    expect(work.variables).toEqual(fullVars)
    expect(work.variables).not.toBeTypeOf("string")
    expect(work.with).toEqual({ agent: { type: "opencode" } })
  })

  it("PreservesObjectVariables_AlreadyParsedShape", () => {
    const work = normalizeMaterializePayload({
      workflowRunId: "wr_1",
      workId: "T-001.1",
      workType: "task",
      stage: "build",
      variables: fullVars,
    })

    expect(work.variables).toEqual(fullVars)
  })

  it("ExposesRepositoryAndIssueAtExpectedPaths_AfterStringParse", () => {
    const work = normalizeMaterializePayload({
      workflowRunId: "wr_1",
      workId: "T-001.1",
      workType: "task",
      variables: JSON.stringify(fullVars),
    })

    // These are the exact reads workspace.ts materialize() performs.
    expect((work.variables as Record<string, unknown>)["repository"]).toEqual(fullVars.repository)
    expect((work.variables as Record<string, unknown>)["issue"]).toEqual(fullVars.issue)
  })

  it("RejectsNonObjectPayload", () => {
    expect(() => normalizeMaterializePayload(null)).toThrow()
    expect(() => normalizeMaterializePayload("not-an-object")).toThrow()
    expect(() => normalizeMaterializePayload([])).toThrow()
  })
})
