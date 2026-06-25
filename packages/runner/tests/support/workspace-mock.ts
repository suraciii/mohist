import type { WorkItem } from "../../core/types.js"
import type { WorkspaceManager, WorkspaceInfo } from "../../runtime/workspace.js"

// Test helper: build a `WorkspaceManager` mock that satisfies the contract
// used by `WorkExecutor`. Tests that just want to say "the workspace is
// ready, here it is" plug in `verifyOnlyWorkspaceManager(...)` so `prepare`
// returns the supplied workspace triple without touching git.
export function verifyOnlyWorkspaceManager(workspace: WorkspaceInfo): WorkspaceManager {
  const prepare = async (_work: WorkItem, _signal: AbortSignal) => workspace
  return { prepare } as unknown as WorkspaceManager
}
