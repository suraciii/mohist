import { existsSync } from "node:fs"
import { resolve } from "node:path"
import { isUnderRunnerRoot } from "./workspace-query.js"
import { defaultRunnerRoot, issueWorkspacePath, readMarkerWorkflowRunId, validateWorkspaceIdentity, withManagedWorkspacePath } from "./workspace.js"
import { deleteDirectory } from "../system/process.js"
import { runnerLogger } from "../system/logger.js"
import type { CleanupPolicy } from "../core/types.js"
import type { WorkspaceRegistry, WorkspaceRegistryEntry, WorkspaceRegistryPhase } from "./workspace-registry.js"
import type { AgentWorkspaceRegistry } from "./agent-workspace-registry.js"
import type { WorkspaceRemovalFence } from "./workspace-removal-fence.js"

const log = runnerLogger.child("cleanup")

// The maintenance loop is shared by workflow workspaces and agent
// managed worktrees (agent-workspace.md): both reuse the same phase
// model, tick, single-flight constraint, storage budget and removal
// fence. `CleanupEntry` is the common shape; the registry supplies its
// own key and phase transitions.

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
  validateAndDeleteWorkspace?(entry: CleanupEntry): Promise<boolean>
  // Whether an `active` child workspace depends on this entry's
  // directory (agent worktrees are git worktrees of their parent's
  // repository). Such entries are DEFERRED, not stuck: the dependency
  // can clear once the child becomes eligible or is released.
  hasActiveDependents?(entry: CleanupEntry): Promise<boolean>
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
  // Eligible entries with `active` dependent workspaces; their removal
  // is deferred (not stuck) until the dependents clear.
  deferred: number
  workspaceUsageBytes: number | null
}

export class CleanupLoop<E extends CleanupEntry = WorkspaceRegistryEntry> {
  private usageCache: { bytes: number; timestamp: number } | null = null
  private readonly usageCacheTtlMs = 5 * 60_000

  constructor(
    private readonly registry: CleanupRegistry<E>,
    private readonly runner: CleanupRunner,
    private readonly runnerRoot: string,
    private readonly removalFence: () => WorkspaceRemovalFence | null = () => null,
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
      deferred: 0,
      workspaceUsageBytes: null,
    }

    if (signal.aborted) return result
    if (!policy) return result

    const initialEligible = this.registry.list().filter((e) => e.phase === "eligible")
    if (initialEligible.length === 0) return result

    const deferredPaths = new Set<string>()
    if (this.runner.hasActiveDependents) {
      for (const entry of initialEligible) {
        if (signal.aborted) break
        if (await this.runner.hasActiveDependents(entry)) deferredPaths.add(entry.workspacePath)
      }
    }
    const eligible = initialEligible.filter((e) => !deferredPaths.has(e.workspacePath))
    if (signal.aborted) return result
    result.deferred = deferredPaths.size
    if (eligible.length === 0) return result

