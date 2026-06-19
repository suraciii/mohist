import type { WorkItem } from "../../core/types.js"
import type { WorkspaceManager, WorkspaceInfo } from "../../runtime/workspace.js"

// Test helper: build a `WorkspaceManager` mock that satisfies the
// post-T-002 contract used by `WorkExecutor`. Tests that just want to
// say "the workspace is ready, here it is" plug in
// `verifyOnlyWorkspaceManager(...)` so:
//   - `planResolution` returns "verify" (the workspace is treated as
//     already materialized for the duration of the test).
//   - `verify` returns the supplied workspace triple.
//   - `materialize` is intentionally NOT provided — any test that
//     accidentally forces the materialize path fails loudly rather
//     than silently invoking a real git clone.
//
// Tests that intentionally exercise the materialize / start-boundary
// precheck paths should construct a real `WorkspaceManager` (or a
// purpose-built mock with `materialize` overridden).
export function verifyOnlyWorkspaceManager(workspace: WorkspaceInfo): WorkspaceManager {
  const planResolution = async (_work: WorkItem, _signal: AbortSignal) => ({
    action: "verify" as const,
    workspacePath: workspace.path,
  })
  const verify = async (_work: WorkItem, _signal: AbortSignal) => workspace
  const ensure = async (_work: WorkItem, _signal: AbortSignal) => workspace
  return { planResolution, verify, ensure } as unknown as WorkspaceManager
}
