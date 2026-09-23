import { runnerLogger } from '../system/logger.js'
import type { CleanupPolicy } from '../core/types.js'
import type { WorkspaceRegistryPhase } from './workspace-registry.js'
import type { WorkspaceRemovalFence } from './workspace-removal-fence.js'

const log = runnerLogger.child('cleanup')

// The maintenance loop is shared by the workflow-workspace and
// named-workspace cleanup runners: both reuse the same phase model,
// tick, single-flight constraint, storage budget and removal fence.
// `CleanupEntry` is the common shape; the registry supplies its own
// key and phase transitions.

export interface CleanupEntry {
  workspacePath: string
  phase: WorkspaceRegistryPhase
  terminalAt: string | null
}

export interface CleanupRegistry<E extends CleanupEntry> {
  list(): E[]
  entryKey(entry: E): string
  markStuck(key: string): Promise<E | null>
  remove(key: string): Promise<boolean>
}

export interface CleanupRunner {
  isUnderRunnerRoot(root: string, candidate: string): boolean
  pathExists(path: string): boolean
  // Disk identity probe: the stable identity recorded on disk at
  // `workspacePath` (workflow marker run id, or child session id
  // derived from the worktree's git backing). `null`/`undefined` means
  // the path is not a valid workspace of the owning kind.
  readWorkspaceIdentity(workspacePath: string): Promise<string | null | undefined>
  deleteDirectory(path: string): Promise<void>
  computeDirectorySize(path: string, signal: AbortSignal): Promise<number | null>
  validateWorkspace?(entry: CleanupEntry): Promise<boolean>
  validateAndDeleteWorkspace?(entry: CleanupEntry, onDeleteStarted?: () => void): Promise<boolean>
}

export interface CleanupLoopResult {
  retentionRemoved: number
  budgetRemoved: number
  guardAborted: number
  // Eligible entries resolved to the terminal `stuck` phase this tick
  // because a pre-delete guard deterministically refused them. Such an
  // entry leaves the eligible set, so it is neither re-evaluated nor
  // re-warned on subsequent ticks.
  stuckResolved: number
  workspaceUsageBytes: number | null
}

export class CleanupLoop<E extends CleanupEntry = CleanupEntry> {
  private usageCache: { bytes: number; timestamp: number } | null = null
  private readonly usageCacheTtlMs = 5 * 60_000

  constructor(
    private readonly registry: CleanupRegistry<E>,
    private readonly runner: CleanupRunner,
    private readonly runnerRoot: string,
    private readonly removalFence: () => WorkspaceRemovalFence | null = () => null,
    private readonly reportOutcome?: (
      entry: E,
      outcome: 'removed' | 'already_absent' | 'in_use' | 'unsafe' | 'deletion_failed',
      reason?: string,
    ) => Promise<void>,
  ) {}

