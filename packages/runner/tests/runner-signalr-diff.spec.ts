import { describe, expect, it as vitestIt, vi } from "vitest"
import * as signalR from "@microsoft/signalr"
import { RunnerSignalRClient } from "../src/server/runner-signalr.js"
import type { CommandResult } from "../src/system/process.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../src/system/filesystem.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { currentSignalRTestState, withSignalRTestResources } from "./support/signalr-test-resources.js"


interface CapturedBuilder {
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
  const state = currentSignalRTestState()
  const conn = (state.builders.at(-1) as CapturedBuilder).connection
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
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      private _connection: FakeConnection = makeFakeConnection()
      withUrl(_url: string) {
        currentSignalRTestState().builders.push({ handlers: this._handlers, connection: this._connection })
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

function buildGitOnlyClient() {
  currentSignalRTestState().builders.length = 0
  new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, { allowUnverifiedWorkspaceQueriesForTest: true })
  return lastBuilder()
}

describe("RunnerSignalRClient GetDiff handler", () => {
  it("UnresolvableWorkspace_ReturnsNullAndDoesNotInvokeGit", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

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

  it("MissingBranch_IsResolvesToNullAndDoesNotInvokeGit", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = await handler({
      workspacePath: "/runner-root/workspace",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("MissingWorkspacePath_IsResolvesToNullAndDoesNotInvokeGit", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

    const builder = buildGitOnlyClient()
    const handler = findHandler("GetDiff")

    const result = await handler({
      branch: "mohist/run-wr-1",
      baseBranch: "master",
    })

    expect(result).toBeNull()
    expect(calls).toEqual([])
  })

  it("GitWorkTreeProbeFails_ReturnsNullAndDoesNotIssueDiffOrMergeBase", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => false
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

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

  it("PathExistsButNotWorktree_ReturnsNullAndDoesNotIssueDiffOrMergeBase", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runFail("fatal: not a git repository")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    }

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

  it("HeadRefMissing_ReturnsNullAndDoesNotIssueDiffOrMergeBase", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "rev-parse --verify refs/heads/mohist/run-wr-1":
          return runFail("error: malformed object name")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    }

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

  it("MergeBaseFails_MergeBaseFallsBackToBaseBranch", async (resources) => {
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
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

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

  it("CommitCountLogFails_CommitCountFallsBackToZero", async (resources) => {
    const numstat = "5\t2\tpackages/bar.ts\n"
    const fullDiff = [
      "diff --git a/packages/bar.ts b/packages/bar.ts",
      "index 0000..1111 100644",
      "--- a/packages/bar.ts",
      "+++ b/packages/bar.ts",
    ].join("\n")
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

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

  it("PerFileDiff_IsKeyedByTheBPath", async (resources) => {
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
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

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

  it("BinaryFile_YieldsZeroAdditionsAndDeletionsAndIsBinaryTrue", async (resources) => {
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
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

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
