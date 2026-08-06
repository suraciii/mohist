import { mkdir, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { describe, expect, it } from "vitest"
import {
  AgentWorkspaceRegistry,
  DEFAULT_ORPHAN_GRACE_RECHECKS,
  type AgentWorkspaceActivityState,
  type AgentWorkspaceRegisterInput,
} from "../src/runtime/agent-workspace-registry.js"
import { createTestTempDir } from "./support/temp-dir.js"

const NOW = new Date("2026-01-01T00:00:00.000Z")

function input(overrides: Partial<AgentWorkspaceRegisterInput> = {}): AgentWorkspaceRegisterInput {
  return {
    childSessionId: "00000000000000000000000000000001",
    projectId: "project-1",
    workspaceIdentity: "agent-wt:00000000000000000000000000000001",
    workspacePath: "/runner/agent-workspaces/00000000000000000000000000000001",
    branch: "mohist/wt-00000000000000000000000000000001",
    parentWorkDir: "/runner/workspaces/wr-1",
    repositoryName: "main",
    ...overrides,
  }
}

describe("AgentWorkspaceRegistry", () => {
  it("Register_WritesThrough_AndReloadRebuildsFromDisk", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    const record = input()

    await registry.register(record)

    const reloaded = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await reloaded.load()
    expect(reloaded.get(record.childSessionId)).toEqual({
      ...record,
      workspacePath: record.workspacePath,
      parentWorkDir: record.parentWorkDir,
      phase: "active",
      materializedAt: NOW.toISOString(),
      terminalAt: null,
    })
  })

  it("Register_PreservesExistingPhaseAndTerminalAt", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())
    await registry.markEligible(input().childSessionId)

    const reRegistered = await registry.register({ ...input(), repositoryName: "renamed" })

    expect(reRegistered.phase).toBe("eligible")
    expect(reRegistered.terminalAt).toBe(NOW.toISOString())
    expect(reRegistered.repositoryName).toBe("renamed")
  })

  it("Register_RejectsAPathOwnedByAnotherKey", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())

    await expect(registry.register(input({ childSessionId: "00000000000000000000000000000002" }))).rejects.toThrow(/already owned/)
  })

  it("MarkEligible_TransitionsAnyPhase_AndStampsTerminalAt", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())
    await registry.markStuck(input().childSessionId)

    // Release is Server-authoritative: even a stuck entry revives to eligible.
    const marked = await registry.markEligible(input().childSessionId)

    expect(marked?.phase).toBe("eligible")
    expect(registry.get(input().childSessionId)?.terminalAt).toBe(NOW.toISOString())
  })

  it("MarkStuck_OnlyTransitionsEligible", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())

    const stuck = await registry.markStuck(input().childSessionId)

    expect(stuck?.phase).toBe("active")
    expect(registry.get(input().childSessionId)?.phase).toBe("active")
  })

  it("Remove_ReturnsTrueOnlyWhenTheEntryExists", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()

    expect(await registry.remove(input().childSessionId)).toBe(false)

    await registry.register(input())
    expect(await registry.remove(input().childSessionId)).toBe(true)
    expect(registry.list()).toHaveLength(0)
    expect(registry.findByWorkspacePath(input().workspacePath)).toBeNull()
  })

  it("FindByWorkspacePath_ResolvesTheOwningEntry", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())

    expect(registry.findByWorkspacePath(input().workspacePath)?.childSessionId).toBe(input().childSessionId)
  })

  it("MissingFile_LoadsEmpty", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })

    await registry.load()

    expect(registry.list()).toHaveLength(0)
  })

  it("CorruptFile_LoadsEmpty_FailOpen", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    await mkdir(join(root, ".mohist", "runner-state"), { recursive: true })
    await writeFile(join(root, ".mohist", "runner-state", "agent-workspaces.json"), "{ not json")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })

    await registry.load()

    expect(registry.list()).toHaveLength(0)
  })

  it("VersionMismatch_LoadsEmpty", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    await mkdir(join(root, ".mohist", "runner-state"), { recursive: true })
    await writeFile(join(root, ".mohist", "runner-state", "agent-workspaces.json"), JSON.stringify({
      version: 99,
      entries: {},
    }))
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })

    await registry.load()

    expect(registry.list()).toHaveLength(0)
  })

  it("Load_SkipsEntriesWithInvalidShapes", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    await mkdir(join(root, ".mohist", "runner-state"), { recursive: true })
    await writeFile(join(root, ".mohist", "runner-state", "agent-workspaces.json"), JSON.stringify({
      version: 1,
      entries: {
        "00000000000000000000000000000001": {
          childSessionId: "00000000000000000000000000000001",
          workspaceIdentity: "agent-wt:00000000000000000000000000000001",
          workspacePath: "/runner/agent-workspaces/00000000000000000000000000000001",
          branch: "mohist/wt-00000000000000000000000000000001",
          parentWorkDir: "/runner/workspaces/wr-1",
          phase: "bogus-phase",
          materializedAt: NOW.toISOString(),
        },
        "00000000000000000000000000000002": {
          childSessionId: "00000000000000000000000000000002",
          workspaceIdentity: "agent-wt:00000000000000000000000000000002",
          workspacePath: "/runner/agent-workspaces/00000000000000000000000000000002",
          branch: "mohist/wt-00000000000000000000000000000002",
          parentWorkDir: "/runner/workspaces/wr-2",
          phase: "eligible",
          materializedAt: NOW.toISOString(),
        },
      },
    }))
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })

    await registry.load()

    const ids = registry.list().map((entry) => entry.childSessionId)
    expect(ids).toEqual(["00000000000000000000000000000002"])
  })

  it("RecordActivity_FirstNotFound_RecordsCandidate_StaysActive", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())

    const observed = await registry.recordActivity(input().childSessionId, "not-found")

    expect(observed?.phase).toBe("active")
    expect(registry.get(input().childSessionId)?.phase).toBe("active")
    expect(registry.get(input().childSessionId)?.terminalAt).toBeNull()
    expect(registry.orphanCandidate(input().childSessionId)).toBe(1)
  })

  it("RecordActivity_ConsecutiveNotFound_ReachesDefaultThreshold_BecomesEligible", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())
    expect(DEFAULT_ORPHAN_GRACE_RECHECKS).toBe(2)

    await registry.recordActivity(input().childSessionId, "not-found")
    const confirmed = await registry.recordActivity(input().childSessionId, "not-found")

    expect(confirmed?.phase).toBe("eligible")
    expect(confirmed?.terminalAt).toBe(NOW.toISOString())
    expect(registry.orphanCandidate(input().childSessionId)).toBe(0)
  })

  it("RecordActivity_NonNotFound_Observations_CancelCandidate", async () => {
    for (const state of ["active", "idle", "pending", "unknown"] as AgentWorkspaceActivityState[]) {
      const root = await createTestTempDir("mohist-agent-registry-")
      const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
      await registry.load()
      await registry.register(input())
      await registry.recordActivity(input().childSessionId, "not-found")
      expect(registry.orphanCandidate(input().childSessionId)).toBe(1)

      const observed = await registry.recordActivity(input().childSessionId, state)

      expect(observed?.phase).toBe("active")
      expect(registry.orphanCandidate(input().childSessionId)).toBe(0)
    }
  })

  it("RecordActivity_CancelThenReobserve_RequiresFreshFullThreshold", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())

    await registry.recordActivity(input().childSessionId, "not-found")
    await registry.recordActivity(input().childSessionId, "active")
    // A single fresh not-found after a cancel must NOT be enough.
    const afterOne = await registry.recordActivity(input().childSessionId, "not-found")

    expect(afterOne?.phase).toBe("active")
    expect(registry.orphanCandidate(input().childSessionId)).toBe(1)

    const afterTwo = await registry.recordActivity(input().childSessionId, "not-found")
    expect(afterTwo?.phase).toBe("eligible")
  })

  it("RecordActivity_TerminalPhasesAreSticky_NotRevertedByActivity", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())

    await registry.markEligible(input().childSessionId)
    await registry.markStuck(input().childSessionId)
    expect(registry.get(input().childSessionId)?.phase).toBe("stuck")

    // Neither a confirming nor an orphaning observation may un-stick an
    // entry: the removal-fence refusal must not be weakened.
    const afterNotFound = await registry.recordActivity(input().childSessionId, "not-found")
    const afterActive = await registry.recordActivity(input().childSessionId, "active")

    expect(afterNotFound?.phase).toBe("stuck")
    expect(afterActive?.phase).toBe("stuck")
    expect(registry.orphanCandidate(input().childSessionId)).toBe(0)
  })

  it("RecordActivity_ExplicitReleaseBypassesGrace_AndStaysEligible", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())
    await registry.recordActivity(input().childSessionId, "not-found")
    expect(registry.orphanCandidate(input().childSessionId)).toBe(1)

    // Explicit release is Server-authoritative and skips grace.
    const released = await registry.markEligible(input().childSessionId)

    expect(released?.phase).toBe("eligible")
    expect(registry.orphanCandidate(input().childSessionId)).toBe(0)

    // A later not-found must not revert an explicitly-released entry.
    const afterNotFound = await registry.recordActivity(input().childSessionId, "not-found")
    expect(afterNotFound?.phase).toBe("eligible")
  })

  it("RecordActivity_UnknownEntry_ReturnsNull", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()

    expect(await registry.recordActivity("missing", "not-found")).toBeNull()
    expect(registry.orphanCandidate("missing")).toBe(0)
  })

  it("OrphanCandidate_IsInMemory_AndResetsOnReload", async () => {
    const root = await createTestTempDir("mohist-agent-registry-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
    await registry.load()
    await registry.register(input())
    await registry.recordActivity(input().childSessionId, "not-found")
    expect(registry.orphanCandidate(input().childSessionId)).toBe(1)

    await registry.reload()

    // The phase is the durable fact; the candidate counter is not
    // persisted, so reload resets the grace window (fail-safe).
    expect(registry.get(input().childSessionId)?.phase).toBe("active")
    expect(registry.orphanCandidate(input().childSessionId)).toBe(0)
  })

  it("OrphanGraceRechecks_ConfigurableThreshold", async () => {
    const disabled = new AgentWorkspaceRegistry(await createTestTempDir("mohist-agent-registry-"), {
      now: () => NOW,
      orphanGraceRechecks: 1,
    })
    await disabled.load()
    await disabled.register(input())
    const immediate = await disabled.recordActivity(input().childSessionId, "not-found")
    expect(immediate?.phase).toBe("eligible")

    const strict = new AgentWorkspaceRegistry(await createTestTempDir("mohist-agent-registry-"), {
      now: () => NOW,
      orphanGraceRechecks: 3,
    })
    await strict.load()
    await strict.register(input())
    await strict.recordActivity(input().childSessionId, "not-found")
    expect(strict.get(input().childSessionId)?.phase).toBe("active")
    await strict.recordActivity(input().childSessionId, "not-found")
    expect(strict.get(input().childSessionId)?.phase).toBe("active")
    const confirmed = await strict.recordActivity(input().childSessionId, "not-found")
    expect(confirmed?.phase).toBe("eligible")
  })
})
