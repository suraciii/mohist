import { describe, expect, it, vi } from "vitest"
import { CleanupLoop, type CleanupRunner } from "../src/runtime/cleanup-loop.js"
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from "../src/runtime/workspace-removal-fence.js"
import type { WorkspaceRegistry, WorkspaceRegistryEntry } from "../src/runtime/workspace-registry.js"

const entry: WorkspaceRegistryEntry = {
  issueNumber: 1,
  workflowRunId: "wr-fenced",
  workspacePath: "/runner/workspace",
  runBranch: "mohist/run-wr-fenced",
  phase: "eligible",
  materializedAt: "2026-07-01T00:00:00.000Z",
  terminalAt: "2026-07-02T00:00:00.000Z",
}

function createFixture(calls: string[] = []) {
  const registry = {
    entryKey: vi.fn(() => entry.workflowRunId),
    remove: vi.fn(async () => {
      calls.push("registry-remove")
      return true
    }),
  } as unknown as WorkspaceRegistry
  const runner: CleanupRunner = {
    isUnderRunnerRoot: vi.fn(() => {
      calls.push("guard-root")
      return true
    }),
    pathExists: vi.fn(() => {
      calls.push("path-exists")
      return true
    }),
    readWorkspaceIdentity: vi.fn(async () => {
      calls.push("guard-marker")
      return entry.workflowRunId
    }),
    deleteDirectory: vi.fn(async () => {
      calls.push("delete")
    }),
    computeDirectorySize: vi.fn(async () => 0),
  }
  return { registry, runner }
}

describe("CleanupLoop removal fence", () => {
  it("runs final guards, deletion, and registry removal inside the fence", async () => {
    const calls: string[] = []
    const orderedFixture = createFixture(calls)
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(_path: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
        calls.push("fence-enter")
        const value = await callback()
        calls.push("fence-exit")
        return { kind: "completed", value }
      },
    }
    const withRemovalFence = vi.spyOn(fence, "withRemovalFence")
    const loop = new CleanupLoop(orderedFixture.registry, orderedFixture.runner, "/runner", () => fence)

    const removed = await loop.safeRemove(entry)

    expect(removed).toBe(true)
    expect(calls).toEqual(["fence-enter", "guard-root", "guard-marker", "path-exists", "delete", "registry-remove", "fence-exit"])
    expect(orderedFixture.runner.deleteDirectory).toHaveBeenCalledWith(entry.workspacePath)
    expect(orderedFixture.registry.remove).toHaveBeenCalledWith(entry.workflowRunId)
    expect(withRemovalFence).toHaveBeenCalledWith(entry.workspacePath, expect.any(Function))
  })

  it("does not delete when the fresh fence reports busy", async () => {
    const fixture = createFixture()
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(_path: string, _callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
        return { kind: "busy" }
      },
    }
    const withRemovalFence = vi.spyOn(fence, "withRemovalFence")
    const loop = new CleanupLoop(fixture.registry, fixture.runner, "/runner", () => fence)

    expect(await loop.safeRemove({ ...entry, workflowRunId: "wr-busy" })).toBe(false)
    expect(withRemovalFence).toHaveBeenCalledOnce()
    expect(fixture.runner.deleteDirectory).not.toHaveBeenCalled()
    expect(fixture.registry.remove).not.toHaveBeenCalled()
  })
})
