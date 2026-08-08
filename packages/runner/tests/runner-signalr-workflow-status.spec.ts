import { join } from "node:path"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { RunnerSignalRClient } from "../src/server/runner-signalr.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../src/system/filesystem.js"
import { WorkspaceRegistry, defaultWorkspaceRegistryFilePath } from "../src/runtime/workspace-registry.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { currentSignalRTestState, withSignalRTestResources } from "./support/signalr-test-resources.js"

// End-to-end coverage of the server's ReceiveWorkflowRunStatus
// SignalR method transitions the matching active registry entry to
// eligible and stamps terminalAt (idempotent — already-eligible entries
// are not re-stamped). Non-terminal statuses leave the entry active.

interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  state: { connected: boolean }
}

type SignalRResources = {
  fileSystem: RunnerFileSystem
  signalRExistsChecker?: (path: string) => boolean
}

function it(name: string, body: (resources: SignalRResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: SignalRResources = { fileSystem: new MemoryFileSystem() }
    await withSignalRTestResources(resources, async () => await body(resources))
  })
}

vi.mock("@microsoft/signalr", () => {
  class FakeConnection {
    state = "Disconnected"
    connectionId: string | null = null
    startCalls = 0
    start = vi.fn(async () => {
      this.state = "Connected"
      this.connectionId = "conn-test"
    })
    stop = vi.fn(async () => {
      this.state = "Disconnected"
      this.connectionId = null
    })
    invoke = vi.fn()
    on = vi.fn((event: string, handler: (...args: unknown[]) => unknown) => {
      const builder = currentSignalRTestState().builders.at(-1) as CapturedBuilder | undefined
      if (!builder) throw new Error("no captured SignalR builder")
      builder.handlers.set(event, handler)
      return this
    })
    onreconnected = vi.fn()
  }

  return {
    HubConnectionBuilder: class {
      private _connection = new FakeConnection()
      withUrl() {
        const builder: CapturedBuilder = { handlers: new Map(), state: { connected: true } }
        currentSignalRTestState().builders.push(builder)
        return this
      }
      withAutomaticReconnect() { return this }
      build() {
        return this._connection as unknown as import("@microsoft/signalr").HubConnection
      }
    },
    HubConnectionState: {
      Disconnected: "Disconnected",
      Connecting: "Connecting",
      Connected: "Connected",
      Disconnecting: "Disconnecting",
      Reconnecting: "Reconnecting",
    },
  }
})

