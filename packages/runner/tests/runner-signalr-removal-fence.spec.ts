import type { HubConnection } from "@microsoft/signalr"
import { AsyncLocalStorage } from "node:async_hooks"
import { describe, expect, it, vi } from "vitest"
import { RunnerSignalRClient } from "../src/server/runner-signalr.js"
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from "../src/runtime/workspace-removal-fence.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"

interface RemovalFenceTestState {
  readonly handlers: Map<string, (...args: unknown[]) => unknown>
  currentRuntime: OpenCodeRuntime | null
}

const removalFenceTestStorage = new AsyncLocalStorage<RemovalFenceTestState>()

function currentRemovalFenceTestState(): RemovalFenceTestState {
  const state = removalFenceTestStorage.getStore()
  if (!state) throw new Error("removal fence test resource context is not active")
  return state
}

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    build() {
      return {
        state: "Disconnected",
        connectionId: null,
        on(event: string, callback: (...args: unknown[]) => unknown) {
          currentRemovalFenceTestState().handlers.set(event, callback)
          return this
        },
        onreconnected() { return this },
        start: vi.fn(async () => undefined),
        stop: vi.fn(async () => undefined),
      } as unknown as HubConnection
    }
  },
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Disconnecting: "Disconnecting",
    Reconnecting: "Reconnecting",
  },
}))

describe("RunnerSignalR removal fence adapter", () => {
  it("resolves the existing OpenCode accessor at RemoveWorkspace invocation time", async () => {
    const state: RemovalFenceTestState = { handlers: new Map(), currentRuntime: null }
    return await removalFenceTestStorage.run(state, async () => {
      const fence: WorkspaceRemovalFence = {
        async withRemovalFence<T>(): Promise<WorkspaceRemovalFenceResult<T>> {
          return { kind: "busy" }
        },
      }
      const runtime = { withRemovalFence: fence.withRemovalFence } as unknown as OpenCodeRuntime
      new RunnerSignalRClient("https://runner.test", "runner-1", "/virtual/projects", null, {
        openCodeRuntime: () => currentRemovalFenceTestState().currentRuntime,
      })
      currentRemovalFenceTestState().currentRuntime = runtime

      const handler = currentRemovalFenceTestState().handlers.get("RemoveWorkspace")!
      const result = await handler({
        workflowRunId: "wr-1",
        gitUrl: "https://repo.test/mohist.git",
        workspacePath: "/virtual/projects/workspaces/wr-1",
        branch: "mohist/run-wr-1",
        baseBranch: "main",
      })

      expect(result).toMatchObject({
        removed: false,
        status: "failed",
        reason: "workspace_cleanup_failed",
        message: "Workspace is busy and cannot be safely released",
      })
    })
  })
})
