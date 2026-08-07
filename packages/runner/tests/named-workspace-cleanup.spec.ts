import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { CleanupLoop } from "../src/runtime/cleanup-loop.js"
import { materializeNamedWorkspace, namedWorkspacePath } from "../src/runtime/workspace-entity.js"
import {
  NamedWorkspaceCleanupRunner,
  NamedWorkspaceReclaimProbe,
} from "../src/runtime/named-workspace-cleanup.js"
import {
  NamedWorkspaceRegistry,
  namedWorkspaceRegistryKey,
} from "../src/runtime/workspace-registry.js"
import type { CleanupPolicy } from "../src/core/types.js"

async function makeRunnerRoot() {
  return await mkdtemp(join(tmpdir(), "mohist-named-cleanup-"))
}

const signal = new AbortController().signal

describe("NamedWorkspaceReclaimProbe", () => {
  let root: string
  let registry: NamedWorkspaceRegistry
  const now = new Date("2026-07-01T08:00:00.000Z")

  beforeEach(async () => {
    root = await makeRunnerRoot()
    registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
    vi.restoreAllMocks()
  })

  it("promotes an archived workspace to eligible", async () => {
    await registry.register({ projectId: "mohist", workspaceName: "pay", workspacePath: "/tmp/pay" })
    const connection = { getWorkspaceReclaimability: vi.fn(async () => ({ status: "archived" as const, activeBoundSessions: 0 })) }
    const probe = new NamedWorkspaceReclaimProbe(registry, connection as never)

    const result = await probe.runOnce(signal)

    expect(result).toEqual({ markedEligible: 1, deferred: 0, unobserved: 0 })
    expect(registry.get("mohist", "pay")).toMatchObject({ phase: "eligible", terminalAt: now.toISOString() })
  })

  it("promotes an active workspace with no active bound session to eligible", async () => {
    await registry.register({ projectId: "mohist", workspaceName: "pay", workspacePath: "/tmp/pay" })
    const connection = { getWorkspaceReclaimability: vi.fn(async () => ({ status: "active" as const, activeBoundSessions: 0 })) }
    const probe = new NamedWorkspaceReclaimProbe(registry, connection as never)

    const result = await probe.runOnce(signal)

    expect(result.markedEligible).toBe(1)
    expect(registry.get("mohist", "pay")?.phase).toBe("eligible")
  })

  it("keeps an active workspace with an active bound session active", async () => {
    await registry.register({ projectId: "mohist", workspaceName: "pay", workspacePath: "/tmp/pay" })
    const connection = { getWorkspaceReclaimability: vi.fn(async () => ({ status: "active" as const, activeBoundSessions: 2 })) }
    const probe = new NamedWorkspaceReclaimProbe(registry, connection as never)

    const result = await probe.runOnce(signal)

    expect(result).toEqual({ markedEligible: 0, deferred: 1, unobserved: 0 })
    expect(registry.get("mohist", "pay")?.phase).toBe("active")
  })

  it("leaves an unobservable workspace active and retries next tick", async () => {
    await registry.register({ projectId: "mohist", workspaceName: "pay", workspacePath: "/tmp/pay" })
    const connection = {
      getWorkspaceReclaimability: vi.fn(async () => {
        throw new Error("workspace reclaimability failed: 500")
      }),
    }
    const probe = new NamedWorkspaceReclaimProbe(registry, connection as never)

    const result = await probe.runOnce(signal)

    expect(result).toEqual({ markedEligible: 0, deferred: 0, unobserved: 1 })
    expect(registry.get("mohist", "pay")?.phase).toBe("active")
  })

  it("ignores non-active entries", async () => {
    await registry.register({ projectId: "mohist", workspaceName: "pay", workspacePath: "/tmp/pay" })
    await registry.markEligible("mohist", "pay")
    const connection = { getWorkspaceReclaimability: vi.fn(async () => ({ status: "active" as const, activeBoundSessions: 0 })) }
    const probe = new NamedWorkspaceReclaimProbe(registry, connection as never)

    const result = await probe.runOnce(signal)

    expect(result.markedEligible).toBe(0)
    expect(connection.getWorkspaceReclaimability).not.toHaveBeenCalled()
  })
})

describe("NamedWorkspaceCleanupRunner", () => {
  let root: string
  let registry: NamedWorkspaceRegistry
  const now = new Date("2026-07-01T08:00:00.000Z")

  beforeEach(async () => {
    root = await makeRunnerRoot()
    registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  it("reads the named workspace marker identity", async () => {
    const runner = new NamedWorkspaceCleanupRunner(root, registry)
    await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    const identity = await runner.readWorkspaceIdentity(namedWorkspacePath(root, "mohist", "pay"))
    expect(identity).toBe(namedWorkspaceRegistryKey("mohist", "pay"))
  })

  it("returns null identity for a directory without a marker", async () => {
    const runner = new NamedWorkspaceCleanupRunner(root, registry)
    expect(await runner.readWorkspaceIdentity(join(root, "workspaces", "unknown"))).toBeNull()
  })

  it("validates the workspace only when marker and derived path match the entry", async () => {
    const runner = new NamedWorkspaceCleanupRunner(root, registry)
    await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    const entry = registry.get("mohist", "pay")!
    expect(await runner.validateWorkspace(entry)).toBe(true)

    const foreign: typeof entry = { ...entry, projectId: "other" }
    expect(await runner.validateWorkspace(foreign)).toBe(false)
  })
})

describe("named workspace cleanup loop end to end", () => {
  let root: string
  let registry: NamedWorkspaceRegistry
  let runner: NamedWorkspaceCleanupRunner
  const now = new Date("2026-07-01T08:00:00.000Z")

  beforeEach(async () => {
    root = await makeRunnerRoot()
    registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()
    runner = new NamedWorkspaceCleanupRunner(root, registry)
  })

  afterEach(async () => {
    await rm(root, { recursive: true, force: true })
  })

  it("evicts an eligible named workspace past the retention window", async () => {
    let current = now
    const registry = new NamedWorkspaceRegistry(root, { now: () => current })
    await registry.load()
    await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    const past = new Date(now.getTime() - 10 * 24 * 60 * 60 * 1000)
    current = past
    await registry.markEligible("mohist", "pay")

    const loop = new CleanupLoop(registry, runner, root, () => null)
    const policy: CleanupPolicy = { retentionDays: 5 }
    const result = await loop.runOnce(policy, signal)

    expect(result.retentionRemoved).toBe(1)
    expect(registry.get("mohist", "pay")).toBeNull()
    await expect(import("node:fs/promises").then((fs) => fs.stat(namedWorkspacePath(root, "mohist", "pay")))).rejects.toMatchObject({ code: "ENOENT" })
  })

  it("refuses (stuck) an eligible entry whose marker identity mismatches the registry", async () => {
    await materializeNamedWorkspace({ runnerRoot: root, projectId: "mohist", workspaceName: "pay", registry })
    await registry.markEligible("mohist", "pay")
    // Corrupt the marker to a different workspace identity.
    await import("node:fs/promises").then((fs) => fs.writeFile(
      join(namedWorkspacePath(root, "mohist", "pay"), ".mohist", "workspace.json"),
      JSON.stringify({ projectId: "other", workspaceName: "pay", repositories: [] }),
    ))

    const loop = new CleanupLoop(registry, runner, root, () => null)
    const policy: CleanupPolicy = { retentionDays: 5 }
    const result = await loop.runOnce(policy, signal)

    expect(result.stuckResolved).toBe(1)
    expect(result.retentionRemoved).toBe(0)
    expect(registry.get("mohist", "pay")?.phase).toBe("stuck")
  })
})