describe("RunnerSignalRClient receives workflow run status updates", () => {
  const root = "/virtual/runner"

  async function newClient(resources: SignalRResources, registry: WorkspaceRegistry): Promise<{ client: RunnerSignalRClient; handler: (payload: unknown) => Promise<unknown> }> {
    resources.signalRExistsChecker = () => false
    const client = new RunnerSignalRClient("https://runner.test", "runner-test", root, null, { registry })
    const builder = currentSignalRTestState().builders.at(-1) as CapturedBuilder | undefined
    const handler = builder?.handlers.get("ReceiveWorkflowRunStatus")
    if (!handler) throw new Error("ReceiveWorkflowRunStatus handler not registered")
    return { client, handler: handler as (payload: unknown) => Promise<unknown> }
  }

  it("OnCompletedPush_TransitionsActiveEntryToEligible", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 42, workflowRunId: "wr-123", workspacePath: join(root, "wks") })

    const { handler } = await newClient(resources, registry)
    await handler({ workflowRunId: "wr-123", status: "Completed" })

    const entry = registry.get("wr-123")
    expect(entry?.phase).toBe("eligible")
    expect(entry?.terminalAt).toBeTruthy()
  })

  it("OnStoppedPush_TransitionsActiveEntryToEligible", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const { handler } = await newClient(resources, registry)
    await handler({ workflowRunId: "wr-1", status: "Stopped" })

    expect(registry.get("wr-1")?.phase).toBe("eligible")
  })

  it("OnFailedPush_LeavesEntryActive", async (resources) => {
    // Failed is a recoverable mid-state (Retry/Rerun revive it), NOT
    // terminal: a push for Failed must not transition the workspace to
    // eligible — reclaims mid-retry lose the run branch.
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const { handler } = await newClient(resources, registry)
    await handler({ workflowRunId: "wr-1", status: "Failed" })

    expect(registry.get("wr-1")?.phase).toBe("active")
    expect(registry.get("wr-1")?.terminalAt).toBeNull()
  })

  it("OnRePushForAlreadyEligibleEntry_DoesNotReStampTerminalAt", async (resources) => {
    // Stamp terminalAt via the registry directly at a known time,
    // then re-deliver a push at a later time. The handler's markEligible
    // is idempotent: it must NOT re-stamp an already-eligible entry.
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })
    await registry.markEligible("wr-1")
    const firstTerminal = registry.get("wr-1")?.terminalAt
    expect(firstTerminal).toBeTruthy()

    // Move the wall clock well past the original terminal time so any
    // re-stamp would produce a different value.
    const later = new Date("2026-12-25T00:00:00.000Z")
    vi.useFakeTimers()
    vi.setSystemTime(later)
    try {
      const { handler } = await newClient(resources, registry)
      await handler({ workflowRunId: "wr-1", status: "Completed" })
    } finally {
      vi.useRealTimers()
    }

    const after = registry.get("wr-1")
    expect(after?.phase).toBe("eligible")
    expect(after?.terminalAt).toBe(firstTerminal)
  })

  it("OnNonTerminalPush_LeavesEntryActive", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const { handler } = await newClient(resources, registry)
    // Server only pushes terminal statuses today (per RunnerWorkflowStatusRouter),
    // but the handler must defensively tolerate anything that arrives.
    // The full new vocabulary (D1) is exercised here so a regression in
    // any single value would be caught: any non-terminal status reported
    // via push must leave the registry entry active and un-stamped.
    for (const status of ["Created", "Pending", "Ready", "Running", "Paused", "AwaitingApproval"]) {
      await handler({ workflowRunId: "wr-1", status })
      expect(registry.get("wr-1")?.phase).toBe("active")
      expect(registry.get("wr-1")?.terminalAt).toBeNull()
    }
  })

  it("OnPushForUnknownRunId_DoesNotThrowAndLeavesRegistryUntouched", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()

    const { handler } = await newClient(resources, registry)
    await expect(handler({ workflowRunId: "wr-other-runner", status: "Completed" })).resolves.toBeUndefined()

    expect(registry.list()).toHaveLength(0)
  })

  it("OnNullPayload_DoesNotThrow", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const { handler } = await newClient(resources, registry)
    await expect(handler(null)).resolves.toBeUndefined()
    await expect(handler(undefined)).resolves.toBeUndefined()

    expect(registry.get("wr-1")?.phase).toBe("active")
  })

  it("OnPayloadWithMissingWorkflowRunId_DoesNotThrow", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const { handler } = await newClient(resources, registry)
    await expect(handler({ status: "Completed" })).resolves.toBeUndefined()
    await expect(handler({ workflowRunId: "", status: "Completed" })).resolves.toBeUndefined()

    expect(registry.get("wr-1")?.phase).toBe("active")
  })

  it("OnPush_PersistsRegistryOnDisk", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-persist", workspacePath: join(root, "w1") })

    const { handler } = await newClient(resources, registry)
    await handler({ workflowRunId: "wr-persist", status: "Completed" })

    const onDisk = JSON.parse(await resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(root)))
    expect(onDisk.entries["wr-persist"]).toMatchObject({ phase: "eligible" })
    expect(onDisk.entries["wr-persist"].terminalAt).toBeTruthy()
  })

  it("OnTerminalPush_TwoIndependentRuns_BothTransitionIndependently", async (resources) => {
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })
    await registry.register({ issueNumber: 2, workflowRunId: "wr-2", workspacePath: join(root, "w2") })

    const { handler } = await newClient(resources, registry)
    await handler({ workflowRunId: "wr-1", status: "Completed" })
    await handler({ workflowRunId: "wr-2", status: "Stopped" })

    expect(registry.get("wr-1")?.phase).toBe("eligible")
    expect(registry.get("wr-2")?.phase).toBe("eligible")
  })

  it("OnTerminalPush_TerminalAtReflectsPushTime", async (resources) => {
    const at = new Date("2026-06-25T09:30:00.000Z")
    vi.useFakeTimers()
    vi.setSystemTime(at)
    const registry = new WorkspaceRegistry(root)
    await registry.load()
    await registry.register({ issueNumber: 1, workflowRunId: "wr-1", workspacePath: join(root, "w1") })

    const { handler } = await newClient(resources, registry)
    await handler({ workflowRunId: "wr-1", status: "Completed" })

    expect(registry.get("wr-1")?.terminalAt).toBe(at.toISOString())
    vi.useRealTimers()
  })
})
