import type { WorkItem } from "../../src/core/types.js"
import type { TaskLogger } from "../../src/runtime/task-log.js"
import type { WorkspaceManager, WorkspaceInfo } from "../../src/runtime/workspace.js"

// Test helper: build a `WorkspaceManager` mock that satisfies the contract
// used by `WorkExecutor`. Tests that just want to say "the workspace is
// ready, here it is" plug in `verifyOnlyWorkspaceManager(...)` so `prepare`
// returns the supplied workspace triple without touching git.
export function verifyOnlyWorkspaceManager(workspace: WorkspaceInfo, onPrepare?: (log: TaskLogger | null) => void): WorkspaceManager {
  const prepare = async (_work: WorkItem, _signal: AbortSignal, log: TaskLogger | null = null) => {
    onPrepare?.(log)
    return workspace
  }
  return { prepare } as unknown as WorkspaceManager
}
