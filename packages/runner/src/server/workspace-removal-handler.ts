// Removal derives the path from Workspace identity and holds the Runner-owned
// use fence through eligibility, marker validation, deletion, and registry update.

import { existsSync as defaultExistsSync } from 'node:fs'
import { deleteDirectory } from '../system/process.js'
import { hasCompleteWorkspaceIdentity, isUnderRunnerRoot, type WorkspaceQuery } from '../runtime/workspace-query.js'
import type { WorkspaceRemovalFence } from '../runtime/workspace-removal-fence.js'
import { namedWorkspacePath, validateNamedWorkspaceIdentity } from '../runtime/workspace-entity.js'
import { namedWorkspaceRegistryKey, type NamedWorkspaceRegistry } from '../runtime/workspace-registry.js'
import { currentRunnerFileSystem, currentRunnerResources } from '../system/filesystem.js'
import { withManagedWorkspaceHandle } from '../runtime/workspace-managed.js'

export interface WorkspaceRemovalHandlerDeps {
  runnerRoot: string
  registry?: NamedWorkspaceRegistry | null
  pathExists?: typeof defaultExistsSync
  removalFence?: () => WorkspaceRemovalFence | null
  eligibleNow?: (projectId: string, workspaceName: string) => Promise<boolean>
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

export function createWorkspaceInspectionHandler(
  deps: WorkspaceRemovalHandlerDeps,
): (query: WorkspaceQuery) => Promise<unknown> {
  const pathExists = deps.pathExists ?? currentRunnerResources()?.controlExistsChecker ?? defaultExistsSync
  return async (query) => {
    if (!hasCompleteWorkspaceIdentity(query)) return { status: 'unsafe', reason: 'workspace_identity_mismatch' }
    const path = namedWorkspacePath(deps.runnerRoot, query.projectId, query.workspaceName)
    if (!isUnderRunnerRoot(deps.runnerRoot, path)) return { status: 'unsafe', reason: 'workspace_path_invalid' }
    const fence = deps.removalFence?.()
    if (!fence) return { status: 'unsafe', reason: 'removal_fence_unavailable' }
    const result = await fence.withRemovalFence(path, async () => {
      if (!pathExists(path)) return { status: 'already_absent', reason: null }
      try {
        await withManagedWorkspaceHandle(deps.runnerRoot, path, true, async (managedPath) => {
          await validateNamedWorkspaceIdentity(managedPath, query, new AbortController().signal)
        })
        return { status: 'present', reason: null }
      } catch {
        return { status: 'unsafe', reason: 'workspace_identity_mismatch' }
      }
    })
    if (result.kind === 'completed') return result.value
    return result.kind === 'busy'
      ? { status: 'in_use', reason: 'workspace_busy' }
      : { status: 'unsafe', reason: result.reason ?? 'workspace_inspection_unavailable' }
  }
}

function registerWorkspaceRemovalHandler(
  conn: { on(method: string, handler: (query: WorkspaceQuery) => Promise<unknown>): void },
  deps: WorkspaceRemovalHandlerDeps,
): void {
  const pathExists = deps.pathExists ?? currentRunnerResources()?.controlExistsChecker ?? defaultExistsSync

  conn.on('RemoveWorkspace', async (query: WorkspaceQuery) => {
    if (!hasCompleteWorkspaceIdentity(query)) {
      return removal(false, 'unsafe', null, 'workspace_identity_mismatch', 'Workspace query requires complete identity')
    }
    const workspacePath = namedWorkspacePath(deps.runnerRoot, query.projectId, query.workspaceName)
    if (!isUnderRunnerRoot(deps.runnerRoot, workspacePath)) {
      return removal(
        false,
        'unsafe',
        workspacePath,
        'workspace_cleanup_refused',
        'Workspace path is outside the runner-managed root',
      )
    }

    const fence = deps.removalFence?.() ?? null
    if (!fence) {
      return removal(
        false,
        'unsafe',
        workspacePath,
        'workspace_cleanup_refused',
        'Runner Workspace removal fence is unavailable',
      )
    }

    const removeWorkspace = async () => {
      if (deps.eligibleNow && !(await deps.eligibleNow(query.projectId!, query.workspaceName!))) {
        return removal(false, 'in_use', workspacePath, 'workspace_not_reclaimable', 'Workspace still needs its Home')
      }
      if (!pathExists(workspacePath)) {
        try {
          await dropNamedRegistryEntry(deps.registry ?? null, query.projectId, query.workspaceName)
        } catch {
          return removal(
            false,
            'unknown',
            workspacePath,
            'registry_update_unconfirmed',
            'Directory is absent but registry update was not confirmed',
          )
        }
        return removal(false, 'already_absent', workspacePath, 'workspace_missing', 'Workspace was already absent')
      }
      const removeAt = async (operationPath: string) => {
        try {
          await validateNamedWorkspaceIdentity(operationPath, query, new AbortController().signal)
        } catch (error) {
          return removal(
            false,
            'unsafe',
            workspacePath,
            'workspace_identity_mismatch',
            error instanceof Error ? error.message : String(error),
          )
        }
        try {
          await deleteDirectory(operationPath)
        } catch (error) {
          return removal(
            false,
            'deletion_failed',
            workspacePath,
            'workspace_cleanup_failed',
            error instanceof Error ? error.message : String(error),
          )
        }
        try {
          await dropNamedRegistryEntry(deps.registry ?? null, query.projectId, query.workspaceName)
        } catch {
          return removal(
            false,
            'unknown',
            workspacePath,
            'registry_update_unconfirmed',
            'Directory was deleted but registry update was not confirmed',
          )
        }
        return removal(true, 'removed', workspacePath, null, 'Workspace removed')
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
            'unsafe',
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
      return result.kind === 'busy'
        ? removal(
            false,
            'in_use',
            workspacePath,
            'workspace_busy',
            'Workspace is in use or removal is in progress; retry later',
          )
        : removal(
            false,
            'unsafe',
            workspacePath,
            result.reason ?? 'workspace_inspection_unavailable',
            result.reason === 'previous_generation_unconfirmed'
              ? 'Previous Runner generation work has no confirmed termination evidence'
              : 'Workspace use cannot be safely checked',
          )
    } catch (error) {
      return removal(
        false,
        'unsafe',
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
  await registry.remove(namedWorkspaceRegistryKey(projectId, workspaceName))
}

function removal(removed: boolean, status: string, path: string | null, reason: string | null, message: string) {
  return { removed, status, path, reason, message }
}