  async runOnce(
    policy: CleanupPolicy | null | undefined,
    signal: AbortSignal,
    blockedPaths: ReadonlySet<string> = new Set(),
  ): Promise<CleanupLoopResult> {
    const result: CleanupLoopResult = {
      retentionRemoved: 0,
      budgetRemoved: 0,
      guardAborted: 0,
      stuckResolved: 0,
      workspaceUsageBytes: null,
    }

    if (signal.aborted) return result
    if (!policy) return result

    const initialEligible = this.registry.list().filter((e) => e.phase === 'eligible')
    if (initialEligible.length === 0) return result

    if (signal.aborted) return result

    // Resolution pass: give a guard-refused eligible entry a
    // deterministic exit so it is not re-evaluated and re-warned every
    // tick. A guard refusal is deterministic (identical outcome on every
    // tick), so it MUST NOT be retried indefinitely. This pass runs
    // before the disabled-policy early-return, so resolution is
    // independent of retention/budget — a stuck entry is resolved even
    // when both policies are disabled. The directory is never deleted
    // here: the safety refusal stands; only the registry's repeated
    // re-evaluation ends.
    for (const entry of initialEligible) {
      if (signal.aborted) break
      if (blockedPaths.has(entry.workspacePath)) continue
      let verdict: Awaited<ReturnType<typeof this.evaluateGuards>>
      try {
        if (!this.runner.pathExists(entry.workspacePath)) continue
        verdict = await this.evaluateGuards(entry)
      } catch (error) {
        await this.reportOutcome?.(entry, 'unsafe', errorReason(error)).catch(() => undefined)
        continue
      }
      if (verdict.ok) continue
      if (verdict.message === 'workspace identity is missing or unreadable') {
        await this.reportOutcome?.(entry, 'unsafe', verdict.message).catch(() => undefined)
        continue
      }
      log.warn('workspace cleanup refused', {
        run: this.registry.entryKey(entry),
        path: entry.workspacePath,
        reason: verdict.message,
      })
      await this.reportOutcome?.(entry, 'unsafe', verdict.message).catch(() => undefined)
      await this.registry.markStuck(this.registry.entryKey(entry))
      result.stuckResolved++
    }
    if (signal.aborted) return result

    const retentionDisabled = policy.retentionDays == null || policy.retentionDays <= 0
    const budgetDisabled = policy.storageBudgetBytes == null || policy.storageBudgetBytes <= 0

    if (retentionDisabled && budgetDisabled) return result

    // Re-list after resolution: entries marked `stuck` above have left
    // the eligible set, so the eviction passes only see entries whose
    // guards passed (plus any that flipped back to eligible in a race).
    const reList = () => this.registry.list().filter((e) => e.phase === 'eligible')

    if (!retentionDisabled) {
      const removable = reList()
      const cutoff = Date.now() - policy.retentionDays! * 24 * 60 * 60 * 1000
      for (const entry of removable) {
        if (signal.aborted) break
        if (!entry.terminalAt) continue
        if (new Date(entry.terminalAt).getTime() > cutoff) continue
        const removed = await this.safeRemove(entry, blockedPaths)
        if (removed) result.retentionRemoved++
        else result.guardAborted++
      }
    }

    if (!budgetDisabled && !signal.aborted) {
      const remaining = reList()
      if (remaining.length === 0) {
        result.workspaceUsageBytes = await this.getWorkspaceUsage(signal)
        return result
      }

      result.workspaceUsageBytes = await this.getWorkspaceUsage(signal)
      if (result.workspaceUsageBytes == null) return result
      if (result.workspaceUsageBytes <= policy.storageBudgetBytes!) return result

      const targetWatermark = policy.storageTargetWatermarkBytes ?? Math.floor(policy.storageBudgetBytes! * 0.7)
      if (targetWatermark < 0 || targetWatermark >= policy.storageBudgetBytes!) {
        log.warn('workspace cleanup refused invalid storage target', { reason: String(targetWatermark) })
        return result
      }
      const sorted = [...remaining].sort((a, b) => {
        if (!a.terminalAt && !b.terminalAt) return 0
        if (!a.terminalAt) return 1
        if (!b.terminalAt) return -1
        return a.terminalAt.localeCompare(b.terminalAt)
      })

      let currentUsage = result.workspaceUsageBytes
      for (const entry of sorted) {
        if (signal.aborted) break
        if (currentUsage <= targetWatermark) break

        const entrySize = await this.runner.computeDirectorySize(entry.workspacePath, signal)
        const removed = await this.safeRemove(entry, blockedPaths)
        if (removed) {
          result.budgetRemoved++
          if (entrySize != null && entrySize > 0) currentUsage -= entrySize
        } else result.guardAborted++
      }
    }

    return result
  }

  private async getWorkspaceUsage(signal: AbortSignal): Promise<number | null> {
    if (this.usageCache && Date.now() - this.usageCache.timestamp < this.usageCacheTtlMs) {
      return this.usageCache.bytes
    }
    const bytes = await this.runner.computeDirectorySize(this.runnerRoot, signal)
    if (bytes != null) {
      this.usageCache = { bytes, timestamp: Date.now() }
    }
    return bytes
  }

  // The three pre-delete safety guards, shared by the resolution pass
  // and `safeRemove`. Returns the refusal reason without logging so the
  // caller controls warning emission. `safeRemove` keeps re-checking as
  // a defensive race backstop (a marker could be deleted between the
  // resolution pass and the eviction pass); in normal operation a guard
  // refusal is already resolved to `stuck` before eviction, so this
  // branch is unreachable outside that rare race.
  private async evaluateGuards(entry: E): Promise<{ ok: true } | { ok: false; message: string }> {
    if (!this.runner.isUnderRunnerRoot(this.runnerRoot, entry.workspacePath)) {
      return { ok: false, message: 'path is outside runnerRoot' }
    }
    const diskIdentity = await this.runner.readWorkspaceIdentity(entry.workspacePath)
    if (!diskIdentity) {
      return { ok: false, message: 'workspace identity is missing or unreadable' }
    }
    const expected = this.registry.entryKey(entry)
    if (diskIdentity !== expected) {
      return {
        ok: false,
        message: `workspace identity (${diskIdentity}) does not match registry (${expected})`,
      }
    }
    return { ok: true }
  }

