import { mkdir, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { describe, expect, it } from "vitest"
import { AgentWorkspaceRegistry, type AgentWorkspaceRegisterInput } from "../src/runtime/agent-workspace-registry.js"
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
})