    // Resolution pass: give a guard-refused eligible entry a
    // deterministic exit so it is not re-evaluated and re-warned every
    // tick. A guard refusal is deterministic (identical outcome on every
    // tick), so it MUST NOT be retried indefinitely. This pass runs
    // before the disabled-policy early-return, so resolution is
    // independent of retention/budget — a stuck entry is resolved even
    // when both policies are disabled. The directory is never deleted
    // here: the safety refusal stands; only the registry's repeated
    // re-evaluation ends.
    for (const entry of eligible) {
      if (signal.aborted) break
      if (blockedPaths.has(entry.workspacePath)) continue
      const verdict = await this.evaluateGuards(entry)
      if (verdict.ok) continue
      log.warn("workspace cleanup refused", { run: this.registry.entryKey(entry), path: entry.workspacePath, reason: verdict.message })
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
    // Deferred entries stay out of both passes.
    const reList = () => this.registry.list().filter((e) => e.phase === "eligible" && !deferredPaths.has(e.workspacePath))

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
        if (entrySize != null && entrySize > 0) {
          currentUsage -= entrySize
        }

        const removed = await this.safeRemove(entry, blockedPaths)
        if (removed) result.budgetRemoved++
        else result.guardAborted++
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
  private async evaluateGuards(
    entry: E,
  ): Promise<{ ok: true } | { ok: false; message: string }> {
    if (!this.runner.isUnderRunnerRoot(this.runnerRoot, entry.workspacePath)) {
      return { ok: false, message: "path is outside runnerRoot" }
    }
    const diskIdentity = await this.runner.readWorkspaceIdentity(entry.workspacePath)
    if (!diskIdentity) {
      return { ok: false, message: "workspace identity is missing or unreadable" }
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
    if (blockedPaths.has(entry.workspacePath)) return false
    if (this.runner.hasActiveDependents && await this.runner.hasActiveDependents(entry)) return false
    const fence = this.removalFence()
    const remove = async (): Promise<boolean> => {
      const verdict = await this.evaluateGuards(entry)
      if (!verdict.ok) {
        log.warn("workspace cleanup refused", { run: this.registry.entryKey(entry), path: entry.workspacePath, reason: verdict.message })
        return false
      }

      if (!this.runner.pathExists(entry.workspacePath)) {
        await this.registry.remove(this.registry.entryKey(entry))
        return true
      }

      if (this.runner.validateAndDeleteWorkspace) {
        if (!(await this.runner.validateAndDeleteWorkspace(entry))) {
          log.warn("workspace cleanup refused", { run: this.registry.entryKey(entry), path: entry.workspacePath, reason: "workspace identity is invalid" })
          return false
        }
        await this.registry.remove(this.registry.entryKey(entry))
        return true
      }

      if (this.runner.validateWorkspace && !(await this.runner.validateWorkspace(entry))) {
        log.warn("workspace cleanup refused", { run: this.registry.entryKey(entry), path: entry.workspacePath, reason: "workspace identity is invalid" })
        return false
      }

      await this.runner.deleteDirectory(entry.workspacePath)
      await this.registry.remove(this.registry.entryKey(entry))
      return true
    }

    if (!fence) {
      try {
        return await remove()
      } catch (error) {
        log.error("workspace cleanup failed to remove path", { run: this.registry.entryKey(entry), path: entry.workspacePath, exception: error })
        return false
      }
    }

    const result = await fence.withRemovalFence(entry.workspacePath, async () => {
      try {
        return await remove()
      } catch (error) {
        log.error("workspace cleanup failed to remove path", { run: this.registry.entryKey(entry), path: entry.workspacePath, exception: error })
        return false
      }
    })
    return result.kind === "completed" ? result.value : false
  }
}

export class DefaultCleanupRunner implements CleanupRunner {
  constructor(
    private readonly runnerRoot = defaultRunnerRoot(),
    private readonly agentRegistry: AgentWorkspaceRegistry | null = null,
  ) {}

  isUnderRunnerRoot(root: string, candidate: string): boolean {
    return isUnderRunnerRoot(root, candidate)
  }

  pathExists(path: string): boolean {
    return existsSync(path)
  }

  async readWorkspaceIdentity(workspacePath: string): Promise<string | null | undefined> {
    return await readMarkerWorkflowRunId(workspacePath)
  }

  async deleteDirectory(path: string): Promise<void> {
    await deleteDirectory(path)
  }

  async computeDirectorySize(path: string, signal: AbortSignal): Promise<number | null> {
    try {
      const { runCommand } = await import("../system/process.js")
      const result = await runCommand("du", ["-sb", path], ".", signal)
      if (result.exitCode !== 0) return null
      const match = result.stdout.match(/^(\d+)/)
      if (!match) return null
      return parseInt(match[1], 10)
    } catch {
      return null
    }
  }

  // A workflow workspace cannot be removed while an `active` agent
  // worktree depends on it (the worktree is a git worktree of the
  // workspace's repository and shares its object store).
  async hasActiveDependents(entry: CleanupEntry): Promise<boolean> {
    if (!this.agentRegistry) return false
    const target = resolve(entry.workspacePath)
    return this.agentRegistry.list().some((candidate) => candidate.phase === "active" && resolve(candidate.parentWorkDir) === target)
  }

  async validateWorkspace(entry: WorkspaceRegistryEntry): Promise<boolean> {
    return await this.withValidWorkspace(entry, async () => true)
  }

  async validateAndDeleteWorkspace(entry: WorkspaceRegistryEntry): Promise<boolean> {
    return await this.withValidWorkspace(entry, async (workspacePath) => {
      await deleteDirectory(workspacePath)
      return true
    })
  }

  private async withValidWorkspace(entry: WorkspaceRegistryEntry, operation: (workspacePath: string) => Promise<boolean>): Promise<boolean> {
    if (!entry.runBranch) return false
    if (entry.workspacePath !== issueWorkspacePath(this.runnerRoot, entry.workflowRunId)) return false
    try {
      return await withManagedWorkspacePath(this.runnerRoot, entry.workspacePath, true, async (workspacePath) => {
        const markerRunId = await readMarkerWorkflowRunId(workspacePath)
        if (markerRunId !== entry.workflowRunId) return false
        return await operation(workspacePath)
      })
    } catch {
      return false
    }
  }
}
