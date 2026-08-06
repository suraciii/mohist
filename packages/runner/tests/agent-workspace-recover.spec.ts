import { mkdir, symlink, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { agentWorkspacePath, AgentWorkspaceManager } from "../src/runtime/agent-workspace.js"
import { AgentWorkspaceRegistry } from "../src/runtime/agent-workspace-registry.js"
import { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import * as processModule from "../src/system/process.js"
import {
  createAgentManager,
  createRunnerOwnedParent,
  expectMaterialized,
  FakeAgentGit,
  validChildSessionId,
} from "./support/agent-workspace-fixture.js"
import { createTestTempDir } from "./support/temp-dir.js"

const GIT_URL = "https://example.test/mohist.git"

let fake: FakeAgentGit
let restoreRunCommand: (() => void) | undefined

beforeEach(() => {
  fake = new FakeAgentGit()
  const spy = vi.spyOn(processModule, "runCommand").mockImplementation((command, args, cwd, signal, env, options) => {
    return fake.run(command, args, cwd, signal, env, options)
  })
  restoreRunCommand = () => spy.mockRestore()
})

afterEach(() => {
  restoreRunCommand?.()
  restoreRunCommand = undefined
})

async function setupMaterialized(root: string, manager: AgentWorkspaceManager, parentPath: string, id: string) {
  const result = await manager.materialize({
    projectId: "project-1",
    childSessionId: id,
    parentWorkDir: parentPath,
    repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" },
  }, new AbortController().signal)
  return expectMaterialized(result)
}

describe("AgentWorkspaceManager.recover", () => {
  it("RegistryLoss_RescansSafeIdDirectories_AndReRegistersFromGitMetadata", async () => {
    const root = await createTestTempDir("mohist-agent-recover-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const idA = validChildSessionId(1)
    const idB = validChildSessionId(2)
    const first = await setupMaterialized(root, manager, parentPath, idA)
    await setupMaterialized(root, manager, parentPath, idB)
    await processModule.deleteFile(registry.getFilePath())

    const rebuiltRegistry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await rebuiltRegistry.load()
    const rebuiltManager = createAgentManager(root, rebuiltRegistry, fake, { workflowRegistry })
    fake.calls.length = 0

    const result = await rebuiltManager.recover(new AbortController().signal)

    expect(result).toEqual({ scanned: 2, adopted: 2, skipped: 0 })
    expect(rebuiltRegistry.get(idA)).toMatchObject({
      childSessionId: idA,
      workspacePath: first.workDir,
      parentWorkDir: parentPath,
      branch: `mohist/wt-${idA}`,
      phase: "active",
    })
    expect(rebuiltRegistry.get(idB)).toMatchObject({ childSessionId: idB, phase: "active" })
    expect(fake.commandArgs().filter((args) => args.includes("worktree") && args.includes("add"))).toHaveLength(0)
  })

  it("RegistryLoss_AdoptsNestedWorktrees_WithBackingDerivedParent", async () => {
    const root = await createTestTempDir("mohist-agent-recover-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const parentId = validChildSessionId(1)
    const childId = validChildSessionId(2)
    await setupMaterialized(root, manager, parentPath, parentId)
    // A linked worktree shares the parent repo's origin.
    fake.origins.set(agentWorkspacePath(root, parentId), GIT_URL)
    await setupMaterialized(root, manager, agentWorkspacePath(root, parentId), childId)
    await processModule.deleteFile(registry.getFilePath())

    const rebuiltRegistry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await rebuiltRegistry.load()
    const rebuiltManager = createAgentManager(root, rebuiltRegistry, fake, { workflowRegistry })
    const result = await rebuiltManager.recover(new AbortController().signal)

    expect(result).toEqual({ scanned: 2, adopted: 2, skipped: 0 })
    // The backing `gitdir` entry always points at the main workspace
    // whose object store the worktree shares.
    expect(rebuiltRegistry.get(childId)).toMatchObject({
      parentWorkDir: parentPath,
      projectId: null,
      repositoryName: null,
    })
  })

  it("SkipsInvalidDirectories_NonSafeIds_Symlinks_BrokenBacking_WrongBranch", async () => {
    const root = await createTestTempDir("mohist-agent-recover-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const validId = validChildSessionId(1)
    await setupMaterialized(root, manager, parentPath, validId)

    // Invalid candidates under <root>/agent-workspaces/:
    const agentRoot = join(root, "agent-workspaces")
    await mkdir(join(agentRoot, "not-a-safe-id"), { recursive: true })
    await mkdir(join(agentRoot, "plain-dir-no-git"), { recursive: true })
    const wrongBranchId = validChildSessionId(3)
    await mkdir(join(agentRoot, wrongBranchId), { recursive: true })
    await writeFile(join(agentRoot, wrongBranchId, ".git"), `gitdir: ${join(parentPath, ".git", "worktrees", wrongBranchId)}\n`)
    fake.branches.set(join(agentRoot, wrongBranchId), "master")
    const brokenBackingId = validChildSessionId(4)
    await mkdir(join(agentRoot, brokenBackingId), { recursive: true })
    await writeFile(join(agentRoot, brokenBackingId, ".git"), `gitdir: ${join(parentPath, ".git", "worktrees", validChildSessionId(9))}\n`)
    fake.branches.set(join(agentRoot, brokenBackingId), `mohist/wt-${brokenBackingId}`)
    const symlinkedId = validChildSessionId(5)
    const realTarget = join(root, "real-target")
    await mkdir(realTarget, { recursive: true })
    await symlink(realTarget, join(agentRoot, symlinkedId))

    const result = await manager.recover(new AbortController().signal)

    expect(result).toEqual({ scanned: 3, adopted: 1, skipped: 2 })
    expect(registry.list().map((entry) => entry.childSessionId)).toEqual([validId])
  })

  it("PreservesExistingRegistryPhases", async () => {
    const root = await createTestTempDir("mohist-agent-recover-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const id = validChildSessionId(1)
    await setupMaterialized(root, manager, parentPath, id)
    await registry.markEligible(id)

    const recovered = await manager.recover(new AbortController().signal)

    expect(recovered.adopted).toBe(1)
    expect(registry.get(id)).toMatchObject({ phase: "eligible" })
    expect(registry.get(id)?.terminalAt).toBe("2026-01-01T00:00:00.000Z")
  })

  it("MissingAgentWorkspacesDirectory_IsANoOp", async () => {
    const root = await createTestTempDir("mohist-agent-recover-")
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake)

    const result = await manager.recover(new AbortController().signal)

    expect(result).toEqual({ scanned: 0, adopted: 0, skipped: 0 })
    expect(fake.commandArgs()).toEqual([])
  })

  it("NeverCreatesOrMutatesDirectories", async () => {
    const root = await createTestTempDir("mohist-agent-recover-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const id = validChildSessionId(1)
    const first = await setupMaterialized(root, manager, parentPath, id)
    await writeFile(join(first.workDir, "draft.txt"), "uncommitted\n")
    await processModule.deleteFile(registry.getFilePath())
    fake.calls.length = 0
    const rebuiltRegistry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await rebuiltRegistry.load()
    const rebuiltManager = createAgentManager(root, rebuiltRegistry, fake, { workflowRegistry })

    await rebuiltManager.recover(new AbortController().signal)

    expect(await processModule.readText(join(first.workDir, "draft.txt"))).toBe("uncommitted\n")
    expect(fake.commandArgs().filter((args) => args.includes("worktree") && args.includes("add"))).toHaveLength(0)
  })
})
