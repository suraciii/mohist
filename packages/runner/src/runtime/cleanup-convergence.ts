import type { ServerConnection } from "../server/connection.js"
import type { WorkspaceRegistry } from "./workspace-registry.js"
import { isTerminalWorkflowStatus } from "./workflow-terminal-status.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("cleanup")

// Convergence backstop for missed workflow terminal events.
//
// Push is a latency optimization only — correctness must not depend on it.
// When the runner misses a `ReceiveWorkflowRunStatus` push (because it was
// offline, the SignalR transport dropped the message, or the workflow grain
// fired before the runner was assigned), the runner must still converge to
// the correct eligibility state. This module implements that backstop:
//
//   - The runner enumerates ONLY registry entries still in phase `active`.
//   - It batches those workflowRunIds into a single
//     `POST /api/runner/{runnerId}/workflow-runs/status` call.
//   - For every run the server reports as terminal, the matching registry
//     entry transitions to `eligible` (idempotent — already-eligible
//     entries are not re-stamped).
//   - Entries the server has forgotten about are dropped from the local
//     registry (the runner only tracks workspaces it still owns).
//
// The runner MUST NOT enumerate or query workflow runs that have no
// active registry entry on this runner — i.e. no full-history scan. The
// caller (RunnerHost) triggers the backstop at startup, on SignalR
// reconnect, and on a periodic timer.

export interface ConvergenceResult {
  queried: number
  transitioned: number
  dropped: number
}

export interface ConvergenceRunner {
  queryActiveStatuses(workflowRunIds: string[], signal: AbortSignal): Promise<Record<string, string>>
}

export class ConvergenceBackstop {
  constructor(
    private readonly registry: WorkspaceRegistry,
    private readonly runner: ConvergenceRunner,
  ) {}

  // Run a single convergence pass. Returns counts for observability:
  //   - queried: how many active workflowRunIds were sent to the server
  //   - transitioned: how many active entries moved to eligible this pass
  //   - dropped: how many active entries the server had no record of
  async runOnce(signal: AbortSignal): Promise<ConvergenceResult> {
    const activeEntries = this.registry.list().filter((entry) => entry.phase === "active")
    if (activeEntries.length === 0) {
      return { queried: 0, transitioned: 0, dropped: 0 }
    }
    const workflowRunIds = activeEntries.map((entry) => entry.workflowRunId)
    let statuses: Record<string, string>
    try {
      statuses = await this.runner.queryActiveStatuses(workflowRunIds, signal)
    } catch (error) {
      // Convergence is best-effort. The next tick (or reconnect) will
      // retry. Push may still be working in parallel.
      log.error("workspace cleanup convergence query failed", { exception: error })
      return { queried: workflowRunIds.length, transitioned: 0, dropped: 0 }
    }

    let transitioned = 0
    let dropped = 0
    for (const entry of activeEntries) {
      const reported = statuses[entry.workflowRunId]
      if (reported === undefined) {
        // The server has no record of this run id. The runner only tracks
        // workspaces it owns; if the server has forgotten the run we
        // should forget the entry too. The on-disk directory is left
        // alone — automatic cleanup has its own pre-delete guards
        //, and the manual `RemoveWorkspace` entrypoint is still
        // available to reclaim disk space if desired.
        const removed = await this.registry.remove(entry.workflowRunId)
        if (removed) dropped++
        continue
      }
      if (!isTerminalWorkflowStatus(reported)) continue
      const updated = await this.registry.markEligible(entry.workflowRunId)
      if (updated && updated.phase === "eligible" && updated.terminalAt) {
        transitioned++
      }
    }
    return { queried: workflowRunIds.length, transitioned, dropped }
  }
}

// Adapter from the runner's ServerConnection into the ConvergenceRunner
// contract. ServerConnection.url() builds the runner-scoped path; this
// adapter strips it to the workflow-runs/status route and POSTs the
// batch query.
export class ServerConnectionConvergenceAdapter implements ConvergenceRunner {
  constructor(private readonly connection: ServerConnection) {}

  async queryActiveStatuses(workflowRunIds: string[], signal: AbortSignal): Promise<Record<string, string>> {
    return await this.connection.workflowRunsStatus(workflowRunIds, signal)
  }
}
