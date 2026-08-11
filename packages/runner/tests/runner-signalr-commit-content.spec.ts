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
  const conn = (currentSignalRTestState().builders.at(-1) as CapturedBuilder).connection
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

function buildGitOnlyClient(_resources: SignalRResources) {
  currentSignalRTestState().builders.length = 0
  new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, { allowUnverifiedWorkspaceQueriesForTest: true })
  return lastBuilder()
}

describe("RunnerSignalRClient GetCommitDiff handler", () => {
  it("UnresolvableWorkspace_ReturnsNullAndDoesNotInvokeGit", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

    const builder = buildGitOnlyClient(resources)
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

  it("GitWorkTreeProbeFails_ReturnsNullAndDoesNotIssueShow", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => false
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

    const builder = buildGitOnlyClient(resources)
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

  it("SuccessfulShow_ReturnsThePatch", async (resources) => {
    const patch = "diff --git a/foo b/foo\nindex 0000..1111 100644\n--- a/foo\n+++ b/foo\n@@ -1 +1 @@\n-old\n+new\n"
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show --format= --patch abc123":
          return runOk(patch)
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    }

    const builder = buildGitOnlyClient(resources)
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

  it("NonZeroExit_ReturnsNull", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --is-inside-work-tree":
          return runOk("true\n")
        case "show --format= --patch deadbeef":
          return runFail("fatal: bad revision")
        default:
          return runFail(`unexpected git call: ${args.join(" ")}`)
      }
    }

    const builder = buildGitOnlyClient(resources)
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

describe("RunnerSignalRClient GetFileContent handler", () => {
  it("UnresolvableWorkspace_ReturnsBaseAndHeadNullAndDoesNotInvokeGit", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

    const builder = buildGitOnlyClient(resources)
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

  it("GitWorkTreeProbeFails_ReturnsBaseAndHeadNull", async (resources) => {
    const calls: string[] = []
    resources.signalRExistsChecker = () => false
    resources.signalRGitRunner = async (_cmd, args) => {
      calls.push(args.join(" "))
      return runFail(`unexpected git call: ${args.join(" ")}`)
    }

    const builder = buildGitOnlyClient(resources)
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

  it("BothSidesPresent_ReturnsBaseAndHeadStdout", async (resources) => {
    const baseStdout = "BASE_CONTENT\n"
    const headStdout = "HEAD_CONTENT\n"
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

    const builder = buildGitOnlyClient(resources)
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

  it("BaseMissing_HeadPresent_ReturnsBaseNullAndHeadStdout", async (resources) => {
    const headStdout = "HEAD_ONLY\n"
    const calls: string[] = []
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

    const builder = buildGitOnlyClient(resources)
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

  it("BasePresent_HeadMissing_ReturnsBaseStdoutAndHeadNull", async (resources) => {
    const baseStdout = "BASE_ONLY\n"
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

    const builder = buildGitOnlyClient(resources)
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

  it("BothSidesMissing_ReturnsBaseAndHeadNull", async (resources) => {
    resources.signalRExistsChecker = () => true
    resources.signalRGitRunner = async (_cmd, args) => {
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
    }

    const builder = buildGitOnlyClient(resources)
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
