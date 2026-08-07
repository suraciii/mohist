import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { agentWorkspacePath, AgentWorkspaceManager } from "../src/runtime/agent-workspace.js"
import { AgentWorkspaceRegistry } from "../src/runtime/agent-workspace-registry.js"
import { AgentCleanupRunner } from "../src/runtime/agent-workspace-cleanup.js"
import { CleanupLoop, DefaultCleanupRunner } from "../src/runtime/cleanup-loop.js"
import { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from "../src/runtime/workspace-removal-fence.js"
import * as processModule from "../src/system/process.js"
import {
  createAgentManager,
  createRunnerOwnedParent,
  FakeAgentGit,
  validChildSessionId,
  writeWorkflowMarker,
} from "./support/agent-workspace-fixture.js"
import { createTestTempDir } from "./support/temp-dir.js"

const GIT_URL = "https://example.test/mohist.git"
const NOW = new Date("2026-01-01T00:00:00.000Z")

let fake: FakeAgentGit
let restoreRunCommand: (() => void) | undefined

beforeEach(() => {
  fake = new FakeAgentGit()
  const spy = vi.spyOn(processModule, "runCommand").mockImplementation((command, args, cwd, signal, env, options) => {
    return fake.run(command, args, cwd, signal, env, options)
  })
  restoreRunCommand = () => spy.mockRestore()
  vi.useFakeTimers({ toFake: ["Date"] })
  vi.setSystemTime(NOW)
})

afterEach(() => {
  restoreRunCommand?.()
  restoreRunCommand = undefined
  vi.useRealTimers()
})

interface Fixture {
  root: string
  workflowRegistry: WorkspaceRegistry
  agentRegistry: AgentWorkspaceRegistry
  manager: AgentWorkspaceManager
  parentPath: string
}

async function createFixture(): Promise<Fixture> {
  const root = await createTestTempDir("mohist-agent-cleanup-")
  const workflowRegistry = new WorkspaceRegistry(root, { now: () => NOW })
  await workflowRegistry.load()
  const agentRegistry = new AgentWorkspaceRegistry(root, { now: () => NOW })
  await agentRegistry.load()
  const manager = createAgentManager(root, agentRegistry, fake, { workflowRegistry })
  const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
  return { root, workflowRegistry, agentRegistry, manager, parentPath }
}

async function materialize(fixture: Fixture, id: string): Promise<string> {
  const result = await fixture.manager.materialize({
    projectId: "project-1",
    childSessionId: id,
    parentWorkDir: fixture.parentPath,
    repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" },
  }, new AbortController().signal)
  if (result.kind !== "materialized") throw new Error(`materialize failed: ${result.reason}`)
  return result.workDir
}

describe("agent worktree maintenance", () => {
  it("ReleaseThenMaintenance_RemovesViaGitWorktreeRemoveAndPrune_InsideRemovalFence", async () => {
    const fixture = await createFixture()
    const id = validChildSessionId(1)
    const worktreePath = await materialize(fixture, id)
    await fixture.agentRegistry.markEligible(id)
    const fenceCalls: string[] = []
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(path: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
        fenceCalls.push(path)
        return { kind: "completed", value: await callback() }
      },
    }
    const loop = new CleanupLoop(
      fixture.agentRegistry,
      new AgentCleanupRunner(fixture.root, fixture.agentRegistry, { registry: fixture.agentRegistry, workflowRegistry: fixture.workflowRegistry }),
      fixture.root,
      () => fence,
    )

    const result = await loop.runOnce(
      { retentionDays: null, storageBudgetBytes: 100, storageTargetWatermarkBytes: 50 },
      new AbortController().signal,
    )

    expect(result.budgetRemoved).toBe(1)
    expect(fenceCalls).toEqual([worktreePath])
    expect(fake.commandArgs()).toContainEqual(["-C", fixture.parentPath, "worktree", "remove", "--force", worktreePath])
    expect(fake.commandArgs()).toContainEqual(["-C", fixture.parentPath, "branch", "-D", `mohist/wt-${id}`])
    expect(fake.commandArgs()).toContainEqual(["-C", fixture.parentPath, "worktree", "prune"])
    expect(processModule.exists(worktreePath)).toBe(false)
    expect(processModule.exists(join(fixture.parentPath, ".git", "worktrees", id))).toBe(false)
    expect(fixture.agentRegistry.get(id)).toBeNull()
  })

  it("RemovalRefusedByGit_IsNotRemoved_AndDoesNotDropTheEntry", async () => {
    const fixture = await createFixture()
    const id = validChildSessionId(1)
    const worktreePath = await materialize(fixture, id)
    await fixture.agentRegistry.markEligible(id)
    fake.failNextWorktreeRemove = true
    const loop = new CleanupLoop(
      fixture.agentRegistry,
      new AgentCleanupRunner(fixture.root, fixture.agentRegistry, { registry: fixture.agentRegistry, workflowRegistry: fixture.workflowRegistry }),
      fixture.root,
      () => null,
    )

    const result = await loop.runOnce(
      { retentionDays: null, storageBudgetBytes: 100, storageTargetWatermarkBytes: 50 },
      new AbortController().signal,
    )

    expect(result.budgetRemoved).toBe(0)
    expect(result.guardAborted).toBe(1)
    expect(processModule.exists(worktreePath)).toBe(true)
    expect(fixture.agentRegistry.get(id)).toMatchObject({ phase: "eligible" })
  })

  it("FenceBusy_AbortsTheRemoval", async () => {
    const fixture = await createFixture()
    const id = validChildSessionId(1)
    const worktreePath = await materialize(fixture, id)
    await fixture.agentRegistry.markEligible(id)
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(): Promise<WorkspaceRemovalFenceResult<T>> {
        return { kind: "busy" }
      },
    }
    const loop = new CleanupLoop(
      fixture.agentRegistry,
      new AgentCleanupRunner(fixture.root, fixture.agentRegistry, { registry: fixture.agentRegistry, workflowRegistry: fixture.workflowRegistry }),
      fixture.root,
      () => fence,
    )

    const result = await loop.runOnce(
      { retentionDays: null, storageBudgetBytes: 100, storageTargetWatermarkBytes: 50 },
      new AbortController().signal,
    )

    expect(result.budgetRemoved).toBe(0)
    expect(processModule.exists(worktreePath)).toBe(true)
    expect(fixture.agentRegistry.get(id)).toMatchObject({ phase: "eligible" })
  })

  it("CorruptWorktree_ResolvesToStuck_WithoutDeleting", async () => {
    const fixture = await createFixture()
    const id = validChildSessionId(1)
    const worktreePath = await materialize(fixture, id)
    await fixture.agentRegistry.markEligible(id)
    // Corrupt the backing entry name so the disk identity no longer matches.
    await processModule.writeText(join(worktreePath, ".git"), `gitdir: ${join(fixture.parentPath, ".git", "worktrees", validChildSessionId(9))}\n`)
    const loop = new CleanupLoop(
      fixture.agentRegistry,
      new AgentCleanupRunner(fixture.root, fixture.agentRegistry, { registry: fixture.agentRegistry, workflowRegistry: fixture.workflowRegistry }),
      fixture.root,
      () => null,
    )

    const result = await loop.runOnce({ retentionDays: 5 }, new AbortController().signal)

    expect(result.stuckResolved).toBe(1)
    expect(processModule.exists(worktreePath)).toBe(true)
    expect(fixture.agentRegistry.get(id)).toMatchObject({ phase: "stuck" })
  })

  it("ActiveChild_DefersParentRemoval_UntilTheChildIsEligible", async () => {
    const fixture = await createFixture()
    const parentId = validChildSessionId(1)
    const childId = validChildSessionId(2)
    const parentPath = await materialize(fixture, parentId)
    fake.origins.set(parentPath, GIT_URL)
    const childResult = await fixture.manager.materialize({
      projectId: "project-1",
      childSessionId: childId,
      parentWorkDir: parentPath,
      repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" },
    }, new AbortController().signal)
    if (childResult.kind !== "materialized") throw new Error(`materialize failed: ${childResult.reason}`)
    await fixture.agentRegistry.markEligible(parentId)
    fake.sizes.set(parentPath, 300)
    fake.sizes.set(agentWorkspacePath(fixture.root, childId), 300)
    const loop = new CleanupLoop(
      fixture.agentRegistry,
      new AgentCleanupRunner(fixture.root, fixture.agentRegistry, { registry: fixture.agentRegistry, workflowRegistry: fixture.workflowRegistry }),
      fixture.root,
      () => null,
    )

    const deferred = await loop.runOnce(
      { retentionDays: null, storageBudgetBytes: 100, storageTargetWatermarkBytes: 50 },
      new AbortController().signal,
    )

    expect(deferred.deferred).toBe(1)
    expect(processModule.exists(parentPath)).toBe(true)
    expect(fixture.agentRegistry.get(parentId)).toMatchObject({ phase: "eligible" })

    // The child's release clears the dependency; the parent is then removed.
    await fixture.agentRegistry.markEligible(childId)
    const after = await loop.runOnce(
      { retentionDays: null, storageBudgetBytes: 100, storageTargetWatermarkBytes: 50 },
      new AbortController().signal,
    )

    expect(after.budgetRemoved).toBe(2)
    expect(processModule.exists(parentPath)).toBe(false)
  })

  it("WorkflowWorkspace_WithActiveAgentChild_IsDeferredUntilTheChildClears", async () => {
    const fixture = await createFixture()
    const childId = validChildSessionId(1)
    const worktreePath = await materialize(fixture, childId)
    const workflowRunId = "wr-parent-1"
    await writeWorkflowMarker(fixture.parentPath, workflowRunId)
    await fixture.workflowRegistry.markEligible(workflowRunId)
    const workflowLoop = new CleanupLoop(
      fixture.workflowRegistry,
      new DefaultCleanupRunner(fixture.root, fixture.agentRegistry),
      fixture.root,
    )

    const deferred = await workflowLoop.runOnce({ retentionDays: null, storageBudgetBytes: 100, storageTargetWatermarkBytes: 50 }, new AbortController().signal)

    expect(deferred.deferred).toBe(1)
    expect(processModule.exists(fixture.parentPath)).toBe(true)
    expect(fixture.workflowRegistry.get(workflowRunId)).toMatchObject({ phase: "eligible" })

    // Child released → parent workspace is reclaimable.
    await fixture.agentRegistry.markEligible(childId)
    await fixture.agentRegistry.remove(childId)
    await processModule.deleteDirectory(worktreePath)
    const after = await workflowLoop.runOnce({ retentionDays: null, storageBudgetBytes: 100, storageTargetWatermarkBytes: 50 }, new AbortController().signal)

    expect(after.budgetRemoved).toBe(1)
    expect(processModule.exists(fixture.parentPath)).toBe(false)
    expect(fixture.workflowRegistry.get(workflowRunId)).toBeNull()
  })
})
