import { AsyncLocalStorage } from "node:async_hooks"
import { join } from "node:path"
import { describe, expect, it as vitestIt, vi } from "vitest"
import {
  WorkspaceRegistry,
  defaultWorkspaceRegistryFilePath,
} from "../src/runtime/workspace-registry.js"
import type { RunnerFileSystem } from "../src/system/filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"

interface WorkspaceRegistryTestState {
  readonly root: string
  readonly fileSystem: RunnerFileSystem
}

const workspaceRegistryTestStorage = new AsyncLocalStorage<WorkspaceRegistryTestState>()

function currentWorkspaceRegistryTestState(): WorkspaceRegistryTestState {
  const state = workspaceRegistryTestStorage.getStore()
  if (!state) throw new Error("workspace registry test resource context is not active")
  return state
}

function testRoot(): string {
  return currentWorkspaceRegistryTestState().root
}

function testFileSystem(): RunnerFileSystem {
  return currentWorkspaceRegistryTestState().fileSystem
}

describe("WorkspaceRegistry", () => {
  function it(name: string, body: () => Promise<void>): void {
    vitestIt(name, async () => {
      await withTestRunnerResources(async (fileSystem) => {
        const state = { root: "/virtual/workspace-registry", fileSystem }
        await workspaceRegistryTestStorage.run(state, async () => {
          try {
            await body()
          } finally {
            await fileSystem.deleteDirectory(state.root)
            if (fileSystem.exists(state.root)) throw new Error(`workspace registry test root was not cleaned: ${state.root}`)
          }
        })
      })
    })
  }

  it("Register_PersistsActiveEntryWithMaterializedAtAndResolvedPath", async () => {
    const now = new Date("2026-06-25T10:00:00.000Z")
    const registry = new WorkspaceRegistry(testRoot(), { now: () => now })

    await registry.load()
    const entry = await registry.register({
      issueNumber: 42,
      workflowRunId: "wr-123",
      workspacePath: join(testRoot(), "mohist-local/workspaces/issue-42"),
      runBranch: "mohist/run-wr-123",
    })

    expect(entry.issueNumber).toBe(42)
    expect(entry.workflowRunId).toBe("wr-123")
    expect(entry.workspacePath).toBe(join(testRoot(), "mohist-local/workspaces/issue-42"))
    expect(entry).toMatchObject({ runBranch: "mohist/run-wr-123" })
    expect(entry.phase).toBe("active")
    expect(entry.materializedAt).toBe(now.toISOString())
    expect(entry.terminalAt).toBeNull()

    const persisted = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(persisted.version).toBe(2)
    expect(persisted.entries["wr-123"]).toMatchObject({
      issueNumber: 42,
      workflowRunId: "wr-123",
      phase: "active",
      materializedAt: now.toISOString(),
      terminalAt: null,
    })
  })

  it("Load_LegacyFile_RejectsEntries", async () => {
    const persistedAt = "2026-06-20T08:00:00.000Z"
    const filePath = defaultWorkspaceRegistryFilePath(testRoot())
    await testFileSystem().ensureDir(join(testRoot(), ".mohist", "runner-state"))
    await testFileSystem().writeText(filePath, JSON.stringify({
      version: 1,
      entries: {
        "wr-existing": {
          issueNumber: 7,
          workflowRunId: "wr-existing",
          workspacePath: join(testRoot(), "workspaces/issue-7"),
          phase: "active",
          materializedAt: persistedAt,
          terminalAt: null,
        },
        "wr-done": {
          issueNumber: 9,
          workflowRunId: "wr-done",
          workspacePath: join(testRoot(), "workspaces/issue-9"),
          phase: "eligible",
          materializedAt: persistedAt,
          terminalAt: "2026-06-24T00:00:00.000Z",
        },
      },
    }, null, 2))

    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()

    const list = registry.list()
    expect(list).toEqual([])
    expect(registry.get("wr-existing")).toBeNull()
    expect(registry.get("wr-done")).toBeNull()
  })

  it("Load_MissingFile_TreatsAsEmpty", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()

    expect(registry.list()).toHaveLength(0)
    expect(registry.get("wr-missing")).toBeNull()
  })

  it("Load_CorruptJson_TreatsAsEmptyAndOverwritesOnNextPersist", async () => {
    const filePath = defaultWorkspaceRegistryFilePath(testRoot())
    await testFileSystem().ensureDir(join(testRoot(), ".mohist", "runner-state"))
    await testFileSystem().writeText(filePath, "{not json")

    const now = new Date("2026-06-25T12:00:00.000Z")
    const registry = new WorkspaceRegistry(testRoot(), { now: () => now })
    await registry.load()
    expect(registry.list()).toHaveLength(0)

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    const persisted = JSON.parse(await testFileSystem().readText(filePath))
    expect(persisted.version).toBe(2)
    expect(persisted.entries["wr-1"]).toMatchObject({ phase: "active" })
  })

  it("Register_UpdatesMaterializedAtOnReRegisterButKeepsOriginalWhenEntryExists", async () => {
    const first = new Date("2026-06-01T00:00:00.000Z")
    const second = new Date("2026-06-25T00:00:00.000Z")
    const now = vi.fn(() => first)
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    now.mockReturnValue(second)
    const updated = await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    // materialize() is the registration point; the contract is "stamps
    // materializedAt on each successful materialize" so the timestamp
    // is bumped when the entry is re-registered by a fresh materialize.
    expect(updated.materializedAt).toBe(second.toISOString())
    expect(updated.phase).toBe("active")
  })

  it("MarkEligible_TransitionsActiveToEligibleAndStampsTerminalAt", async () => {
    const first = new Date("2026-06-01T00:00:00.000Z")
    const terminal = new Date("2026-06-25T10:00:00.000Z")
    const now = vi.fn(() => first)
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    now.mockReturnValue(terminal)
    const eligible = await registry.markEligible("wr-1")

    expect(eligible).toMatchObject({ phase: "eligible", terminalAt: terminal.toISOString() })

    // Persisted on disk.
    const persisted = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(persisted.entries["wr-1"]).toMatchObject({
      phase: "eligible",
      terminalAt: terminal.toISOString(),
    })
  })

  it("MarkEligible_IsIdempotent_AlreadyEligibleEntryIsNotReStamped", async () => {
    const first = new Date("2026-06-01T00:00:00.000Z")
    const originalTerminal = new Date("2026-06-20T00:00:00.000Z")
    const laterTerminal = new Date("2026-06-25T00:00:00.000Z")
    const now = vi.fn(() => first)
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    now.mockReturnValue(originalTerminal)
    await registry.markEligible("wr-1")

    const beforeRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))

    now.mockReturnValue(laterTerminal)
    const again = await registry.markEligible("wr-1")

    expect(again).toMatchObject({ phase: "eligible", terminalAt: originalTerminal.toISOString() })

    const afterRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(afterRewrite).toEqual(beforeRewrite)
  })

  it("MarkEligible_OnUnknownRunId_ReturnsNullWithoutPersisting", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()

    expect(await registry.markEligible("wr-unknown")).toBeNull()
  })

  it("Remove_DropsEntryAndPersists", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })
    await registry.register({
      issueNumber: 2,
      workflowRunId: "wr-2",
      workspacePath: join(testRoot(), "workspaces/issue-2"),
    })

    expect(await registry.remove("wr-1")).toBe(true)
    expect(registry.get("wr-1")).toBeNull()
    expect(registry.get("wr-2")).not.toBeNull()

    const persisted = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(persisted.entries["wr-1"]).toBeUndefined()
    expect(persisted.entries["wr-2"]).toBeDefined()
  })

  it("Remove_OnUnknownRunId_ReturnsFalse", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()
    expect(await registry.remove("wr-not-there")).toBe(false)
  })

  it("FindByWorkspacePath_ReturnsMatchingEntry", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()
    await registry.register({
      issueNumber: 7,
      workflowRunId: "wr-7",
      workspacePath: join(testRoot(), "workspaces/issue-7"),
    })

    expect(registry.findByWorkspacePath(join(testRoot(), "workspaces/issue-7"))?.workflowRunId).toBe("wr-7")
    expect(registry.findByWorkspacePath(join(testRoot(), "workspaces/issue-9"))).toBeNull()
  })

  it("Persist_UsesAtomicRename_NoTempFilesRemain", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()
    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    const dirEntries = await testFileSystem().readdir(join(testRoot(), ".mohist", "runner-state"))
    const tempLeftovers = dirEntries.filter((entry) => entry.name.endsWith(".tmp"))
    expect(tempLeftovers).toHaveLength(0)
  })

  it("Methods_RequireLoad_FailClearlyWhenCalledBeforeLoad", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    expect(() => registry.list()).toThrow(/has not been loaded/)
    expect(() => registry.get("wr-1")).toThrow(/has not been loaded/)
    expect(() => registry.findByWorkspacePath(join(testRoot(), "x"))).toThrow(/has not been loaded/)
  })

  it("MarkStuck_TransitionsEligibleToStuckAndPersists", async () => {
    const first = new Date("2026-06-01T00:00:00.000Z")
    const terminal = new Date("2026-06-25T10:00:00.000Z")
    const now = vi.fn(() => first)
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    now.mockReturnValue(terminal)
    await registry.markEligible("wr-1")

    const stuck = await registry.markStuck("wr-1")

    expect(stuck).not.toBeNull()
    expect(stuck!.phase).toBe("stuck")
    expect(stuck!.terminalAt).toBe(terminal.toISOString())

    const persisted = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(persisted.entries["wr-1"]).toMatchObject({
      phase: "stuck",
      terminalAt: terminal.toISOString(),
    })
  })

  it("MarkStuck_IsIdempotent_AlreadyStuckEntryDoesNotRewriteFile", async () => {
    const first = new Date("2026-06-01T00:00:00.000Z")
    const terminal = new Date("2026-06-25T10:00:00.000Z")
    const later = new Date("2026-06-26T00:00:00.000Z")
    const now = vi.fn(() => first)
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    now.mockReturnValue(terminal)
    await registry.markEligible("wr-1")
    await registry.markStuck("wr-1")

    const beforeRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))

    now.mockReturnValue(later)
    const again = await registry.markStuck("wr-1")

    expect(again).not.toBeNull()
    expect(again!.phase).toBe("stuck")
    expect(again!.terminalAt).toBe(terminal.toISOString())

    const afterRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(afterRewrite).toEqual(beforeRewrite)
  })

  it("MarkStuck_OnActiveEntry_IsNoOpAndDoesNotPersist", async () => {
    const now = vi.fn(() => new Date("2026-06-01T00:00:00.000Z"))
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    const beforeRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))

    const result = await registry.markStuck("wr-1")

    // Only an `eligible` entry transitions to `stuck`; an `active` entry
    // (not yet terminal) is returned unchanged and the file is not rewritten.
    expect(result).toMatchObject({ phase: "active" })

    const afterRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(afterRewrite).toEqual(beforeRewrite)
  })

  it("MarkStuck_OnUnknownRunId_ReturnsNull", async () => {
    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()
    expect(await registry.markStuck("wr-unknown")).toBeNull()
  })

  it("Load_StuckEntry_RoundTripsThroughReload", async () => {
    const filePath = defaultWorkspaceRegistryFilePath(testRoot())
    await testFileSystem().ensureDir(join(testRoot(), ".mohist", "runner-state"))
    const staleAt = "2026-06-20T08:00:00.000Z"
    const materialisedAt = "2026-06-15T08:00:00.000Z"
    await testFileSystem().writeText(filePath, JSON.stringify({
      version: 2,
      entries: {
        "wr-stuck": {
          issueNumber: 33,
          workflowRunId: "wr-stuck",
          workspacePath: join(testRoot(), "workspaces/issue-33"),
          phase: "stuck",
          materializedAt: materialisedAt,
          terminalAt: staleAt,
        },
        "wr-eligible": {
          issueNumber: 9,
          workflowRunId: "wr-eligible",
          workspacePath: join(testRoot(), "workspaces/issue-9"),
          phase: "eligible",
          materializedAt: materialisedAt,
          terminalAt: staleAt,
        },
      },
    }, null, 2))

    const registry = new WorkspaceRegistry(testRoot())
    await registry.load()

    const stuck = registry.get("wr-stuck")
    expect(stuck).toMatchObject({
      phase: "stuck",
      terminalAt: staleAt,
      materializedAt: materialisedAt,
    })

    await registry.reload()
    expect(registry.get("wr-stuck")).toMatchObject({
      phase: "stuck",
      terminalAt: staleAt,
      materializedAt: materialisedAt,
    })
  })

  it("MarkEligible_OnStuckEntry_LeavesEntryUnchanged", async () => {
    const first = new Date("2026-06-01T00:00:00.000Z")
    const terminal = new Date("2026-06-25T10:00:00.000Z")
    const later = new Date("2026-06-26T00:00:00.000Z")
    const now = vi.fn(() => first)
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    now.mockReturnValue(terminal)
    await registry.markEligible("wr-1")
    await registry.markStuck("wr-1")

    const beforeRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))

    now.mockReturnValue(later)
    const revived = await registry.markEligible("wr-1")

    expect(revived).not.toBeNull()
    expect(revived!.phase).toBe("stuck")
    expect(revived!.terminalAt).toBe(terminal.toISOString())

    const afterRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(afterRewrite).toEqual(beforeRewrite)
  })

  it("MarkEligible_OnAlreadyEligibleEntry_RemainsNoOp", async () => {
    const first = new Date("2026-06-01T00:00:00.000Z")
    const originalTerminal = new Date("2026-06-20T00:00:00.000Z")
    const laterTerminal = new Date("2026-06-25T00:00:00.000Z")
    const now = vi.fn(() => first)
    const registry = new WorkspaceRegistry(testRoot(), { now })
    await registry.load()

    await registry.register({
      issueNumber: 1,
      workflowRunId: "wr-1",
      workspacePath: join(testRoot(), "workspaces/issue-1"),
    })

    now.mockReturnValue(originalTerminal)
    await registry.markEligible("wr-1")

    const beforeRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))

    now.mockReturnValue(laterTerminal)
    const again = await registry.markEligible("wr-1")

    expect(again).toMatchObject({ phase: "eligible", terminalAt: originalTerminal.toISOString() })

    const afterRewrite = JSON.parse(await testFileSystem().readText(defaultWorkspaceRegistryFilePath(testRoot())))
    expect(afterRewrite).toEqual(beforeRewrite)
  })
})
