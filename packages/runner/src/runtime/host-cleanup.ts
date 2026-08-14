import { join } from 'node:path'
import type { CleanupPolicy } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { RunnerSignalRClient } from '../server/runner-signalr.js'
import type { WorkspaceRegistry, NamedWorkspaceRegistry } from './workspace-registry.js'
import { createNamedWorkspaceCleanupLoop, type NamedWorkspaceReclaimProbe } from './named-workspace-cleanup.js'
import type { ConvergenceBackstop } from './cleanup-convergence.js'
import type { BindingConvergence } from './binding-convergence.js'
import type { CleanupLoop } from './cleanup-loop.js'
import type { OpenCodeRuntime } from './opencode/index.js'
import { formatDirectoryReclaimSummary } from './opencode/reclaim-summary.js'
import { runnerLogger } from '../system/logger.js'
import { deleteDirectory, exists } from '../system/process.js'

const log = runnerLogger.child('host')
const cleanupLog = runnerLogger.child('cleanup')

export interface HostCleanupDeps {
  readonly runnerRoot: string
  readonly connection: ServerConnection
  readonly signalR: RunnerSignalRClient
  readonly workspaceRegistry: WorkspaceRegistry
  readonly namedWorkspaceRegistry: NamedWorkspaceRegistry
  readonly namedWorkspaceReclaimProbe: NamedWorkspaceReclaimProbe
  readonly namedCleanupLoop: ReturnType<typeof createNamedWorkspaceCleanupLoop>
  readonly cleanupLoop: CleanupLoop
  readonly convergence: ConvergenceBackstop
  readonly bindingConvergence: BindingConvergence
  readonly openCodeRuntime: () => OpenCodeRuntime | null
}

export function createHostCleanup(deps: HostCleanupDeps) {
  let cleanupInFlight: Promise<void> | null = null
  let lastCleanupPolicy: CleanupPolicy | null = null

  async function runConvergenceOnce(signal: AbortSignal): Promise<void> {
    try {
      await deps.convergence.runOnce(signal)
    } catch (error) {
      // Convergence is best-effort; the next tick or reconnect retries.
      cleanupLog.error('workspace cleanup convergence pass failed', { exception: error })
    }
  }

  async function runBindingConvergenceOnce(signal: AbortSignal): Promise<void> {
    if (
      typeof (deps.connection as { listAgentSessionsForReconcile?: unknown }).listAgentSessionsForReconcile !==
      'function'
    )
      return
    try {
      await deps.bindingConvergence.runOnce(signal)
    } catch (error) {
      log.error('agent-session binding convergence pass failed', { exception: error, session: 'binding' })
    }
  }

  function runCleanupOnce(signal: AbortSignal): Promise<void> {
    if (cleanupInFlight) return cleanupInFlight
    const pass = executeCleanupOnce(signal)
    cleanupInFlight = pass
    void pass.finally(() => {
      if (cleanupInFlight === pass) cleanupInFlight = null
    })
    return pass
  }

  async function executeCleanupOnce(signal: AbortSignal): Promise<void> {
    try {
      // Legacy sweep for the retired managed-worktree concept: the
      // whole `<runnerRoot>/agent-workspaces/` tree is retired disk
      // data (no registry, no migration to Workspace entities) and is
      // removed here as ordinary disk-policy cleanup.
      const legacyAgentWorkspaces = join(deps.runnerRoot, 'agent-workspaces')
      if (exists(legacyAgentWorkspaces)) {
        await deleteDirectory(legacyAgentWorkspaces)
        cleanupLog.info('removed retired agent-workspaces directory', { path: legacyAgentWorkspaces })
      }
      const runtime = deps.openCodeRuntime()
      let blockedPaths = new Set<string>()
      if (runtime) {
        let reclaim: Awaited<ReturnType<OpenCodeRuntime['reclaimWhere']>>
        try {
          reclaim = await runtime.reclaimWhere((directory) => {
            const entry = deps.workspaceRegistry.findByWorkspacePath(directory)
            return entry?.phase === 'eligible' || entry?.phase === 'stuck'
          })
        } catch (error) {
          cleanupLog.error('workspace cleanup runtime reclamation failed', { exception: error })
          return
        }
        if (reclaim.candidates > 0)
          cleanupLog.info('workspace reclaim completed', { reason: formatDirectoryReclaimSummary(reclaim) })
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
        cleanupLog.warn('named workspace reclaim probe failed', { exception: error })
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
      const result = await deps.cleanupLoop.runOnce(policy, signal, blockedPaths)
      if (
        result.retentionRemoved > 0 ||
        result.budgetRemoved > 0 ||
        result.guardAborted > 0 ||
        result.stuckResolved > 0
      ) {
        cleanupLog.info('workspace cleanup completed', {
          reason: `retention=${result.retentionRemoved} budget=${result.budgetRemoved} guardAborted=${result.guardAborted} stuck=${result.stuckResolved} usage=${result.workspaceUsageBytes ?? 'unknown'}`,
        })
      }
    } catch (error) {
      // Cleanup is best-effort; the next tick retries. fetchConfig failures
      // (network blip, server restart) flow through this same catch so the
      // loop stays resilient without a stale-policy fallback.
      cleanupLog.error('workspace cleanup loop failed', { exception: error })
    }
  }

  async function runSelfCheck(signal: AbortSignal) {
    if (signal.aborted) return
    const alive = await deps.signalR.probeLiveness(signal).catch(() => false)
    if (signal.aborted) return
    if (alive) return
    log.warn('dispatch liveness probe failed; forcing reconnect', { reason: 'liveness' })
    try {
      await deps.signalR.forceReconnect(signal)
    } catch (error) {
      log.error('forceReconnect failed', { exception: error, reason: 'reconnect' })
    }
  }

  return { runConvergenceOnce, runBindingConvergenceOnce, runCleanupOnce, runSelfCheck }
}
