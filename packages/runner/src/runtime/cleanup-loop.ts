import { isUnderRunnerRoot } from "./workspace-query.js"
import { readMarkerWorkflowRunId } from "./workspace.js"
import { deleteDirectory } from "../system/process.js"
import type { CleanupPolicy } from "../core/types.js"
import type { WorkspaceRegistry, WorkspaceRegistryEntry } from "./workspace-registry.js"

export interface CleanupRunner {
  isUnderRunnerRoot(root: string, candidate: string): boolean
  readMarkerWorkflowRunId(workspacePath: string): Promise<string | null | undefined>
  deleteDirectory(path: string): Promise<void>
  computeDirectorySize(path: string, signal: AbortSignal): Promise<number | null>
}

export interface CleanupLoopResult {
  retentionRemoved: number
  budgetRemoved: number
  guardAborted: number
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
      workspaceUsageBytes: null,
    }

    if (signal.aborted) return result
    if (!policy) return result

    const eligible = this.registry.list().filter((e) => e.phase === "eligible")
    if (eligible.length === 0) return result

    const retentionDisabled = policy.retentionDays == null || policy.retentionDays <= 0
    const budgetDisabled = policy.storageBudgetBytes == null || policy.storageBudgetBytes <= 0

    if (retentionDisabled && budgetDisabled) return result

    if (!retentionDisabled) {
      const cutoff = Date.now() - policy.retentionDays! * 24 * 60 * 60 * 1000
      for (const entry of eligible) {
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

  async safeRemove(entry: WorkspaceRegistryEntry): Promise<boolean> {
    if (!this.runner.isUnderRunnerRoot(this.runnerRoot, entry.workspacePath)) {
      console.warn(
        `workspace cleanup: refused to remove ${entry.workspacePath} — path is outside runnerRoot`,
      )
      return false
    }

    const markerRunId = await this.runner.readMarkerWorkflowRunId(entry.workspacePath)
    if (!markerRunId) {
      console.warn(
        `workspace cleanup: refused to remove ${entry.workspacePath} — marker is missing or unreadable`,
      )
      return false
    }
    if (markerRunId !== entry.workflowRunId) {
      console.warn(
        `workspace cleanup: refused to remove ${entry.workspacePath} — marker workflowRunId (${markerRunId}) does not match registry (${entry.workflowRunId})`,
      )
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
  isUnderRunnerRoot(root: string, candidate: string): boolean {
    return isUnderRunnerRoot(root, candidate)
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
}