  async safeRemove(entry: E, blockedPaths: ReadonlySet<string> = new Set()): Promise<boolean> {
    if (blockedPaths.has(entry.workspacePath)) {
      await this.reportOutcome?.(entry, 'in_use', 'runtime_resource_busy').catch(() => undefined)
      return false
    }
    const fence = this.removalFence()
    if (!fence) {
      await this.reportOutcome?.(entry, 'unsafe', 'removal_fence_unavailable').catch(() => undefined)
      return false
    }
    const remove = async (): Promise<boolean> => {
      let present: boolean
      try {
        present = this.runner.pathExists(entry.workspacePath)
      } catch (error) {
        await this.reportOutcome?.(entry, 'unsafe', errorReason(error)).catch(() => undefined)
        return false
      }
      if (!present) {
        if (!this.runner.isUnderRunnerRoot(this.runnerRoot, entry.workspacePath)) {
          await this.reportOutcome?.(entry, 'unsafe', 'path_outside_runner_root').catch(() => undefined)
          return false
        }
        await this.reportOutcome?.(entry, 'already_absent')
        await this.registry.remove(this.registry.entryKey(entry))
        return true
      }
      let verdict: Awaited<ReturnType<typeof this.evaluateGuards>>
      try {
        verdict = await this.evaluateGuards(entry)
      } catch (error) {
        await this.reportOutcome?.(entry, 'unsafe', errorReason(error)).catch(() => undefined)
        return false
      }
      if (!verdict.ok) {
        log.warn('workspace cleanup refused', {
          run: this.registry.entryKey(entry),
          path: entry.workspacePath,
          reason: verdict.message,
        })
        await this.reportOutcome?.(entry, 'unsafe', verdict.message).catch(() => undefined)
        return false
      }

      if (this.runner.validateAndDeleteWorkspace) {
        let deleted: boolean
        let deleteStarted = false
        try {
          deleted = await this.runner.validateAndDeleteWorkspace(entry, () => {
            deleteStarted = true
          })
        } catch (error) {
          await this.reportOutcome?.(entry, deleteStarted ? 'deletion_failed' : 'unsafe', errorReason(error)).catch(
            () => undefined,
          )
          throw error
        }
        if (!deleted) {
          log.warn('workspace cleanup refused', {
            run: this.registry.entryKey(entry),
            path: entry.workspacePath,
            reason: 'workspace identity is invalid',
          })
          await this.reportOutcome?.(entry, 'unsafe', 'workspace_identity_or_eligibility_invalid').catch(
            () => undefined,
          )
          return false
        }
        await this.reportOutcome?.(entry, 'removed')
        await this.registry.remove(this.registry.entryKey(entry))
        return true
      }

      let valid = true
      try {
        if (this.runner.validateWorkspace) valid = await this.runner.validateWorkspace(entry)
      } catch (error) {
        await this.reportOutcome?.(entry, 'unsafe', errorReason(error)).catch(() => undefined)
        return false
      }
      if (!valid) {
        log.warn('workspace cleanup refused', {
          run: this.registry.entryKey(entry),
          path: entry.workspacePath,
          reason: 'workspace identity is invalid',
        })
        await this.reportOutcome?.(entry, 'unsafe', 'workspace_identity_or_eligibility_invalid').catch(() => undefined)
        return false
      }

      try {
        await this.runner.deleteDirectory(entry.workspacePath)
      } catch (error) {
        await this.reportOutcome?.(entry, 'deletion_failed', errorReason(error)).catch(() => undefined)
        throw error
      }
      await this.reportOutcome?.(entry, 'removed')
      await this.registry.remove(this.registry.entryKey(entry))
      return true
    }

    const result = await fence.withRemovalFence(entry.workspacePath, async () => {
      try {
        return await remove()
      } catch (error) {
        log.error('workspace cleanup failed to remove path', {
          run: this.registry.entryKey(entry),
          path: entry.workspacePath,
          exception: error,
        })
        return false
      }
    })
    if (result.kind === 'busy') await this.reportOutcome?.(entry, 'in_use', 'workspace_busy').catch(() => undefined)
    if (result.kind === 'failed') await this.reportOutcome?.(entry, 'unsafe', result.reason).catch(() => undefined)
    return result.kind === 'completed' ? result.value : false
  }
}

function errorReason(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
