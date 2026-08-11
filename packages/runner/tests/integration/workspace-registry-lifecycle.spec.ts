import { join } from "node:path"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerSignalRClient } from "../../src/server/runner-signalr.js"
import { WorkspaceManager } from "../../src/runtime/workspace.js"
import { WorkspaceRegistry, defaultWorkspaceRegistryFilePath } from "../../src/runtime/workspace-registry.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../../src/system/filesystem.js"
import { MemoryFileSystem } from "../support/memory-filesystem.js"
import { currentSignalRTestState, withSignalRTestResources } from "../support/signalr-test-resources.js"

interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  state: { connected: boolean }
}

const testGitUrl = "https://repo.test/mohist.git"
type TestResources = {
  fileSystem: RunnerFileSystem
  commandRunner: NonNullable<RunnerResourceContext["commandRunner"]>
  signalRExistsChecker: (path: string) => boolean
}

function it(name: string, body: (resources: TestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const fileSystem = new MemoryFileSystem()
    const resources = {
      fileSystem,
      commandRunner: undefined as unknown as TestResources["commandRunner"],
      signalRExistsChecker: (path: string) => fileSystem.exists(path),
    }
    installGitFake(resources, new Map())
    await withSignalRTestResources(resources, async () => await body(resources))
  })
}

