import { AsyncLocalStorage } from "node:async_hooks"
import { join } from "node:path"
import { describe, expect, it as vitestIt, vi } from "vitest"
import {
  materializeNamedWorkspace,
  namedWorkspaceMarkerPath,
  namedWorkspacePath,
  NamedWorkspaceManager,
  readNamedWorkspaceMarker,
  WorkspaceHomeClaimedError,
} from "../src/runtime/workspace-entity.js"
import { NamedWorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import type { ServerConnection } from "../src/server/connection.js"
import { createTestTempDir } from "./support/temp-dir.js"
import { mkdir, readFile, rm, stat, symlink, writeFile } from "./support/test-fs.js"
import { withTestRunnerResources } from "./support/test-resources.js"

async function makeRunnerRoot() {
  return await createTestTempDir("mohist-named-ws-")
}

interface TestContext {
  root: string
  registry: NamedWorkspaceRegistry
}

const testContextStorage = new AsyncLocalStorage<TestContext>()

function context(): TestContext {
  const value = testContextStorage.getStore()
  if (!value) throw new Error("workspace entity test resource context is not active")
  return value
}

const it = Object.assign(
  (name: string, body: () => unknown) => vitestIt(name, () => withTestRunnerResources(async () => {
    const root = await makeRunnerRoot()
    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()
    await testContextStorage.run({ root, registry }, async () => await body())
  })),
  { each: vitestIt.each.bind(vitestIt) },
) as typeof vitestIt

const now = new Date("2026-07-01T08:00:00.000Z")

describe("namedWorkspacePath", () => {
  it("is deterministic per (projectId, workspaceName)", () => {
    expect(namedWorkspacePath("/root", "mohist", "pay")).toBe(namedWorkspacePath("/root", "mohist", "pay"))
  })

  it("lives under the managed workspaces parent", () => {
    const path = namedWorkspacePath("/root", "mohist", "pay")
    expect(path.startsWith(join("/root", "workspaces"))).toBe(true)
  })

  it("distinguishes slug collisions with a content hash", () => {
    expect(namedWorkspacePath("/root", "mohist", "a b")).not.toBe(namedWorkspacePath("/root", "mohist", "a-b"))
  })

  it("differs across projects", () => {
    expect(namedWorkspacePath("/root", "mohist", "pay")).not.toBe(namedWorkspacePath("/root", "other", "pay"))
  })
})

describe("materializeNamedWorkspace", () => {
  it("creates an empty persistent directory with an identity marker and an active registry entry", async () => {
    const { root, registry } = context()
    const result = await materializeNamedWorkspace({
      runnerRoot: root,
      projectId: "mohist",
      workspaceName: "pay",
      repositories: [{ name: "server", gitUrl: "https://github.com/mohist/server.git" }],
      registry,
    })

    expect(result.path).toBe(namedWorkspacePath(root, "mohist", "pay"))
    expect(result.created).toBe(true)

    const marker = await readNamedWorkspaceMarker(result.path)
    expect(marker).toEqual({
      projectId: "mohist",
      workspaceName: "pay",
      repositories: [{ name: "server", gitUrl: "https://github.com/mohist/server.git" }],
    })

    const entry = registry.get("mohist", "pay")
    expect(entry).toMatchObject({
      projectId: "mohist",
      workspaceName: "pay",
      workspacePath: result.path,
      phase: "active",
      materializedAt: now.toISOString(),
    })
  })

  it("keeps an existing directory and reports created=false on re-materialization", async () => {
    const { root, registry } = context()
    const first = await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    const second = await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })

    expect(second.path).toBe(first.path)
    expect(second.created).toBe(false)
    expect(registry.get("mohist", "pay")).toMatchObject({ workspacePath: first.path, phase: "active" })
  })

  it("re-materializes an empty directory after the old one was recycled", async () => {
    const { root, registry } = context()
    await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    await rm(namedWorkspacePath(root, "mohist", "pay"), { recursive: true, force: true })

    const result = await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    expect(result.created).toBe(true)
    const marker = await readNamedWorkspaceMarker(result.path)
    expect(marker?.workspaceName).toBe("pay")
  })

  it("rejects a symlinked directory through the managed-path walk", async () => {
    const { root, registry } = context()
    await mkdir(join(root, "workspaces"), { recursive: true })
    await writeFile(join(root, "marker-for-symlink"), "x")
    await symlink(join(root, "marker-for-symlink"), namedWorkspacePath(root, "mohist", "pay"))
    await expect(materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })).rejects.toThrow(/symlink/i)
  })

  it("writes the marker inside .mohist/workspace.json", async () => {
    const { root, registry } = context()
    await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    const raw = JSON.parse(await readFile(namedWorkspaceMarkerPath(namedWorkspacePath(root, "mohist", "pay")), "utf8") as string)
    expect(raw).toMatchObject({ projectId: "mohist", workspaceName: "pay" })
  })
})

describe("NamedWorkspaceManager", () => {
  it("reports the materialized path to the server", async () => {
    const { root, registry } = context()
    const report = vi.fn(async () => ({ runnerId: "runner-1", path: namedWorkspacePath(root, "mohist", "pay") }))
    const manager = new NamedWorkspaceManager(root, registry, { reportWorkspaceMaterialized: report } as never)

    const result = await manager.materialize("mohist", "pay", [], new AbortController().signal)

    expect(report).toHaveBeenCalledWith("mohist", "pay", result.path, expect.any(AbortSignal))
    expect(registry.get("mohist", "pay")).not.toBeNull()
  })

  it("yields (deleting only a directory it created) when the home is claimed by another runner", async () => {
    const { root, registry } = context()
    const claimed = new WorkspaceHomeClaimedError("already materialized on runner-2")
    const report = vi.fn(async () => {
      throw claimed
    })
    const manager = new NamedWorkspaceManager(root, registry, { reportWorkspaceMaterialized: report } as never)

    await expect(manager.materialize("mohist", "pay", [], new AbortController().signal)).rejects.toBe(claimed)
    expect(registry.get("mohist", "pay")).toBeNull()
    await expect(stat(namedWorkspacePath(root, "mohist", "pay"))).rejects.toMatchObject({ code: "ENOENT" })
  })

  it("does not delete a pre-existing directory when yielding a claimed home", async () => {
    const { root, registry } = context()
    await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    await registry.remove("ws:mohist:pay")
    const report = vi.fn(async () => {
      throw new WorkspaceHomeClaimedError("already materialized on runner-2")
    })
    const manager = new NamedWorkspaceManager(root, registry, { reportWorkspaceMaterialized: report } as never)

    await expect(manager.materialize("mohist", "pay", [], new AbortController().signal)).rejects.toBeInstanceOf(WorkspaceHomeClaimedError)
    const marker = await readNamedWorkspaceMarker(namedWorkspacePath(root, "mohist", "pay"))
    expect(marker?.workspaceName).toBe("pay")
  })

  it("propagates non-claim materialization report failures", async () => {
    const { root, registry } = context()
    const report = vi.fn(async () => {
      throw new Error("workspace materialization failed: 500")
    })
    const manager = new NamedWorkspaceManager(root, registry, { reportWorkspaceMaterialized: report } as never)

    await expect(manager.materialize("mohist", "pay", [], new AbortController().signal)).rejects.toThrow("workspace materialization failed: 500")
  })
})
