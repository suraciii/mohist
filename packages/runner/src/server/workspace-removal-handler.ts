// Issue-313 T-008 / design P7 / D2 / D3 / D5: the server-invoked
// `RemoveWorkspace` SignalR method is extracted from `runner-signalr.ts`
// into a free-function `registerWorkspaceRemovalHandler(conn, deps)` so
// the cluster's dependency surface is explicit (D3) and so the manual
// workspace-removal handler can be exercised independently from the
// connection lifecycle / other push handlers.
//
// Behaviour preserves the SignalR reply contract while enforcing the
// issue-313 registry-safety invariant:
//   - runner-root containment check (rejects `workspace_cleanup_refused`)
//   - paths outside runnerRoot never mutate the registry
//   - for in-root paths, registry entry drop before disk deletion (so the
//     registry tracks disk reality: dropped regardless of whether the
//     directory exists)
//   - `workspace_missing` reply when the directory was already absent
//   - `workspace_cleanup_failed` reply carrying the error message on
//     delete failure
//   - reply shape `{ removed, status, path, reason, message }`
//
// The handler deps are minimised per design D3: `runnerRoot` (needed for
// the containment check), `registry` (for the consistent-entry drop),
// and `pathExists` (kept as an optional injection point, falls through
// to `existsSync` from `node:fs` so existing tests work unchanged).

import { existsSync as defaultExistsSync } from "node:fs"
import { resolve } from "node:path"
import * as signalR from "@microsoft/signalr"
import { deleteDirectory } from "../system/process.js"
import { hasCompleteWorkspaceIdentity, isUnderRunnerRoot, type WorkspaceQuery } from "../runtime/workspace-query.js"
import type { WorkspaceRegistry } from "../runtime/workspace-registry.js"
import { issueWorkspacePath, validateWorkspaceIdentity, type IssueWorkspaceMarker } from "../runtime/workspace.js"

export interface WorkspaceRemovalHandlerDeps {
  runnerRoot: string
  registry?: WorkspaceRegistry | null
  pathExists?: typeof defaultExistsSync
}

export function registerWorkspaceRemovalHandler(
  conn: signalR.HubConnection,
  deps: WorkspaceRemovalHandlerDeps,
): void {
  const pathExists = deps.pathExists ?? defaultExistsSync

  conn.on("RemoveWorkspace", async (query: WorkspaceQuery) => {
    if (!query?.workspacePath) {
      return removal(false, "missing", query?.workspacePath ?? null, "workspace_missing", "Workspace already removed")
    }
    const workspacePath = resolve(query.workspacePath)
    if (!hasCompleteWorkspaceIdentity(query)) {
      return removal(false, "failed", workspacePath, "workspace_identity_mismatch", "Workspace query requires complete identity")
    }
    if (!isUnderRunnerRoot(deps.runnerRoot, workspacePath)) {
      return removal(false, "failed", workspacePath, "workspace_cleanup_refused", "Workspace path is outside the runner-managed root")
    }
    if (workspacePath !== issueWorkspacePath(deps.runnerRoot, query.workflowRunId)) {
      return removal(false, "failed", workspacePath, "workspace_cleanup_refused", "Workspace path does not belong to the workflow run")
    }
    if (!pathExists(workspacePath)) {
      await dropRegistryEntryForPath(deps.registry ?? null, workspacePath)
      return removal(false, "missing", workspacePath, "workspace_missing", "Workspace already removed")
    }
    const expected: IssueWorkspaceMarker = {
      workflowRunId: query.workflowRunId,
      runBranch: query.branch,
    }
    try {
      await validateWorkspaceIdentity(workspacePath, expected, query.gitUrl, new AbortController().signal, null, deps.runnerRoot)
    } catch (error) {
      return removal(false, "failed", workspacePath, "workspace_identity_mismatch", error instanceof Error ? error.message : String(error))
    }
    try {
      await deleteDirectory(workspacePath)
      await dropRegistryEntryForPath(deps.registry ?? null, workspacePath)
      return removal(true, "removed", workspacePath, null, "Workspace removed")
    } catch (error) {
      return removal(false, "failed", workspacePath, "workspace_cleanup_failed", error instanceof Error ? error.message : String(error))
    }
  })
}

// Drop the registry entry whose workspace path resolves to
// `workspacePath`. Called by the manual RemoveWorkspace handler so the
// registry stays consistent with disk reality: the entry is dropped
// regardless of whether the directory existed on disk, matching the
// T-002 contract "safeRemove must tolerate an already-missing
// directory (treat as removed, delete the entry)". `null` is accepted
// to cover the "query.workspacePath missing" branch — there is no path
// to match, so the registry is left untouched.
async function dropRegistryEntryForPath(
  registry: WorkspaceRegistry | null,
  workspacePath: string | null,
): Promise<void> {
  if (!registry || !workspacePath) return
  const entry = registry.findByWorkspacePath(workspacePath)
  if (!entry) return
  try {
    await registry.remove(entry.workflowRunId)
  } catch (error) {
    console.error("workspace registry remove failed:", error)
  }
}

function removal(removed: boolean, status: string, path: string | null, reason: string | null, message: string) {
  return { removed, status, path, reason, message }
}