vi.mock("@microsoft/signalr", () => {
  class FakeConnection {
    state = "Disconnected"
    connectionId: string | null = null
    startCalls = 0
    start = vi.fn(async () => {
      this.state = "Connected"
      this.connectionId = "conn-test"
    })
    stop = vi.fn(async () => {
      this.state = "Disconnected"
      this.connectionId = null
    })
    invoke = vi.fn()
    on = vi.fn((event: string, handler: (...args: unknown[]) => unknown) => {
      const builder = currentSignalRTestState().builders.at(-1) as CapturedBuilder | undefined
      if (!builder) throw new Error("no captured SignalR builder")
      builder.handlers.set(event, handler)
      return this
    })
    onreconnected = vi.fn()
  }

  return {
    HubConnectionBuilder: class {
      private _connection = new FakeConnection()
      withUrl() {
        const builder: CapturedBuilder = { handlers: new Map(), state: { connected: true } }
        currentSignalRTestState().builders.push(builder)
        return this
      }
      withAutomaticReconnect() { return this }
      build() {
        return this._connection as unknown as import("@microsoft/signalr").HubConnection
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

function commandResult(stdout = "") {
  return { exitCode: 0, stdout, stderr: "" }
}

function installGitFake(resources: TestResources, workspaceBranches: Map<string, string>) {
  resources.commandRunner = {
    async run(command, args) {
    if (command !== "git") return commandResult()

    const workDir = args[0] === "-C" ? args[1] : null
    const gitArgs = workDir ? args.slice(2) : args

    if (gitArgs[0] === "ls-remote") return commandResult("deadbeef\trefs/heads/main\n")

    if (gitArgs[0] === "clone") {
      const workspacePath = gitArgs.at(-1)!
      await resources.fileSystem.ensureDir(join(workspacePath, ".git", "info"))
      return commandResult()
    }

    if (gitArgs[0] === "checkout" && (gitArgs[1] === "-b" || gitArgs[1] === "-B") && workDir) {
      workspaceBranches.set(workDir, gitArgs[2]!)
      return commandResult()
    }

    if (gitArgs[0] === "remote" && gitArgs[1] === "get-url" && gitArgs[2] === "origin") {
      return commandResult(`${testGitUrl}\n`)
    }

    if (gitArgs[0] === "rev-parse" && gitArgs.includes("--abbrev-ref") && workDir) {
      return commandResult(`${workspaceBranches.get(workDir) ?? workspaceBranches.get(`${workDir}.preparing`) ?? "main"}\n`)
    }

    return commandResult()
    },
  }
}

function work(workflowRunId: string, issueNumber: number, gitUrl: string) {
  return {
    workflowRunId,
    workId: "proposal.1",
    workType: "task",
    uses: "mohist/opencode",
    variables: {
      workflow: { runId: workflowRunId },
      issue: { number: issueNumber, projectId: "project-1" },
      repository: {
        name: "main",
        gitUrl,
        baseBranch: "main",
      },
    },
  }
}

function removalQuery(workflowRunId: string, issueNumber: number, workspacePath: string) {
  return {
    workflowRunId,
    gitUrl: testGitUrl,
    workspacePath,
    branch: `mohist/run-${workflowRunId}`,
    baseBranch: "main",
  }
}

describe("workspace registry lifecycle", () => {
  const root = "/virtual/workspace-registry"

  it("materialization registers an active entry by workflow run", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-001", 42, repo), new AbortController().signal)

    const entry = registry.get("wr-001")
    expect(entry).toMatchObject({
      issueNumber: 42,
      workflowRunId: "wr-001",
      workspacePath: info.path,
      phase: "active",
    })
    expect(entry?.materializedAt).toBeTruthy()
    expect(entry?.terminalAt).toBeNull()

    // The registry is persisted atomically to disk.
    const persisted = JSON.parse(await resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(runnerRoot)))
    expect(persisted.entries["wr-001"]).toMatchObject({ phase: "active", workflowRunId: "wr-001" })
  })

  it("materialization writes only workspace identity to the marker", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-marker", 99, repo), new AbortController().signal)

    const marker = JSON.parse(await resources.fileSystem.readText(join(info.path, ".mohist/workspace.json")))
    expect(marker).toMatchObject({
      workflowRunId: "wr-marker",
      runBranch: "mohist/run-wr-marker",
    })
  })

  it("verification refreshes an existing entry", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")
    const first = new Date("2026-06-01T00:00:00.000Z")
    const second = new Date("2026-06-25T12:00:00.000Z")
    const now = vi.fn<() => Date>()
      .mockReturnValueOnce(first)
      .mockReturnValueOnce(second)
    const registry = new WorkspaceRegistry(runnerRoot, { now })
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work("wr-refresh", 1, repo)
    await manager.prepare(item, new AbortController().signal)
    expect(registry.get("wr-refresh")?.materializedAt).toBe(first.toISOString())

    await manager.verify(item, new AbortController().signal)
    expect(registry.get("wr-refresh")?.materializedAt).toBe(second.toISOString())
    expect(registry.get("wr-refresh")?.phase).toBe("active")
  })

  it("verification does not create a missing registry entry", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work("wr-existing-only", 1, repo)
    await manager.prepare(item, new AbortController().signal)

    // Drop the registry entry directly (simulate a stale / pre-registry
    // disk state) but leave the marker intact.
    await registry.remove("wr-existing-only")

    const verified = await manager.verify(item, new AbortController().signal)
    expect(verified.path).toBeTruthy()
    expect(registry.get("wr-existing-only")).toBeNull()
  })

  it("a fresh registry loads an active entry", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")

    // First host: materialize + register, then "die".
    const registryA = new WorkspaceRegistry(runnerRoot)
    await registryA.load()
    const managerA = new WorkspaceManager(runnerRoot, registryA)
    await managerA.prepare(work("wr-persist", 1, repo), new AbortController().signal)

    // Second host: fresh registry instance, simulates restart.
    const registryB = new WorkspaceRegistry(runnerRoot)
    await registryB.load()

    const entry = registryB.get("wr-persist")
    expect(entry).toMatchObject({
      phase: "active",
      workflowRunId: "wr-persist",
      issueNumber: 1,
    })
    expect(entry?.terminalAt).toBeNull()
  })

  it("manual removal drops the matching registry entry", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-remove", 1, repo), new AbortController().signal)
    expect(registry.get("wr-remove")).not.toBeNull()

    // Stand up a SignalR client bound to the same registry. The handler
    // is registered automatically by the constructor.
    void new RunnerSignalRClient("https://runner.test", "runner-test", runnerRoot, null, { registry })
    const builder = currentSignalRTestState().builders.at(-1) as CapturedBuilder
    const removeHandler = builder.handlers.get("RemoveWorkspace")
    expect(removeHandler).toBeTypeOf("function")

    const result = await (removeHandler as (query: unknown) => Promise<unknown>)(removalQuery("wr-remove", 1, info.path))

    expect(result).toMatchObject({ removed: true, status: "removed" })
    expect(registry.get("wr-remove")).toBeNull()

    // The on-disk registry was rewritten.
    const persisted = JSON.parse(await resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(runnerRoot)))
    expect(persisted.entries["wr-remove"]).toBeUndefined()
  })

  it("manual removal clears the entry when the workspace is already missing", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-gone", 2, repo), new AbortController().signal)
    await resources.fileSystem.deleteDirectory(info.path)
    expect(resources.fileSystem.exists(info.path)).toBe(false)

    void new RunnerSignalRClient("https://runner.test", "runner-test", runnerRoot, null, { registry })
    const removeHandler = (currentSignalRTestState().builders.at(-1) as CapturedBuilder).handlers.get("RemoveWorkspace")!
    const result = await (removeHandler as (query: unknown) => Promise<unknown>)(removalQuery("wr-gone", 2, info.path))

    expect(result).toMatchObject({ removed: false, status: "missing" })
    expect(registry.get("wr-gone")).toBeNull()
  })

  it("manual removal refuses a path outside the runner root", async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-out", 3, repo), new AbortController().signal)

    void new RunnerSignalRClient("https://runner.test", "runner-test", runnerRoot, null, { registry })
    const removeHandler = (currentSignalRTestState().builders.at(-1) as CapturedBuilder).handlers.get("RemoveWorkspace")!

    // Path that resolves outside runnerRoot. The handler should refuse
    // BEFORE the registry is touched so a misbehaving caller cannot
    // drop a registry entry for a directory it never managed.
    await resources.fileSystem.ensureDir(join(root, "outside"))
    const outsidePath = join(root, "outside", "decoy")
    await resources.fileSystem.writeText(outsidePath, "not a workspace")

    const result = await (removeHandler as (query: unknown) => Promise<unknown>)(removalQuery("wr-out", 3, outsidePath))
    expect(result).toMatchObject({ removed: false, reason: "workspace_cleanup_refused" })
    expect(registry.get("wr-out")).not.toBeNull()

    // Sanity: the real workspace entry is unchanged and the directory
    // still exists.
    expect(resources.fileSystem.exists(info.path)).toBe(true)
  })

  it("manual removal preserves a registry entry outside the runner root", async (resources) => {
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const outsidePath = join(root, "outside-root-workspace")
    await resources.fileSystem.ensureDir(outsidePath)
    await registry.register({
      issueNumber: 4,
      workflowRunId: "wr-outside-entry",
      workspacePath: outsidePath,
    })

    void new RunnerSignalRClient("https://runner.test", "runner-test", runnerRoot, null, { registry })
    const removeHandler = (currentSignalRTestState().builders.at(-1) as CapturedBuilder).handlers.get("RemoveWorkspace")!

    const result = await (removeHandler as (query: unknown) => Promise<unknown>)({ workspacePath: outsidePath })

    expect(result).toMatchObject({ removed: false, status: "failed", reason: "workspace_identity_mismatch" })
    expect(registry.get("wr-outside-entry")).toMatchObject({ workspacePath: outsidePath })
    expect(resources.fileSystem.exists(outsidePath)).toBe(true)
  })
})
