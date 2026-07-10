import { existsSync } from "node:fs"
import { mkdir, readFile, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  RunnerSignalRClient,
  setRunnerSignalRExistsCheckerForTest,
} from "../../src/server/runner-signalr.js"
import { WorkspaceManager } from "../../src/runtime/workspace.js"
import { WorkspaceRegistry, defaultWorkspaceRegistryFilePath } from "../../src/runtime/workspace-registry.js"
import { exists, runCommand } from "../../src/system/process.js"
import { clearedInheritedGitEnvironment } from "../support/git-environment.js"
import { createTestTempDir } from "../support/temp-dir.js"

interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  state: { connected: boolean }
}

const builders: CapturedBuilder[] = []
let root: string
let environment: GitEnvironment

interface GitEnvironment extends NodeJS.ProcessEnv {
  HOME: string
  XDG_CONFIG_HOME: string
  GIT_CONFIG_GLOBAL: string
  GIT_CONFIG_COUNT: "0"
  GIT_CONFIG_NOSYSTEM: "1"
  GIT_TERMINAL_PROMPT: "0"
  GIT_AUTHOR_NAME: string
  GIT_AUTHOR_EMAIL: string
  GIT_COMMITTER_NAME: string
  GIT_COMMITTER_EMAIL: string
}

beforeEach(async () => {
  builders.length = 0
  setRunnerSignalRExistsCheckerForTest(existsSync)
  root = await createTestTempDir("mohist-workspace-registry-integration-")
  environment = await createGitEnvironment(root)
  isolateGitEnvironment(environment)
})

afterEach(() => {
  setRunnerSignalRExistsCheckerForTest(null)
})

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
      const builder = builders.at(-1)!
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
        builders.push(builder)
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

async function makeRepo(root: string, environment: GitEnvironment) {
  const repo = join(root, "repo")
  await git(root, ["init", "--initial-branch=main", repo], environment)
  await git(repo, ["config", "user.name", environment.GIT_AUTHOR_NAME], environment)
  await git(repo, ["config", "user.email", environment.GIT_AUTHOR_EMAIL], environment)
  await git(repo, ["config", "core.hooksPath", join(root, "hooks")], environment)
  await writeFile(join(repo, "README.md"), "base\n")
  await git(repo, ["add", "."], environment)
  await git(repo, ["commit", "-m", "base"], environment)
  return repo
}

function work(workflowRunId: string, issueId: string, issueNumber: number, gitUrl: string) {
  return {
    workflowRunId,
    workId: "proposal.1",
    workType: "task",
    uses: "mohist/acp-agent",
    variables: {
      mohist: { runId: workflowRunId },
      issue: { id: issueId, number: issueNumber },
      project: { id: "project-1", name: "Mohist Local" },
      repository: { name: "main", gitUrl, baseBranch: "main" },
      openspecChangeDir: "openspec/changes/sample-change",
    },
  }
}

