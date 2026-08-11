import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"

class RecordingMemoryFileSystem extends MemoryFileSystem {
  writeCount = 0

  override async writeText(path: string, content: string): Promise<void> {
    this.writeCount += 1
    await super.writeText(path, content)
  }
}

const fileSystemStorage = new AsyncLocalStorage<RecordingMemoryFileSystem>()

function fileSystem(): RecordingMemoryFileSystem {
  const value = fileSystemStorage.getStore()
  if (!value) throw new Error("workspace registry test resource context is not active")
  return value
}

const it = Object.assign(
  (name: string, body: () => unknown) => vitestIt(name, () => {
    const resources = new RecordingMemoryFileSystem()
    vi.useFakeTimers()
    vi.setSystemTime(new Date("2026-07-31T00:00:00.000Z"))
    return withTestRunnerResources(
      async () => await fileSystemStorage.run(resources, async () => {
        try {
          return await body()
        } finally {
          vi.useRealTimers()
        }
      }),
      { fileSystem: resources },
    )
  }),
  { each: vitestIt.each.bind(vitestIt) },
) as typeof vitestIt

describe("WorkspaceRegistry resolved-path index", () => {
  it("rejects a path collision before changing memory or persistence", async () => {
    const registry = new WorkspaceRegistry("/runner", { filePath: "/runner/state.json" })
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-a", workspacePath: "/runner/workspace" })
    const beforeEntries = registry.list()
    const beforeFile = await fileSystem().readText("/runner/state.json")
    const writes = fileSystem().writeCount

    await expect(registry.register({ issueNumber: 2, workflowRunId: "wr-b", workspacePath: "/runner/./workspace" })).rejects.toThrow(/already owned by workflowRunId wr-a/)

    expect(registry.list()).toEqual(beforeEntries)
    expect(await fileSystem().readText("/runner/state.json")).toBe(beforeFile)
    expect(fileSystem().writeCount).toBe(writes)
  })

  it("updates and removes the secondary index on replacement and remove", async () => {
    const registry = new WorkspaceRegistry("/runner", { filePath: "/runner/state.json" })
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-a", workspacePath: "/runner/old" })
    await registry.register({ issueNumber: 1, workflowRunId: "wr-a", workspacePath: "/runner/new" })

    expect(registry.findByWorkspacePath("/runner/old")).toBeNull()
    expect(registry.findByWorkspacePath("/runner/./new")?.workflowRunId).toBe("wr-a")
    await registry.remove("wr-a")
    expect(registry.findByWorkspacePath("/runner/new")).toBeNull()
  })

  it("loads duplicate resolved paths as an empty table", async () => {
    await fileSystem().writeText("/runner/state.json", JSON.stringify({
      version: 2,
      entries: {
        "wr-a": {
          issueNumber: 1,
          workflowRunId: "wr-a",
          workspacePath: "/runner/workspace",
          phase: "eligible",
          materializedAt: "2026-07-01T00:00:00.000Z",
          terminalAt: "2026-07-02T00:00:00.000Z",
        },
        "wr-b": {
          issueNumber: 2,
          workflowRunId: "wr-b",
          workspacePath: "/runner/./workspace",
          phase: "active",
          materializedAt: "2026-07-01T00:00:00.000Z",
          terminalAt: null,
        },
      },
    }))
    const registry = new WorkspaceRegistry("/runner", { filePath: "/runner/state.json" })

    await registry.load()

    expect(registry.list()).toEqual([])
    expect(registry.findByWorkspacePath("/runner/workspace")).toBeNull()
  })

  it("rebuilds the index from a unique persisted entry without listing", async () => {
    await fileSystem().writeText("/runner/state.json", JSON.stringify({
      version: 2,
      entries: {
        "wr-a": {
          issueNumber: 1,
          workflowRunId: "wr-a",
          workspacePath: "/runner/workspace",
          phase: "eligible",
          materializedAt: "2026-07-01T00:00:00.000Z",
          terminalAt: "2026-07-02T00:00:00.000Z",
        },
      },
    }))
    const registry = new WorkspaceRegistry("/runner", { filePath: "/runner/state.json" })
    await registry.load()
    vi.spyOn(registry, "list").mockImplementation(() => {
      throw new Error("path lookup must not list the registry")
    })

    expect(registry.findByWorkspacePath("/runner/./workspace")?.workflowRunId).toBe("wr-a")
  })
})
