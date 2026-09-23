import { join } from 'node:path'
import type { CleanupPolicy } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { RunnerControlWebSocketClient } from '../server/runner-control-websocket.js'
import type { NamedWorkspaceRegistry } from './workspace-registry.js'
import { createNamedWorkspaceCleanupLoop, type NamedWorkspaceReclaimProbe } from './named-workspace-cleanup.js'
import type { OpenCodeRuntime } from './opencode/index.js'
import { formatDirectoryReclaimSummary } from './opencode/reclaim-summary.js'
import { runnerLogger } from '../system/logger.js'
import { runnerTransportDiagnostics } from '../server/connection-errors.js'
import { deleteDirectory, exists } from '../system/process.js'

const log = runnerLogger.child('host')
const cleanupLog = runnerLogger.child('cleanup')

export interface HostCleanupDeps {
  readonly runnerRoot: string
  readonly connection: ServerConnection
  readonly control: RunnerControlWebSocketClient
  readonly namedWorkspaceRegistry: NamedWorkspaceRegistry
  readonly namedWorkspaceReclaimProbe: NamedWorkspaceReclaimProbe
  readonly namedCleanupLoop: ReturnType<typeof createNamedWorkspaceCleanupLoop>
  readonly openCodeRuntime: () => OpenCodeRuntime | null
}

export function createHostCleanup(deps: HostCleanupDeps) {
  let lastCleanupPolicy: CleanupPolicy | null = null

  async function executeCleanupOnce(signal: AbortSignal): Promise<void> {
    try {
      // Legacy sweep for the retired managed-worktree concept: the
      // whole `<runnerRoot>/agent-workspaces/` tree is retired disk
      // data (no registry, no migration to Workspace entities) and is
      // removed here as ordinary disk-policy cleanup.
      const retiredDirectories = [
        join(deps.runnerRoot, 'agent-workspaces'),
        join(deps.runnerRoot, '.mohist', 'runner-state'),
      ]
      for (const path of retiredDirectories) {
        if (!exists(path)) continue
        await deleteDirectory(path)
        cleanupLog.info('removed retired Runner directory', { path })
      }
      const runtime = deps.openCodeRuntime()
      let blockedPaths = new Set<string>()
      if (runtime) {
        let reclaim: Awaited<ReturnType<OpenCodeRuntime['reclaimWhere']>>
        try {
          reclaim = await runtime.reclaimWhere((_directory, owner) => {
            const entry = owner ? deps.namedWorkspaceRegistry.findByWorkspacePath(owner) : null
            return entry?.phase === 'eligible' || entry?.phase === 'stuck'
          })
        } catch (error) {
          cleanupLog.error(
            'workspace cleanup runtime reclamation failed',
            runnerTransportDiagnostics(error, { includeCredentialGuidance: true }),
          )
          return
        }
        if (reclaim.candidates > 0)
          cleanupLog.info('workspace reclaim completed', {
            reason: formatDirectoryReclaimSummary(reclaim),
          })
        blockedPaths = new Set(reclaim.blockedDirectories)
      }
      const policy = await deps.connection.fetchConfig(signal)
      lastCleanupPolicy = policy
      // Named workspaces: server-authoritative lifecycle probe first
      // (archived or no active bound session → eligible), then the
      // named cleanup pass. Best-effort: a probe failure leaves entries
      // active and the next tick retries.
      try {
        const reclaim = await deps.namedWorkspaceReclaimProbe.runOnce(signal)
        if (reclaim.markedEligible > 0 || reclaim.deferred > 0 || reclaim.unobserved > 0) {
          cleanupLog.info('named workspace reclaim probe', {
            markedEligible: reclaim.markedEligible,
            deferred: reclaim.deferred,
            unobserved: reclaim.unobserved,
          })
        }
      } catch (error) {
        cleanupLog.warn(
          'named workspace reclaim probe failed',
          runnerTransportDiagnostics(error, { includeCredentialGuidance: true }),
        )
      }
      if (deps.namedWorkspaceRegistry.list().some((entry) => entry.phase === 'eligible')) {
        const namedResult = await deps.namedCleanupLoop.runOnce(policy, signal, blockedPaths)
        if (
          namedResult.retentionRemoved > 0 ||
          namedResult.budgetRemoved > 0 ||
          namedResult.guardAborted > 0 ||
          namedResult.stuckResolved > 0
        ) {
          cleanupLog.info('named workspace cleanup completed', {
            reason: `retention=${namedResult.retentionRemoved} budget=${namedResult.budgetRemoved} guardAborted=${namedResult.guardAborted} stuck=${namedResult.stuckResolved} usage=${namedResult.workspaceUsageBytes ?? 'unknown'}`,
          })
        }
      }
    } catch (error) {
      // Cleanup is best-effort; the next tick retries. fetchConfig failures
      // (network blip, server restart) flow through this same catch so the
      // loop stays resilient without a stale-policy fallback.
      cleanupLog.error(
        'workspace cleanup loop failed',
        runnerTransportDiagnostics(error, { includeCredentialGuidance: true }),
      )
    }
  }

  async function runSelfCheck(signal: AbortSignal) {
    if (signal.aborted) return
    const alive = await deps.control.probeLiveness(signal).catch(() => false)
    if (signal.aborted) return
    if (alive) return
    log.warn('dispatch liveness probe failed; forcing reconnect', {
      reason: 'liveness',
    })
    try {
      await deps.control.forceReconnect(signal)
    } catch (error) {
      log.error('forceReconnect failed', {
        ...runnerTransportDiagnostics(error, { includeCredentialGuidance: true }),
        reason: 'reconnect',
      })
    }
  }

  return { runCleanupOnce: executeCleanupOnce, runSelfCheck }
}
