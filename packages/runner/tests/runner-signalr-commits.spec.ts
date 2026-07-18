import { afterEach, describe, expect, it, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { RunnerSignalRClient, setRunnerSignalRExistsCheckerForTest, setRunnerSignalRGitRunnerForTest } from "../src/server/runner-signalr.js"
import type { CommandResult } from "../src/system/process.js"


interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

const builders: CapturedBuilder[] = []
let nextConnectionId = 0

afterEach(() => {
  vi.restoreAllMocks()
  builders.length = 0
  nextConnectionId = 0
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
    conn.connectionId = `conn-${++nextConnectionId}`
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
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      private _connection: FakeConnection = makeFakeConnection()
      withUrl(_url: string) {
        builders.push({ handlers: this._handlers, connection: this._connection })
        return this
      }
      withAutomaticReconnect(_reconnectPolicy: number[]) {
        return this
      }
      build() {
        this._connection.on.mockImplementation((event: string, handler: (...args: unknown[]) => unknown) => {
          this._handlers.set(event, handler)
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

function runOk(stdout = ""): CommandResult {
  return { exitCode: 0, stdout, stderr: "" }
}

function runFail(stderr = ""): CommandResult {
  return { exitCode: 1, stdout: "", stderr }
}

function buildGitOnlyClient() {
  builders.length = 0
  new RunnerSignalRClient("http://localhost:3456", "runner-1", "/tmp/mohist/projects", null, { allowUnverifiedWorkspaceQueriesForTest: true })
  return lastBuilder()
}

describe("RunnerSignalRClient GetCommits handler", () => {
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
