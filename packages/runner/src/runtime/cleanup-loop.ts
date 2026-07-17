import { existsSync } from "node:fs"
import { isUnderRunnerRoot } from "./workspace-query.js"
import { defaultRunnerRoot, issueWorkspacePath, readMarkerWorkflowRunId, validateWorkspaceIdentity, withManagedWorkspacePath, type IssueWorkspaceMarker } from "./workspace.js"
import { deleteDirectory } from "../system/process.js"
import type { CleanupPolicy } from "../core/types.js"
import type { WorkspaceRegistry, WorkspaceRegistryEntry } from "./workspace-registry.js"

export interface CleanupRunner {
  isUnderRunnerRoot(root: string, candidate: string): boolean
  pathExists(path: string): boolean
  readMarkerWorkflowRunId(workspacePath: string): Promise<string | null | undefined>
  deleteDirectory(path: string): Promise<void>
  computeDirectorySize(path: string, signal: AbortSignal): Promise<number | null>
  validateWorkspace?(entry: WorkspaceRegistryEntry): Promise<boolean>
  validateAndDeleteWorkspace?(entry: WorkspaceRegistryEntry): Promise<boolean>
}

export interface CleanupLoopResult {
  retentionRemoved: number
  budgetRemoved: number
  guardAborted: number
  // Eligible entries resolved to the terminal `stuck` phase this tick
  // because a pre-delete guard deterministically refused them. Such an
  // entry leaves the eligible set, so it is neither re-evaluated nor
  // re-warned on subsequent ticks (issue-423).
  stuckResolved: number
  workspaceUsageBytes: number | null
}

export class CleanupLoop {
  private usageCache: { bytes: number; timestamp: number } | null = null
  private readonly usageCacheTtlMs = 5 * 60_000

  constructor(
    private readonly registry: WorkspaceRegistry,
    private readonly runner: CleanupRunner,
    private readonly runnerRoot: string,
  ) {}

  async runOnce(
    policy: CleanupPolicy | null | undefined,
    signal: AbortSignal,
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

    const eligible = this.registry.list().filter((e) => e.phase === "eligible")
    if (eligible.length === 0) return result

    // Resolution pass (issue-423): give a guard-refused eligible entry a
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
      const verdict = await this.evaluateGuards(entry)
      if (verdict.ok) continue
      console.warn(`workspace cleanup: refused to remove ${entry.workspacePath} — ${verdict.message}`)
      await this.registry.markStuck(entry.workflowRunId)
      result.stuckResolved++
    }
    if (signal.aborted) return result

    const retentionDisabled = policy.retentionDays == null || policy.retentionDays <= 0
    const budgetDisabled = policy.storageBudgetBytes == null || policy.storageBudgetBytes <= 0

    if (retentionDisabled && budgetDisabled) return result

    // Re-list after resolution: entries marked `stuck` above have left
    // the eligible set, so the eviction passes only see entries whose
    // guards passed (plus any that flipped back to eligible in a race).
    if (!retentionDisabled) {
      const removable = this.registry.list().filter((e) => e.phase === "eligible")
      const cutoff = Date.now() - policy.retentionDays! * 24 * 60 * 60 * 1000
      for (const entry of removable) {
        if (signal.aborted) break
        if (!entry.terminalAt) continue
        if (new Date(entry.terminalAt).getTime() > cutoff) continue
        const removed = await this.safeRemove(entry)
        if (removed) result.retentionRemoved++
        else result.guardAborted++
      }
    }

    if (!budgetDisabled && !signal.aborted) {
      const remaining = this.registry.list().filter((e) => e.phase === "eligible")
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

        const removed = await this.safeRemove(entry)
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
    entry: WorkspaceRegistryEntry,
  ): Promise<{ ok: true } | { ok: false; message: string }> {
    if (!this.runner.isUnderRunnerRoot(this.runnerRoot, entry.workspacePath)) {
      return { ok: false, message: "path is outside runnerRoot" }
    }
    const markerRunId = await this.runner.readMarkerWorkflowRunId(entry.workspacePath)
    if (!markerRunId) {
      return { ok: false, message: "marker is missing or unreadable" }
    }
    if (markerRunId !== entry.workflowRunId) {
      return {
        ok: false,
        message: `marker workflowRunId (${markerRunId}) does not match registry (${entry.workflowRunId})`,
      }
    }
    return { ok: true }
  }

  async safeRemove(entry: WorkspaceRegistryEntry): Promise<boolean> {
    const verdict = await this.evaluateGuards(entry)
    if (!verdict.ok) {
      console.warn(`workspace cleanup: refused to remove ${entry.workspacePath} — ${verdict.message}`)
      return false
    }

    if (!this.runner.pathExists(entry.workspacePath)) {
      await this.registry.remove(entry.workflowRunId)
      return true
    }

    if (this.runner.validateAndDeleteWorkspace) {
      try {
        if (!(await this.runner.validateAndDeleteWorkspace(entry))) {
          console.warn(`workspace cleanup: refused to remove ${entry.workspacePath} - workspace identity is invalid`)
          return false
        }
        await this.registry.remove(entry.workflowRunId)
        return true
      } catch (error) {
        console.error(`workspace cleanup: failed to remove ${entry.workspacePath}:`, error)
        return false
      }
    }

    if (this.runner.validateWorkspace && !(await this.runner.validateWorkspace(entry))) {
      console.warn(`workspace cleanup: refused to remove ${entry.workspacePath} - workspace identity is invalid`)
      return false
    }

    try {
      await this.runner.deleteDirectory(entry.workspacePath)
      await this.registry.remove(entry.workflowRunId)
      return true
    } catch (error) {
      console.error(`workspace cleanup: failed to remove ${entry.workspacePath}:`, error)
      return false
    }
  }
}

export class DefaultCleanupRunner implements CleanupRunner {
  constructor(private readonly runnerRoot = defaultRunnerRoot()) {}

  isUnderRunnerRoot(root: string, candidate: string): boolean {
    return isUnderRunnerRoot(root, candidate)
  }

  pathExists(path: string): boolean {
    return existsSync(path)
  }

  async readMarkerWorkflowRunId(workspacePath: string): Promise<string | null | undefined> {
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
    if (!entry.projectId || !entry.repositoryName || !entry.baseBranch || !entry.runBranch || !entry.remoteFingerprint || !entry.remoteIdentityVersion) return false
    if (entry.workspacePath !== issueWorkspacePath(this.runnerRoot, entry.workflowRunId)) return false
    const expected: IssueWorkspaceMarker = {
      version: 2,
      issueId: entry.issueId,
      issueNumber: entry.issueNumber,
      workflowRunId: entry.workflowRunId,
      projectId: entry.projectId!,
      repositoryName: entry.repositoryName!,
      baseBranch: entry.baseBranch!,
      runBranch: entry.runBranch!,
      remoteFingerprint: entry.remoteFingerprint!,
      remoteIdentityVersion: entry.remoteIdentityVersion!,
    }
    try {
      return await withManagedWorkspacePath(this.runnerRoot, entry.workspacePath, true, async (workspacePath) => {
        const markerRunId = await readMarkerWorkflowRunId(workspacePath)
        if (markerRunId !== entry.workflowRunId) return false
        await validateWorkspaceIdentity(workspacePath, expected, new AbortController().signal)
        return await operation(workspacePath)
      })
    } catch {
      return false
    }
  }
}
