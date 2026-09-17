// The server-invoked
// `RemoveWorkspace` control WebSocket method is registered through the
// free-function `registerWorkspaceRemovalHandler(conn, deps)` so
// the cluster's dependency surface is explicit and the handler can be
// exercised independently from the connection lifecycle.
//
// Behaviour preserves the control WebSocket reply contract while enforcing the
// Named Workspace safety invariants:
//   - identity is `(projectId, workspaceName)`; the runner derives the
//     directory from `namedWorkspacePath(runnerRoot, ...)` and never trusts a
//     Server-supplied path
//   - incomplete identity is refused before any delete
//   - the named marker (`projectId`, `workspaceName`, repository origin) is
//     validated inside one directory removal fence before deletion
//   - the Runtime removal fence is mandatory: without it the handler refuses
//     rather than deleting outside the Runtime lifecycle
//   - registry mutation drops only the matching named entry
//   - `workspace_missing` reply when the directory was already absent
//   - `workspace_cleanup_failed` reply carrying the error message on
//     delete failure
//   - reply shape `{ removed, status, path, reason, message }`

import { existsSync as defaultExistsSync } from 'node:fs'
import { deleteDirectory } from '../system/process.js'
import { hasCompleteWorkspaceIdentity, isUnderRunnerRoot, type WorkspaceQuery } from '../runtime/workspace-query.js'
import type { WorkspaceRemovalFence } from '../runtime/workspace-removal-fence.js'
import { namedWorkspacePath, validateNamedWorkspaceIdentity } from '../runtime/workspace-entity.js'
import { namedWorkspaceRegistryKey, type NamedWorkspaceRegistry } from '../runtime/workspace-registry.js'
import { runnerLogger } from '../system/logger.js'
import { currentRunnerFileSystem, currentRunnerResources } from '../system/filesystem.js'
import { withManagedWorkspaceHandle } from '../runtime/workspace-managed.js'

const log = runnerLogger.child('cleanup')

export interface WorkspaceRemovalHandlerDeps {
  runnerRoot: string
  registry?: NamedWorkspaceRegistry | null
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
    },
    deps,
  )
  return (query) => handler!(query)
}

function registerWorkspaceRemovalHandler(
  conn: { on(method: string, handler: (query: WorkspaceQuery) => Promise<unknown>): void },
  deps: WorkspaceRemovalHandlerDeps,
): void {
  const pathExists = deps.pathExists ?? currentRunnerResources()?.controlExistsChecker ?? defaultExistsSync

  conn.on('RemoveWorkspace', async (query: WorkspaceQuery) => {
    if (!hasCompleteWorkspaceIdentity(query)) {
      return removal(false, 'failed', null, 'workspace_identity_mismatch', 'Workspace query requires complete identity')
    }
    const workspacePath = namedWorkspacePath(deps.runnerRoot, query.projectId, query.workspaceName)
    if (!isUnderRunnerRoot(deps.runnerRoot, workspacePath)) {
      return removal(
        false,
        'failed',
        workspacePath,
        'workspace_cleanup_refused',
        'Workspace path is outside the runner-managed root',
      )
    }

    const fence = deps.removalFence?.() ?? null
    if (!fence) {
      return removal(
        false,
        'failed',
        workspacePath,
        'workspace_cleanup_refused',
        'Runtime removal fence is unavailable',
      )
    }

    const removeWorkspace = async () => {
      if (!pathExists(workspacePath)) {
        await dropNamedRegistryEntry(deps.registry ?? null, query.projectId, query.workspaceName)
        return removal(false, 'missing', workspacePath, 'workspace_missing', 'Workspace already removed')
      }
      const removeAt = async (operationPath: string) => {
        try {
          await validateNamedWorkspaceIdentity(operationPath, query, new AbortController().signal)
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
          await deleteDirectory(operationPath)
          await dropNamedRegistryEntry(deps.registry ?? null, query.projectId, query.workspaceName)
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
      const fileSystem = currentRunnerFileSystem()
      if (
        !deps.pathExists &&
        process.platform === 'linux' &&
        fileSystem.supportsDirectoryHandles &&
        fileSystem.openDirectory
      ) {
        try {
          return await withManagedWorkspaceHandle(
            deps.runnerRoot,
            workspacePath,
            true,
            async (managedPath) => await removeAt(managedPath),
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
      }
      return await removeAt(workspacePath)
    }

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

// Drop the named registry entry identified by `(projectId, workspaceName)`.
// The caller invokes this only after the removal fence has admitted the
// directory callback. The entry is dropped regardless of whether the
// directory existed on disk — an already-missing directory is treated as
// removed and its entry deleted.
async function dropNamedRegistryEntry(
  registry: NamedWorkspaceRegistry | null,
  projectId: string,
  workspaceName: string,
): Promise<void> {
  if (!registry) return
  const entry = registry.get(projectId, workspaceName)
  if (!entry) return
  try {
    await registry.remove(namedWorkspaceRegistryKey(projectId, workspaceName))
  } catch (error) {
    log.error('named workspace registry remove failed', { workspace: workspaceName, exception: error })
  }
}

function removal(removed: boolean, status: string, path: string | null, reason: string | null, message: string) {
  return { removed, status, path, reason, message }
}
