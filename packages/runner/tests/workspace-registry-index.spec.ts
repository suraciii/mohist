import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"

const fakeFiles = vi.hoisted(() => new Map<string, string>())
const fakeFs = vi.hoisted(() => ({
  mkdir: vi.fn(async () => undefined),
  rename: vi.fn(async (from: string, to: string) => {
    const content = fakeFiles.get(from)
    if (content !== undefined) fakeFiles.set(to, content)
    fakeFiles.delete(from)
  }),
  writeFile: vi.fn(async (path: string, content: string) => {
    fakeFiles.set(path, content)
  }),
}))

vi.mock("node:fs/promises", () => fakeFs)
vi.mock("../src/system/process.js", () => ({
  exists: (path: string) => fakeFiles.has(path),
  readText: async (path: string) => {
    const content = fakeFiles.get(path)
    if (content === undefined) throw new Error("missing fake file")
    return content
  },
}))

const { WorkspaceRegistry } = await import("../src/runtime/workspace-registry.js")

describe("WorkspaceRegistry resolved-path index", () => {
  beforeEach(() => {
    fakeFiles.clear()
    fakeFs.mkdir.mockClear()
    fakeFs.rename.mockClear()
    fakeFs.writeFile.mockClear()
    vi.useFakeTimers()
    vi.setSystemTime(new Date("2026-07-31T00:00:00.000Z"))
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it("rejects a path collision before changing memory or persistence", async () => {
    const registry = new WorkspaceRegistry("/runner", { filePath: "/runner/state.json" })
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-a", workspacePath: "/runner/workspace" })
    const beforeEntries = registry.list()
    const beforeFile = fakeFiles.get("/runner/state.json")
    const writes = fakeFs.writeFile.mock.calls.length

    await expect(registry.register({ issueNumber: 2, workflowRunId: "wr-b", workspacePath: "/runner/./workspace" })).rejects.toThrow(/already owned by workflowRunId wr-a/)

    expect(registry.list()).toEqual(beforeEntries)
    expect(fakeFiles.get("/runner/state.json")).toBe(beforeFile)
    expect(fakeFs.writeFile).toHaveBeenCalledTimes(writes)
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
    fakeFiles.set("/runner/state.json", JSON.stringify({
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
    fakeFiles.set("/runner/state.json", JSON.stringify({
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
