// The server-invoked
// `RemoveWorkspace` SignalR method is registered through the
// free-function `registerWorkspaceRemovalHandler(conn, deps)` so
// the cluster's dependency surface is explicit and the handler can be
// exercised independently from the connection lifecycle.
//
// Behaviour preserves the SignalR reply contract while enforcing the
// registry-safety invariant:
//   - runner-root containment check (rejects `workspace_cleanup_refused`)
//   - paths outside runnerRoot never mutate the registry
//   - for in-root paths, path inspection, identity validation, deletion, and
//     registry mutation run inside one directory removal fence
//   - `workspace_missing` reply when the directory was already absent
//   - `workspace_cleanup_failed` reply carrying the error message on
//     delete failure
//   - reply shape `{ removed, status, path, reason, message }`
//
// The handler deps are minimised: `runnerRoot` (needed for the containment
// check), `registry` (for the consistent-entry drop), a late-bound removal
// fence, and `pathExists` (kept as an optional injection point, falling
// through to `existsSync` from `node:fs` when no test seam is supplied).

import { existsSync as defaultExistsSync } from 'node:fs'
import { resolve } from 'node:path'
import * as signalR from '@microsoft/signalr'
import { deleteDirectory } from '../system/process.js'
import { hasCompleteWorkspaceIdentity, isUnderRunnerRoot, type WorkspaceQuery } from '../runtime/workspace-query.js'
import type { WorkspaceRemovalFence } from '../runtime/workspace-removal-fence.js'
import type { WorkspaceRegistry } from '../runtime/workspace-registry.js'
import { issueWorkspacePath, validateWorkspaceIdentity, type IssueWorkspaceMarker } from '../runtime/workspace.js'
import { runnerLogger } from '../system/logger.js'
import { currentRunnerResources } from '../system/filesystem.js'

const log = runnerLogger.child('cleanup')

export interface WorkspaceRemovalHandlerDeps {
  runnerRoot: string
  registry?: WorkspaceRegistry | null
  pathExists?: typeof defaultExistsSync
  removalFence?: () => WorkspaceRemovalFence | null
}

export function createWorkspaceRemovalHandler(
  deps: WorkspaceRemovalHandlerDeps,
): (query: WorkspaceQuery) => Promise<unknown> {
  let handler: ((query: WorkspaceQuery) => Promise<unknown>) | undefined
  registerWorkspaceRemovalHandler(
    {
      on(_method: string, registered: (query: WorkspaceQuery) => Promise<unknown>) {
        handler = registered
      },
    } as signalR.HubConnection,
    deps,
  )
  return (query) => handler!(query)
}

export function registerWorkspaceRemovalHandler(conn: signalR.HubConnection, deps: WorkspaceRemovalHandlerDeps): void {
  const pathExists = deps.pathExists ?? currentRunnerResources()?.signalRExistsChecker ?? defaultExistsSync

  conn.on('RemoveWorkspace', async (query: WorkspaceQuery) => {
    if (!query?.workspacePath) {
      return removal(false, 'missing', query?.workspacePath ?? null, 'workspace_missing', 'Workspace already removed')
    }
    const workspacePath = resolve(query.workspacePath)
    if (!hasCompleteWorkspaceIdentity(query)) {
      return removal(
        false,
        'failed',
        workspacePath,
        'workspace_identity_mismatch',
        'Workspace query requires complete identity',
      )
    }
    if (!isUnderRunnerRoot(deps.runnerRoot, workspacePath)) {
      return removal(
        false,
        'failed',
        workspacePath,
        'workspace_cleanup_refused',
        'Workspace path is outside the runner-managed root',
      )
    }
    if (workspacePath !== issueWorkspacePath(deps.runnerRoot, query.workflowRunId)) {
      return removal(
        false,
        'failed',
        workspacePath,
        'workspace_cleanup_refused',
        'Workspace path does not belong to the workflow run',
      )
    }
    const removeWorkspace = async () => {
      if (!pathExists(workspacePath)) {
        await dropRegistryEntryForPath(deps.registry ?? null, workspacePath)
        return removal(false, 'missing', workspacePath, 'workspace_missing', 'Workspace already removed')
      }
      const expected: IssueWorkspaceMarker = {
        workflowRunId: query.workflowRunId,
        runBranch: query.branch,
      }
      try {
        await validateWorkspaceIdentity(
          workspacePath,
          expected,
          query.gitUrl,
          new AbortController().signal,
          null,
          deps.runnerRoot,
        )
      } catch (error) {
        return removal(
          false,
          'failed',
          workspacePath,
          'workspace_identity_mismatch',
          error instanceof Error ? error.message : String(error),
        )
      }
      try {
        await deleteDirectory(workspacePath)
        await dropRegistryEntryForPath(deps.registry ?? null, workspacePath)
        return removal(true, 'removed', workspacePath, null, 'Workspace removed')
      } catch (error) {
        return removal(
          false,
          'failed',
          workspacePath,
          'workspace_cleanup_failed',
          error instanceof Error ? error.message : String(error),
        )
      }
    }

    const fence = deps.removalFence?.() ?? null
    if (!fence) return await removeWorkspace()
    try {
      const result = await fence.withRemovalFence(workspacePath, removeWorkspace)
      if (result.kind === 'completed') return result.value
      const message =
        result.kind === 'busy'
          ? 'Workspace is busy and cannot be safely released'
          : 'Workspace cannot be safely released because the removal fence failed'
      return removal(false, 'failed', workspacePath, 'workspace_cleanup_failed', message)
    } catch (error) {
      return removal(
        false,
        'failed',
        workspacePath,
        'workspace_cleanup_failed',
        error instanceof Error ? error.message : String(error),
      )
    }
  })
}

// Drop the registry entry whose workspace path resolves to `workspacePath`.
// The caller invokes this only after the removal fence has admitted the
// directory callback. The entry is dropped regardless of whether the
// directory existed on disk — an already-missing directory is treated as
// removed and its entry deleted. `null` is accepted for the query branch
// with no path, where there is no registry identity to match.
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
    log.error('workspace registry remove failed', { path: workspacePath, run: entry.workflowRunId, exception: error })
  }
}

function removal(removed: boolean, status: string, path: string | null, reason: string | null, message: string) {
  return { removed, status, path, reason, message }
}
