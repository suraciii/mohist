import { mkdir, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  agentWorktreeBranch,
  agentWorktreeName,
  agentWorkspaceIdentity,
  agentWorkspacePath,
  CHILD_SESSION_ID_PATTERN,
  AgentWorkspaceManager,
} from "../src/runtime/agent-workspace.js"
import { AgentWorkspaceRegistry } from "../src/runtime/agent-workspace-registry.js"
import { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import * as processModule from "../src/system/process.js"
import {
  createAgentManager,
  createRunnerOwnedParent,
  createSymlinkedDir,
  expectMaterialized,
  FakeAgentGit,
  validChildSessionId,
  type CommandCall,
} from "./support/agent-workspace-fixture.js"
import { createTestTempDir } from "./support/temp-dir.js"

const ID = validChildSessionId(1)
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

describe("deterministic derivation", () => {
  it("derives path, branch, worktree name and identity from the child session id", () => {
    const root = "/runner"
    expect(agentWorkspacePath(root, ID)).toBe(`${root}/agent-workspaces/${ID}`)
    expect(agentWorktreeBranch(ID)).toBe(`mohist/wt-${ID}`)
    expect(agentWorktreeName(ID)).toBe(ID)
    expect(agentWorkspaceIdentity(ID)).toBe(`agent-wt:${ID}`)
    expect(CHILD_SESSION_ID_PATTERN.test(ID)).toBe(true)
  })

  it.each(["", "not-hex", "ABCDEF", "0123456789abcdef0123456789abcdef0", "0123456789abcdef0123456789abcdeZ"])(
    "rejects invalid child session id %j",
    (candidate) => {
      expect(() => agentWorkspacePath("/runner", candidate)).toThrow(/Invalid child session id/)
      expect(() => agentWorktreeBranch(candidate)).toThrow(/Invalid child session id/)
      expect(() => agentWorkspaceIdentity(candidate)).toThrow(/Invalid child session id/)
    },
  )
})

describe("MaterializeAgentWorkspace", () => {
  let root: string
  let registry: AgentWorkspaceRegistry
  let workflowRegistry: WorkspaceRegistry
  let manager: AgentWorkspaceManager
  let parentPath: string

  beforeEach(async () => {
    root = await createTestTempDir("mohist-agent-ws-")
    workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    manager = createAgentManager(root, registry, fake, { workflowRegistry })
    parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
  })

  function request(overrides: Partial<{ childSessionId: string; parentWorkDir: string; gitUrl: string }> = {}) {
    return {
      projectId: "project-1",
      childSessionId: overrides.childSessionId ?? ID,
      parentWorkDir: overrides.parentWorkDir ?? parentPath,
      repository: { name: "main", gitUrl: overrides.gitUrl ?? GIT_URL, baseBranch: "master" },
    }
  }

  it("FreshMaterialize_CreatesWorktreeWithDeterministicCoordinates", async () => {
    const result = await manager.materialize(request(), new AbortController().signal)

    const expectedPath = agentWorkspacePath(root, ID)
    expect(result).toEqual({
      kind: "materialized",
      workspaceIdentity: `agent-wt:${ID}`,
      workDir: expectedPath,
    })
    expect(fake.commandArgs()).toContainEqual(["-C", parentPath, "rev-parse", "HEAD"])
    expect(fake.commandArgs()).toContainEqual(["-C", parentPath, "worktree", "add", "-B", `mohist/wt-${ID}`, expectedPath, "fake-head-sha"])
    expect(registry.get(ID)).toMatchObject({
      childSessionId: ID,
      projectId: "project-1",
      workspaceIdentity: `agent-wt:${ID}`,
      workspacePath: expectedPath,
      branch: `mohist/wt-${ID}`,
      parentWorkDir: parentPath,
      repositoryName: "main",
      phase: "active",
    })
    expect(await processModule.readText(join(expectedPath, ".git"))).toContain(`.git/worktrees/${ID}`)
  })

  it("SameKeyReplay_ReturnsSameIdentityAndPath_WithoutRecreating", async () => {
    const first = await manager.materialize(request(), new AbortController().signal)
    fake.calls.length = 0

    const second = await manager.materialize(request({ parentWorkDir: join(root, "workspaces", "somewhere-else") }), new AbortController().signal)

    expect(second).toEqual(first)
    expect(fake.commandArgs()).toEqual([])
  })

  it("ConcurrentSameKey_SingleFlight_NoDoubleCreation", async () => {
    const [first, second] = await Promise.all([
      manager.materialize(request(), new AbortController().signal),
      manager.materialize(request(), new AbortController().signal),
    ])

    expect(first).toEqual(second)
    expect(fake.commandArgs().filter((args) => args.includes("worktree") && args.includes("add"))).toHaveLength(1)
  })

  it("RegisteredEntry_ReplaysEvenWhenWorktreeDirectoryIsGone", async () => {
    const first = expectMaterialized(await manager.materialize(request(), new AbortController().signal))
    await processModule.deleteDirectory(first.workDir)
    fake.calls.length = 0

    const replay = await manager.materialize(request(), new AbortController().signal)

    expect(replay).toEqual(first)
    expect(fake.commandArgs()).toEqual([])
  })

  it("InvalidChildSessionId_IsRejectedWithoutTouchingAnything", async () => {
    const result = await manager.materialize(request({ childSessionId: "not-a-valid-id" }), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "invalid" })
    expect(fake.commandArgs()).toEqual([])
    expect(registry.list()).toHaveLength(0)
  })

  it("MissingRepositorySnapshot_IsRejectedInvalid", async () => {
    const result = await manager.materialize({ ...request(), repository: null }, new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "invalid" })
    expect(fake.commandArgs()).toEqual([])
  })

  it("ParentOutsideRunnerRoot_IsRejectedParentUnavailable", async () => {
    const outside = join(root, "..", "outside")
    await mkdir(outside, { recursive: true })

    const result = await manager.materialize(request({ parentWorkDir: outside }), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "parent-workspace-unavailable" })
    expect(fake.commandArgs()).toEqual([])
    expect(registry.list()).toHaveLength(0)
  })

  it("SymlinkedParent_IsRejectedParentUnavailable", async () => {
    const realParent = join(root, "real-parent")
    const linkedParent = join(root, "workspaces", "wr-symlinked")
    await createSymlinkedDir(realParent, linkedParent)

    const result = await manager.materialize(request({ parentWorkDir: linkedParent }), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "parent-workspace-unavailable" })
    expect(fake.commandArgs()).toEqual([])
  })

  it("UnregisteredParentDirectory_IsRejectedParentUnavailable", async () => {
    const rogue = join(root, "workspaces", "wr-rogue")
    await mkdir(join(rogue, ".git"), { recursive: true })

    const result = await manager.materialize(request({ parentWorkDir: rogue }), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "parent-workspace-unavailable" })
    expect(fake.commandArgs()).toEqual([])
  })

  it("ParentWithoutReadableGit_IsRejectedParentUnavailable", async () => {
    const noGit = join(root, "workspaces", "wr-no-git")
    await mkdir(noGit, { recursive: true })
    await workflowRegistry.register({ issueNumber: 1, workflowRunId: "wr-no-git", workspacePath: noGit })

    const result = await manager.materialize(request({ parentWorkDir: noGit }), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "parent-workspace-unavailable" })
    expect(fake.commandArgs()).toEqual([])
  })

  it("ParentOriginMismatch_IsRejectedRepositoryMismatch", async () => {
    fake.origins.set(parentPath, "https://example.test/other.git")

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "repository-mismatch" })
    expect(fake.commandArgs()).not.toContainEqual(expect.arrayContaining(["worktree", "add"]))
    expect(registry.list()).toHaveLength(0)
  })

  it("UnreadableParentOrigin_IsRejectedRepositoryMismatch", async () => {
    fake.origins.delete(parentPath)

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "repository-mismatch" })
  })

  it("StorageBudgetExhausted_IsRejectedCapacity", async () => {
    manager = createAgentManager(root, registry, fake, { workflowRegistry, budgetBytes: 500 })
    fake.sizes.set(root, 1000)

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "capacity" })
    expect(fake.commandArgs()).not.toContainEqual(expect.arrayContaining(["worktree", "add"]))
    expect(registry.list()).toHaveLength(0)
  })

  it("UnknownUsageWithBudgetEnabled_IsRejectedCapacityFailClosed", async () => {
    manager = createAgentManager(root, registry, fake, { workflowRegistry, budgetBytes: 500 })
    fake.sizes.clear()

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "capacity" })
  })

  it("BudgetDisabled_DoesNotGateMaterialization", async () => {
    manager = createAgentManager(root, registry, fake, { workflowRegistry, budgetBytes: null })
    fake.sizes.set(root, 10_000_000)

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "materialized" })
  })

  it("WorktreeAddFailure_IsRejectedPermission", async () => {
    fake.failNextWorktreeAdd = true

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "permission" })
    expect(registry.list()).toHaveLength(0)
  })

  it("UnreadableParentHead_IsRejectedParentUnavailable", async () => {
    fake.heads.set(parentPath, "")
    const revParse = fake.run.bind(fake)
    vi.spyOn(processModule, "runCommand").mockImplementation(async (command, args, cwd, signal, env, options) => {
      if (command === "git" && args.includes("rev-parse") && args.at(-1) === "HEAD") {
        return { exitCode: 1, stdout: "", stderr: "fatal: bad revision" }
      }
      return revParse(command, args, cwd, signal, env, options)
    })

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "parent-workspace-unavailable" })
  })

  it("Adoption_ReRegistersAnExistingWorktreeAfterRegistryLoss", async () => {
    const first = expectMaterialized(await manager.materialize(request(), new AbortController().signal))
    await processModule.deleteFile(registry.getFilePath())
    const rebuiltRegistry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await rebuiltRegistry.load()
    const rebuiltManager = createAgentManager(root, rebuiltRegistry, fake, { workflowRegistry })
    fake.calls.length = 0

    const adopted = await rebuiltManager.materialize(request(), new AbortController().signal)

    expect(adopted).toEqual(first)
    expect(fake.commandArgs()).not.toContainEqual(expect.arrayContaining(["worktree", "add"]))
    expect(rebuiltRegistry.get(ID)).toMatchObject({
      childSessionId: ID,
      workspacePath: first.workDir,
      parentWorkDir: parentPath,
      phase: "active",
    })
  })

  it("AdoptionWithWrongBacking_IsRejectedInvalid_LeavesDirectoryUntouched", async () => {
    const worktreePath = agentWorkspacePath(root, ID)
    const otherId = validChildSessionId(2)
    await mkdir(worktreePath, { recursive: true })
    await writeFile(join(worktreePath, ".git"), `gitdir: ${join(parentPath, ".git", "worktrees", otherId)}\n`)
    await writeFile(join(worktreePath, "sentinel.txt"), "keep me\n")

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "invalid" })
    expect(await processModule.readText(join(worktreePath, "sentinel.txt"))).toBe("keep me\n")
    expect(registry.list()).toHaveLength(0)
  })

  it("AdoptionWithWrongBranch_IsRejectedInvalid", async () => {
    const worktreePath = agentWorkspacePath(root, ID)
    await mkdir(worktreePath, { recursive: true })
    await writeFile(join(worktreePath, ".git"), `gitdir: ${join(parentPath, ".git", "worktrees", ID)}\n`)
    fake.branches.set(worktreePath, "master")

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "invalid" })
  })

  it("AdoptionWithUnregisteredBackingParent_IsRejectedInvalid", async () => {
    const worktreePath = agentWorkspacePath(root, ID)
    const rogueParent = join(root, "workspaces", "wr-rogue")
    await mkdir(rogueParent, { recursive: true })
    await mkdir(worktreePath, { recursive: true })
    await writeFile(join(worktreePath, ".git"), `gitdir: ${join(rogueParent, ".git", "worktrees", ID)}\n`)
    fake.branches.set(worktreePath, `mohist/wt-${ID}`)

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "invalid" })
  })

  it("SymlinkedWorktreePath_IsRejectedInvalid", async () => {
    const worktreePath = agentWorkspacePath(root, ID)
    const realTarget = join(root, "real-worktree")
    await createSymlinkedDir(realTarget, worktreePath)
    fake.branches.set(realTarget, `mohist/wt-${ID}`)

    const result = await manager.materialize(request(), new AbortController().signal)

    expect(result).toMatchObject({ kind: "rejected", reason: "invalid" })
  })
})

