import type { HubConnection } from "@microsoft/signalr"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { registerWorkspaceRemovalHandler } from "../src/server/workspace-removal-handler.js"
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from "../src/runtime/workspace-removal-fence.js"
import type { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"

const deleteDirectory = vi.hoisted(() => vi.fn(async (_path: string) => undefined))
const validateWorkspaceIdentity = vi.hoisted(() => vi.fn(async (..._args: unknown[]) => undefined))

vi.mock("../src/system/process.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../src/system/process.js")>()),
  deleteDirectory,
}))
vi.mock("../src/runtime/workspace.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../src/runtime/workspace.js")>()),
  validateWorkspaceIdentity,
}))

const workspacePath = "/runner/workspaces/wr-1"
const query = {
  workflowRunId: "wr-1",
  gitUrl: "https://repo.test/mohist.git",
  workspacePath,
  branch: "mohist/run-wr-1",
  baseBranch: "main",
}

function createRegistry(calls: string[]): WorkspaceRegistry {
  return {
    findByWorkspacePath: vi.fn(() => ({ workflowRunId: query.workflowRunId })),
    remove: vi.fn(async () => {
      calls.push("registry-remove")
      return true
    }),
  } as unknown as WorkspaceRegistry
}

function createHandler(deps: Parameters<typeof registerWorkspaceRemovalHandler>[1]) {
  let handler!: (query: unknown) => Promise<unknown>
  const connection = {
    on: vi.fn((_method: string, callback: typeof handler) => {
      handler = callback
    }),
  } as unknown as HubConnection
  registerWorkspaceRemovalHandler(connection, deps)
  return handler
}

function completedFence(calls: string[]): WorkspaceRemovalFence {
  return {
    async withRemovalFence<T>(path: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
      calls.push(`fence-enter:${path}`)
      const value = await callback()
      calls.push("fence-exit")
      return { kind: "completed", value }
    },
  }
}

describe("RemoveWorkspace removal fence", () => {
  beforeEach(() => {
    deleteDirectory.mockClear().mockResolvedValue(undefined)
    validateWorkspaceIdentity.mockClear().mockResolvedValue(undefined)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it("keeps path inspection, identity, delete, and registry removal inside the idle fence", async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    const handler = createHandler({
      runnerRoot: "/runner",
      registry,
      pathExists: vi.fn(() => {
        calls.push("path-exists")
        return true
      }),
      removalFence: () => completedFence(calls),
    })
    validateWorkspaceIdentity.mockImplementation(async () => {
      calls.push("identity")
    })
    deleteDirectory.mockImplementation(async () => {
      calls.push("delete")
    })

    await expect(handler(query)).resolves.toMatchObject({ removed: true, status: "removed" })
    expect(calls).toEqual([
      `fence-enter:${workspacePath}`,
      "path-exists",
      "identity",
      "delete",
      "registry-remove",
      "fence-exit",
    ])
  })

  it.each([
    ["busy", "Workspace is busy and cannot be safely released"],
    ["failed", "Workspace cannot be safely released because the removal fence failed"],
  ] as const)("returns cleanup failure and does not enter the callback when the fence is %s", async (kind, message) => {
    const pathExists = vi.fn(() => true)
    const registry = createRegistry([])
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(): Promise<WorkspaceRemovalFenceResult<T>> {
        return { kind }
      },
    }
    const handler = createHandler({ runnerRoot: "/runner", registry, pathExists, removalFence: () => fence })

    await expect(handler(query)).resolves.toEqual({
      removed: false,
      status: "failed",
      path: workspacePath,
      reason: "workspace_cleanup_failed",
      message,
    })
    expect(pathExists).not.toHaveBeenCalled()
    expect(validateWorkspaceIdentity).not.toHaveBeenCalled()
    expect(deleteDirectory).not.toHaveBeenCalled()
    expect(registry.remove).not.toHaveBeenCalled()
  })

  it("drops registry identity for a missing directory only after fence admission", async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    const handler = createHandler({
      runnerRoot: "/runner",
      registry,
      pathExists: vi.fn(() => {
        calls.push("path-exists")
        return false
      }),
      removalFence: () => completedFence(calls),
    })

    await expect(handler(query)).resolves.toEqual({
      removed: false,
      status: "missing",
      path: workspacePath,
      reason: "workspace_missing",
      message: "Workspace already removed",
    })
    expect(calls).toEqual([`fence-enter:${workspacePath}`, "path-exists", "registry-remove", "fence-exit"])
    expect(validateWorkspaceIdentity).not.toHaveBeenCalled()
    expect(deleteDirectory).not.toHaveBeenCalled()
  })

  it("keeps the existing behavior when no Runtime fence is available", async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    const handler = createHandler({
      runnerRoot: "/runner",
      registry,
      pathExists: vi.fn(() => true),
    })

    await expect(handler(query)).resolves.toMatchObject({ removed: true, status: "removed" })
    expect(registry.remove).toHaveBeenCalledOnce()
    expect(deleteDirectory).toHaveBeenCalledOnce()
  })

  it("preserves identity failure semantics inside the fence", async () => {
    const calls: string[] = []
    const registry = createRegistry(calls)
    validateWorkspaceIdentity.mockRejectedValue(new Error("marker mismatch"))
    const handler = createHandler({
      runnerRoot: "/runner",
      registry,
      pathExists: vi.fn(() => true),
      removalFence: () => completedFence(calls),
    })

    await expect(handler(query)).resolves.toEqual({
      removed: false,
      status: "failed",
      path: workspacePath,
      reason: "workspace_identity_mismatch",
      message: "marker mismatch",
    })
    expect(deleteDirectory).not.toHaveBeenCalled()
    expect(registry.remove).not.toHaveBeenCalled()
  })
})
