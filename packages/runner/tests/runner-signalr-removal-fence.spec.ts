import type { HubConnection } from "@microsoft/signalr"
import { describe, expect, it, vi } from "vitest"
import { RunnerSignalRClient } from "../src/server/runner-signalr.js"
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from "../src/runtime/workspace-removal-fence.js"
import type { OpenCodeRuntime } from "../src/runtime/opencode/index.js"

const handlers = new Map<string, (...args: unknown[]) => unknown>()

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    build() {
      return {
        state: "Disconnected",
        connectionId: null,
        on(event: string, callback: (...args: unknown[]) => unknown) {
          handlers.set(event, callback)
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
    handlers.clear()
    const fence: WorkspaceRemovalFence = {
      async withRemovalFence<T>(): Promise<WorkspaceRemovalFenceResult<T>> {
        return { kind: "busy" }
      },
    }
    const runtime = { withRemovalFence: fence.withRemovalFence } as unknown as OpenCodeRuntime
    let currentRuntime: OpenCodeRuntime | null = null
    new RunnerSignalRClient("http://localhost:3456", "runner-1", "/runner", null, {
      openCodeRuntime: () => currentRuntime,
    })
    currentRuntime = runtime

    const handler = handlers.get("RemoveWorkspace")!
    const result = await handler({
      workflowRunId: "wr-1",
      gitUrl: "https://repo.test/mohist.git",
      workspacePath: "/runner/workspaces/wr-1",
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