describe("ReleaseAgentWorkspace", () => {
  let root: string
  let registry: AgentWorkspaceRegistry
  let workflowRegistry: WorkspaceRegistry
  let manager: AgentWorkspaceManager
  let parentPath: string

  beforeEach(async () => {
    root = await createTestTempDir("mohist-agent-release-")
    workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    manager = createAgentManager(root, registry, fake, { workflowRegistry })
    parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
  })

  it("Release_MarksTheEntryEligible", async () => {
    await manager.materialize({ projectId: "p", childSessionId: ID, parentWorkDir: parentPath, repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" } }, new AbortController().signal)

    const result = await manager.release({ childSessionId: ID, workspaceIdentity: `agent-wt:${ID}` })

    expect(result).toEqual({ kind: "released" })
    expect(registry.get(ID)).toMatchObject({ phase: "eligible" })
    expect(registry.get(ID)?.terminalAt).toBe("2026-01-01T00:00:00.000Z")
  })

  it("ReleaseReplay_IsIdempotent", async () => {
    await manager.materialize({ projectId: "p", childSessionId: ID, parentWorkDir: parentPath, repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" } }, new AbortController().signal)
    await manager.release({ childSessionId: ID, workspaceIdentity: `agent-wt:${ID}` })

    const replay = await manager.release({ childSessionId: ID, workspaceIdentity: `agent-wt:${ID}` })

    expect(replay).toEqual({ kind: "released" })
  })

  it("IdentityMismatch_IsRejectedInvalid_WithoutChangingPhase", async () => {
    await manager.materialize({ projectId: "p", childSessionId: ID, parentWorkDir: parentPath, repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" } }, new AbortController().signal)

    const result = await manager.release({ childSessionId: ID, workspaceIdentity: `agent-wt:${validChildSessionId(9)}` })

    expect(result).toMatchObject({ kind: "invalid" })
    expect(registry.get(ID)).toMatchObject({ phase: "active" })
  })

  it("UnknownKey_IsNotFound", async () => {
    const result = await manager.release({ childSessionId: validChildSessionId(3), workspaceIdentity: `agent-wt:${validChildSessionId(3)}` })

    expect(result).toEqual({ kind: "not-found" })
  })

  it("InvalidChildSessionId_IsRejectedInvalid", async () => {
    const result = await manager.release({ childSessionId: "nope", workspaceIdentity: "agent-wt:nope" })

    expect(result).toMatchObject({ kind: "invalid" })
  })
})

describe("runner-owned workspace resolution", () => {
  it("recognizes workflow workspaces, agent worktrees and configured defaults", async () => {
    const root = await createTestTempDir("mohist-owned-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, {
      workflowRegistry,
      defaultWorkspacePaths: [join(root, "default-ws")],
    })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const defaultPath = join(root, "default-ws")

    expect(await manager.isRunnerOwnedWorkspace(parentPath)).toBe(true)
    expect(await manager.isRunnerOwnedWorkspace(defaultPath)).toBe(true)
    expect(await manager.isRunnerOwnedWorkspace(join(root, "unregistered"))).toBe(false)

    await manager.materialize({ projectId: "p", childSessionId: ID, parentWorkDir: parentPath, repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" } }, new AbortController().signal)
    expect(await manager.isRunnerOwnedWorkspace(agentWorkspacePath(root, ID))).toBe(true)
  })
})