describe("workspace registry lifecycle", () => {
  it("materialization registers an active entry by workflow run", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-001", "issue-42", 42, repo), new AbortController().signal)

    const entry = registry.get("wr-001")
    expect(entry).toMatchObject({
      issueId: "issue-42",
      issueNumber: 42,
      workflowRunId: "wr-001",
      workspacePath: info.path,
      phase: "active",
    })
    expect(entry?.materializedAt).toBeTruthy()
    expect(entry?.terminalAt).toBeNull()

    // The registry is persisted atomically to disk.
    const persisted = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(runnerRoot), "utf8"))
    expect(persisted.entries["wr-001"]).toMatchObject({ phase: "active", workflowRunId: "wr-001" })
  })

  it("materialization writes only workspace identity to the marker", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-marker", "issue-99", 99, repo), new AbortController().signal)

    const marker = JSON.parse(await readFile(join(info.path, ".mohist/workspace.json"), "utf8"))
    expect(Object.keys(marker).sort()).toEqual(["issueId", "issueNumber", "workflowRunId"])
    expect(marker).toEqual({ issueId: "issue-99", issueNumber: 99, workflowRunId: "wr-marker" })
  })

  it("verification refreshes an existing entry", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")
    const first = new Date("2026-06-01T00:00:00.000Z")
    const second = new Date("2026-06-25T12:00:00.000Z")
    const now = vi.fn<() => Date>()
      .mockReturnValueOnce(first)
      .mockReturnValueOnce(second)
    const registry = new WorkspaceRegistry(runnerRoot, { now })
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work("wr-refresh", "issue-1", 1, repo)
    await manager.prepare(item, new AbortController().signal)
    expect(registry.get("wr-refresh")?.materializedAt).toBe(first.toISOString())

    await manager.verify(item, new AbortController().signal)
    expect(registry.get("wr-refresh")?.materializedAt).toBe(second.toISOString())
    expect(registry.get("wr-refresh")?.phase).toBe("active")
  })

  it("verification does not create a missing registry entry", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work("wr-existing-only", "issue-1", 1, repo)
    await manager.prepare(item, new AbortController().signal)

    // Drop the registry entry directly (simulate a stale / pre-registry
    // disk state) but leave the marker intact.
    await registry.remove("wr-existing-only")

    const verified = await manager.verify(item, new AbortController().signal)
    expect(verified.path).toBeTruthy()
    expect(registry.get("wr-existing-only")).toBeNull()
  })

  it("a fresh registry loads an active entry", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")

    // First host: materialize + register, then "die".
    const registryA = new WorkspaceRegistry(runnerRoot)
    await registryA.load()
    const managerA = new WorkspaceManager(runnerRoot, registryA)
    await managerA.prepare(work("wr-persist", "issue-1", 1, repo), new AbortController().signal)

    // Second host: fresh registry instance, simulates restart.
    const registryB = new WorkspaceRegistry(runnerRoot)
    await registryB.load()

    const entry = registryB.get("wr-persist")
    expect(entry).toMatchObject({
      phase: "active",
      workflowRunId: "wr-persist",
      issueId: "issue-1",
      issueNumber: 1,
    })
    expect(entry?.terminalAt).toBeNull()
  })

  it("manual removal drops the matching registry entry", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-remove", "issue-1", 1, repo), new AbortController().signal)
    expect(registry.get("wr-remove")).not.toBeNull()

    // Stand up a SignalR client bound to the same registry. The handler
    // is registered automatically by the constructor.
    void new RunnerSignalRClient("http://localhost:0", "runner-test", runnerRoot, null, { registry })
    const builder = builders.at(-1)!
    const removeHandler = builder.handlers.get("RemoveWorkspace")
    expect(removeHandler).toBeTypeOf("function")

    const result = await (removeHandler as (query: unknown) => Promise<unknown>)({ workspacePath: info.path })

    expect(result).toMatchObject({ removed: true, status: "removed" })
    expect(registry.get("wr-remove")).toBeNull()

    // The on-disk registry was rewritten.
    const persisted = JSON.parse(await readFile(defaultWorkspaceRegistryFilePath(runnerRoot), "utf8"))
    expect(persisted.entries["wr-remove"]).toBeUndefined()
  })

  it("manual removal clears the entry when the workspace is already missing", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-gone", "issue-2", 2, repo), new AbortController().signal)
    await rm(info.path, { recursive: true, force: true })
    expect(exists(info.path)).toBe(false)

    void new RunnerSignalRClient("http://localhost:0", "runner-test", runnerRoot, null, { registry })
    const removeHandler = builders.at(-1)!.handlers.get("RemoveWorkspace")!
    const result = await (removeHandler as (query: unknown) => Promise<unknown>)({ workspacePath: info.path })

    expect(result).toMatchObject({ removed: false, status: "missing" })
    expect(registry.get("wr-gone")).toBeNull()
  })

  it("manual removal refuses a path outside the runner root", async () => {
    const repo = await makeRepo(root, environment)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work("wr-out", "issue-3", 3, repo), new AbortController().signal)

    void new RunnerSignalRClient("http://localhost:0", "runner-test", runnerRoot, null, { registry })
    const removeHandler = builders.at(-1)!.handlers.get("RemoveWorkspace")!

    // Path that resolves outside runnerRoot. The handler should refuse
    // BEFORE the registry is touched so a misbehaving caller cannot
    // drop a registry entry for a directory it never managed.
    await mkdir(join(root, "outside"), { recursive: true })
    const outsidePath = join(root, "outside", "decoy")
    await writeFile(outsidePath, "not a workspace")

    const result = await (removeHandler as (query: unknown) => Promise<unknown>)({ workspacePath: outsidePath })
    expect(result).toMatchObject({ removed: false, reason: "workspace_cleanup_refused" })
    expect(registry.get("wr-out")).not.toBeNull()

    // Sanity: the real workspace entry is unchanged and the directory
    // still exists.
    expect(exists(info.path)).toBe(true)
  })

  it("manual removal preserves a registry entry outside the runner root", async () => {
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const outsidePath = join(root, "outside-root-workspace")
    await mkdir(outsidePath, { recursive: true })
    await registry.register({
      issueId: "issue-outside",
      issueNumber: 4,
      workflowRunId: "wr-outside-entry",
      workspacePath: outsidePath,
    })

    void new RunnerSignalRClient("http://localhost:0", "runner-test", runnerRoot, null, { registry })
    const removeHandler = builders.at(-1)!.handlers.get("RemoveWorkspace")!

    const result = await (removeHandler as (query: unknown) => Promise<unknown>)({ workspacePath: outsidePath })

    expect(result).toMatchObject({ removed: false, status: "failed", reason: "workspace_cleanup_refused" })
    expect(registry.get("wr-outside-entry")).toMatchObject({ workspacePath: outsidePath })
    expect(exists(outsidePath)).toBe(true)
  })
})

async function createGitEnvironment(root: string): Promise<GitEnvironment> {
  const home = join(root, "home")
  const xdg = join(root, "xdg")
  const globalConfig = join(root, "gitconfig")
  const hooks = join(root, "hooks")
  await Promise.all([mkdir(home, { recursive: true }), mkdir(xdg, { recursive: true }), mkdir(hooks, { recursive: true })])
  await writeFile(globalConfig, "")
  return {
    ...clearedInheritedGitEnvironment,
    HOME: home,
    XDG_CONFIG_HOME: xdg,
    GIT_CONFIG_GLOBAL: globalConfig,
    GIT_CONFIG_COUNT: "0",
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_TERMINAL_PROMPT: "0",
    GIT_AUTHOR_NAME: "Mohist Integration Test",
    GIT_AUTHOR_EMAIL: "mohist-integration@example.test",
    GIT_COMMITTER_NAME: "Mohist Integration Test",
    GIT_COMMITTER_EMAIL: "mohist-integration@example.test",
  }
}

async function git(cwd: string, args: string[], environment: GitEnvironment) {
  const result = await runCommand("git", args, cwd, new AbortController().signal, environment)
  if (result.exitCode !== 0) throw new Error(result.stderr || result.stdout || `git ${args.join(" ")} failed`)
  return result
}

function isolateGitEnvironment(environment: GitEnvironment) {
  for (const [key, value] of Object.entries(environment)) vi.stubEnv(key, value)
}
