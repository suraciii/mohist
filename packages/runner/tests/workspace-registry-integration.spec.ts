import { existsSync } from "node:fs"
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  RunnerSignalRClient,
  setRunnerSignalRExistsCheckerForTest,
} from "../src/server/runner-signalr.js"
import { WorkspaceManager } from "../src/runtime/workspace.js"
import { WorkspaceRegistry, defaultWorkspaceRegistryFilePath } from "../src/runtime/workspace-registry.js"
import { exists, runCommand } from "../src/system/process.js"

// End-to-end coverage of T-002: the runner-local workspace registry is
// persisted at <runnerRoot>/.mohist/runner-state/workspaces.json, written
// through on every WorkspaceManager.materialize() / verify() success, and
// dropped when the manual RemoveWorkspace SignalR handler removes the
// matching directory. The on-disk marker stays exactly identity-only.

interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  state: { connected: boolean }
}

const builders: CapturedBuilder[] = []

beforeEach(() => {
  builders.length = 0
  setRunnerSignalRExistsCheckerForTest(existsSync)
})

afterEach(async () => {
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

async function makeRepo(root: string) {
  const repo = join(root, "repo")
  await runCommand("git", ["init", repo], ".", new AbortController().signal, {
    GIT_AUTHOR_NAME: "Mohist Test",
    GIT_AUTHOR_EMAIL: "mohist-test@example.com",
    GIT_COMMITTER_NAME: "Mohist Test",
    GIT_COMMITTER_EMAIL: "mohist-test@example.com",
  })
  await writeFile(join(repo, "README.md"), "base\n")
  await runCommand("git", ["-C", repo, "add", "."], ".", new AbortController().signal)
  await runCommand("git", ["-C", repo, "commit", "-m", "base"], ".", new AbortController().signal, {
    GIT_AUTHOR_NAME: "Mohist Test",
    GIT_AUTHOR_EMAIL: "mohist-test@example.com",
    GIT_COMMITTER_NAME: "Mohist Test",
    GIT_COMMITTER_EMAIL: "mohist-test@example.com",
  })
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
      repository: { name: "master", gitUrl, baseBranch: "master" },
      openspecChangeDir: "openspec/changes/issue-9",
    },
  }
}

describe("WorkspaceManager + WorkspaceRegistry (T-002)", () => {
  let root: string

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "mohist-manager-registry-"))
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  it("Materialize_RegistersActiveEntryKeyedByWorkflowRunId", async () => {
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.materialize(work("wr-001", "issue-42", 42, repo), new AbortController().signal)

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

  it("Marker_WrittenOnMaterialize_ContainsExactlyIdentityFields", async () => {
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.materialize(work("wr-marker", "issue-99", 99, repo), new AbortController().signal)

    const marker = JSON.parse(await readFile(join(info.path, ".mohist/workspace.json"), "utf8"))
    expect(Object.keys(marker).sort()).toEqual(["issueId", "issueNumber", "workflowRunId"])
    expect(marker).toEqual({ issueId: "issue-99", issueNumber: 99, workflowRunId: "wr-marker" })
  })

  it("Verify_RefreshesMaterializedAtForExistingEntry", async () => {
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")
    const first = new Date("2026-06-01T00:00:00.000Z")
    const second = new Date("2026-06-25T12:00:00.000Z")
    const now = vi.fn<Date, []>()
      .mockReturnValueOnce(first)
      .mockReturnValueOnce(second)
    const registry = new WorkspaceRegistry(runnerRoot, { now })
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work("wr-refresh", "issue-1", 1, repo)
    await manager.materialize(item, new AbortController().signal)
    expect(registry.get("wr-refresh")?.materializedAt).toBe(first.toISOString())

    await manager.verify(item, new AbortController().signal)
    expect(registry.get("wr-refresh")?.materializedAt).toBe(second.toISOString())
    expect(registry.get("wr-refresh")?.phase).toBe("active")
  })

  it("Verify_OnRunIdWithNoRegistryEntry_DoesNotCreateEntry", async () => {
    // Verify is the dispatch-time read path. If a verify() ever runs
    // against a run id that has no registry entry (e.g. an existing
    // marker from a pre-registry runner build), verify() must NOT
    // fabricate a registry entry — only materialize() is the
    // registration point. T-002 explicitly says verify "refreshes
    // materializedAt"; a missing entry stays missing.
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work("wr-existing-only", "issue-1", 1, repo)
    await manager.materialize(item, new AbortController().signal)

    // Drop the registry entry directly (simulate a stale / pre-registry
    // disk state) but leave the marker intact.
    await registry.remove("wr-existing-only")

    const verified = await manager.verify(item, new AbortController().signal)
    expect(verified.path).toBeTruthy()
    expect(registry.get("wr-existing-only")).toBeNull()
  })

  it("Registry_LoadedFreshOnEachHostStart_PreservesActivePhase", async () => {
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")

    // First host: materialize + register, then "die".
    const registryA = new WorkspaceRegistry(runnerRoot)
    await registryA.load()
    const managerA = new WorkspaceManager(runnerRoot, registryA)
    await managerA.materialize(work("wr-persist", "issue-1", 1, repo), new AbortController().signal)

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

  it("ManualRemoveWorkspace_RemovesMatchingRegistryEntry", async () => {
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.materialize(work("wr-remove", "issue-1", 1, repo), new AbortController().signal)
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

  it("ManualRemoveWorkspace_AlreadyMissingDirectory_StillDropsRegistryEntry", async () => {
    // T-002 notes: "safeRemove must tolerate an already-missing
    // directory (treat as removed, delete the entry)". The existing
    // SignalR handler returns "workspace_missing" for missing paths
    // but must also clear the registry entry so the registry reflects
    // the real disk state.
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.materialize(work("wr-gone", "issue-2", 2, repo), new AbortController().signal)
    await rm(info.path, { recursive: true, force: true })
    expect(exists(info.path)).toBe(false)

    void new RunnerSignalRClient("http://localhost:0", "runner-test", runnerRoot, null, { registry })
    const removeHandler = builders.at(-1)!.handlers.get("RemoveWorkspace")!
    const result = await (removeHandler as (query: unknown) => Promise<unknown>)({ workspacePath: info.path })

    expect(result).toMatchObject({ removed: false, status: "missing" })
    expect(registry.get("wr-gone")).toBeNull()
  })

  it("ManualRemoveWorkspace_PathOutsideRunnerRoot_DoesNotTouchRegistry", async () => {
    const repo = await makeRepo(root)
    const runnerRoot = join(root, "runner")
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.materialize(work("wr-out", "issue-3", 3, repo), new AbortController().signal)

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
})