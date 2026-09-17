import type { DispatchWorkItem } from '../../src/core/types.js'
import type { TaskLogger } from '../../src/runtime/task-log.js'
import type { WorkspacePreparer } from '../../src/runtime/executor.js'

export interface WorkspaceInfo {
  path: string
  branch?: string | null
}

// Test helper: build a `WorkspacePreparer` mock that satisfies the contract
// used by `WorkExecutor`. Tests that just want to say "the workspace is
// ready, here it is" plug in `verifyOnlyWorkspacePreparer(...)` so `prepare`
// returns the supplied workspace triple without touching git.
export function verifyOnlyWorkspacePreparer(
  workspace: WorkspaceInfo,
  onPrepare?: (log: TaskLogger | null) => void,
): WorkspacePreparer {
  const prepare = async (_work: DispatchWorkItem, _signal: AbortSignal, log: TaskLogger | null = null) => {
    onPrepare?.(log)
    return workspace
  }
  return { prepare }
}
