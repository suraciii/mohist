import { resolve } from 'node:path'
import { isUnderRunnerRoot } from './workspace-query.js'
import { namedWorkspacePath, readNamedWorkspaceMarker } from './workspace-entity.js'
import { withManagedWorkspaceHandle } from './workspace-managed.js'
import { WorkspaceIdentityMismatchError } from './workspace-errors.js'
import { deleteDirectory } from '../system/process.js'
import { runnerLogger } from '../system/logger.js'
import { currentRunnerFileSystem } from '../system/filesystem.js'
import { CleanupLoop, type CleanupEntry, type CleanupRunner } from './cleanup-loop.js'
import type { NamedWorkspaceRegistry, NamedWorkspaceRegistryEntry } from './workspace-registry.js'
import type { WorkspaceRemovalFence } from './workspace-removal-fence.js'
import type { ServerConnection } from '../server/connection.js'
import { runnerTransportDiagnostics } from '../server/connection-errors.js'

const log = runnerLogger.child('named-cleanup')

// Cleanup runner for the named-workspace dimension. Disk identity is
// the named-workspace marker (`ws:<projectId>:<workspaceName>`), which
// matches the registry key exactly, so the shared guard pass in
// CleanupLoop applies unchanged. A directory whose marker is missing or
// mismatched is deterministically refused (→ stuck) exactly like the
// workflow dimension.
export class NamedWorkspaceCleanupRunner implements CleanupRunner {
  constructor(
    private readonly runnerRoot: string,
    private readonly registry: NamedWorkspaceRegistry | null = null,
    private readonly eligibleNow?: (projectId: string, workspaceName: string) => Promise<boolean>,
  ) {}

  isUnderRunnerRoot(root: string, candidate: string): boolean {
    return isUnderRunnerRoot(root, candidate)
  }

  pathExists(path: string): boolean {
    return currentRunnerFileSystem().exists(path)
  }

  async readWorkspaceIdentity(workspacePath: string): Promise<string | null | undefined> {
    const marker = await readNamedWorkspaceMarker(workspacePath)
    if (!marker) return null
    return `ws:${marker.projectId}:${marker.workspaceName}`
  }

  async deleteDirectory(path: string): Promise<void> {
    await deleteDirectory(path)
  }

  async computeDirectorySize(path: string, signal: AbortSignal): Promise<number | null> {
    try {
      const { runCommand } = await import('../system/process.js')
      const result = await runCommand('du', ['-sb', path], '.', signal)
      if (result.exitCode !== 0) return null
      const match = result.stdout.match(/^(\d+)/)
      if (!match) return null
      return parseInt(match[1], 10)
    } catch {
      return null
    }
  }

  async validateWorkspace(entry: NamedWorkspaceRegistryEntry): Promise<boolean> {
    if (this.eligibleNow && !(await this.eligibleNow(entry.projectId, entry.workspaceName))) return false
    return await this.withValidWorkspace(entry, async () => true)
  }

  async validateAndDeleteWorkspace(entry: NamedWorkspaceRegistryEntry): Promise<boolean> {
    if (this.eligibleNow && !(await this.eligibleNow(entry.projectId, entry.workspaceName))) return false
    return await this.withValidWorkspace(entry, async (workspacePath) => {
      await deleteDirectory(workspacePath)
      return true
    })
  }

  private async withValidWorkspace(
    entry: NamedWorkspaceRegistryEntry,
    operation: (workspacePath: string) => Promise<boolean>,
  ): Promise<boolean> {
    if (entry.workspacePath !== namedWorkspacePath(this.runnerRoot, entry.projectId, entry.workspaceName)) return false
    try {
      return await withManagedWorkspaceHandle(this.runnerRoot, entry.workspacePath, true, async (heldPath) => {
        const marker = await readNamedWorkspaceMarker(heldPath)
        if (!marker || marker.projectId !== entry.projectId || marker.workspaceName !== entry.workspaceName)
          return false
        return await operation(heldPath)
      })
    } catch (error) {
      if (error instanceof WorkspaceIdentityMismatchError) return false
      throw error
    }
  }
}

export interface NamedWorkspaceReclaimProbeResult {
  // Active entries promoted to eligible this tick (archived, or no
  // active bound session on the server).
  markedEligible: number
  // Active entries kept active because the server reports at least one
  // active bound session.
  deferred: number
  // Active entries the server could not be asked about this tick
  // (transport error) — left active, retried next tick.
  unobserved: number
}

// Server eligibility includes bound Sessions and Workflows that still need Home.
// A failed probe leaves the entry active for a later pass.
export class NamedWorkspaceReclaimProbe {
  constructor(
    private readonly registry: NamedWorkspaceRegistry,
    private readonly connection: ServerConnection,
  ) {}

  async runOnce(signal: AbortSignal): Promise<NamedWorkspaceReclaimProbeResult> {
    const result: NamedWorkspaceReclaimProbeResult = { markedEligible: 0, deferred: 0, unobserved: 0 }
    for (const entry of this.registry.list()) {
      if (signal.aborted) break
      let info: Awaited<ReturnType<ServerConnection['getWorkspaceReclaimability']>>
      try {
        info = await this.connection.getWorkspaceReclaimability(entry.projectId, entry.workspaceName, signal)
      } catch (error) {
        log.warn('named workspace reclaimability probe failed', {
          workspace: entry.workspaceName,
          ...runnerTransportDiagnostics(error, { includeCredentialGuidance: true }),
        })
        result.unobserved++
        continue
      }
      if (info.reclaimable) {
        const promoted = await this.registry.markEligible(entry.projectId, entry.workspaceName)
        if (entry.phase !== 'eligible' && promoted?.phase === 'eligible') {
          log.info('named workspace reclaimable', {
            workspace: entry.workspaceName,
            reason: info.status === 'archived' ? 'archived' : 'no active bound session',
          })
          result.markedEligible++
        }
      } else {
        await this.registry.markActive(entry.projectId, entry.workspaceName)
        result.deferred++
      }
    }
    return result
  }
}

// Convenience factory so host wiring builds the named cleanup loop
// with the same seam as the other dimensions.
export function createNamedWorkspaceCleanupLoop(
  registry: NamedWorkspaceRegistry,
  runnerRoot: string,
  removalFence: () => WorkspaceRemovalFence | null = () => null,
  eligibleNow?: (projectId: string, workspaceName: string) => Promise<boolean>,
  reportOutcome?: (
    entry: NamedWorkspaceRegistryEntry,
    outcome: 'removed' | 'already_absent' | 'in_use' | 'unsafe' | 'deletion_failed',
    reason?: string,
  ) => Promise<void>,
) {
  return new CleanupLoop<NamedWorkspaceRegistryEntry>(
    registry,
    new NamedWorkspaceCleanupRunner(runnerRoot, registry, eligibleNow),
    runnerRoot,
    removalFence,
    reportOutcome,
  )
}

export type { CleanupEntry }
