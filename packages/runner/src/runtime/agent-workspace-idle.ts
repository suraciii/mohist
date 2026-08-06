import type { CleanupPolicy } from "../core/types.js"
import type { AgentWorkspaceActivity } from "../server/connection.js"
import type { AgentWorkspaceRegistry } from "./agent-workspace-registry.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("cleanup")

const DAY_MS = 24 * 60 * 60 * 1000

export interface AgentWorkspaceActivityConnection {
  getAgentWorkspaceActivity(projectId: string, childSessionId: string, signal: AbortSignal): Promise<AgentWorkspaceActivity>
}

export interface AgentWorkspaceIdleProbeOptions {
  readonly registry: AgentWorkspaceRegistry
  readonly connection: AgentWorkspaceActivityConnection
  readonly now?: () => Date
}

export interface AgentWorkspaceIdleProbeResult {
  readonly markedEligible: number
}

/**
 * Transitions runner-tracked managed-worktree entries from `active` to
 * `eligible` from two server-authoritative signals, both fail-closed.
 *
 * Every activity response is first fed to the registry's orphan grace
 * (`recordActivity`): consecutive `not-found` reclaims an orphaned
 * session's workspace, while any `active` / `idle` / `pending` /
 * `unknown` clears the orphan candidate. When `recordActivity` itself
 * flips an entry to `eligible`, that transition is counted and not
 * repeated.
 *
 * The server is the sole arbiter of "idle": only a confirmed `idle`
 * answer with a durable `idleSince` older than the maintenance cycle's
 * retention threshold is eligible (the second path, via `markEligible`).
 *
 * An unusable answer — a network / 403 / malformed error, or an entry
 * with no project binding to query — is observed as `unknown` so a
 * stale orphan candidate is cleared; it never confers eligibility.
 *
 * Eligibility is additive: explicit release, the parent-dependency
 * fence and the removal fence are untouched.
 */
export class AgentWorkspaceIdleProbe {
  private readonly now: () => Date

  constructor(private readonly options: AgentWorkspaceIdleProbeOptions) {
    this.now = options.now ?? (() => new Date())
  }

  async runOnce(policy: CleanupPolicy | null | undefined, signal: AbortSignal): Promise<AgentWorkspaceIdleProbeResult> {
    if (signal.aborted) return { markedEligible: 0 }

    const active = this.options.registry.list().filter((entry) => entry.phase === "active")
    if (active.length === 0) return { markedEligible: 0 }

    const cutoff = this.idleCutoff(policy)
    let markedEligible = 0
    for (const entry of active) {
      if (signal.aborted) break
      // The activity route is keyed by (projectId, childSessionId); an
      // entry without a project binding cannot be queried. Observe it
      // as `unknown` so a stale orphan candidate is cleared, then stay
      // fail-closed — leave it to explicit release.
      if (entry.projectId === null) {
        await this.options.registry.recordActivity(entry.childSessionId, "unknown")
        continue
      }
      try {
        const activity = await this.options.connection.getAgentWorkspaceActivity(entry.projectId, entry.childSessionId, signal)
        // Every observation feeds the orphan grace state machine first.
        // `recordActivity` flipping an entry to `eligible` (consecutive
        // `not-found`) is the authoritative transition — count it and
        // do not repeat it. A still-active entry then falls through to
        // the server-confirmed-idle path.
        const observed = await this.options.registry.recordActivity(entry.childSessionId, activity.state)
        if (observed?.phase === "eligible") {
          markedEligible++
          continue
        }
        if (this.isIdleEligible(activity, cutoff)) {
          await this.options.registry.markEligible(entry.childSessionId)
          markedEligible++
        }
      } catch (error) {
        // network / 403 / malformed: the answer is unusable. Observe
        // `unknown` to clear any orphan candidate and stay fail-closed.
        await this.options.registry.recordActivity(entry.childSessionId, "unknown")
        log.warn("agent workspace idle probe failed", { session: entry.childSessionId, exception: error })
      }
    }
    return { markedEligible }
  }

  // The maintenance cycle's retention window is reused as the minimum
  // idle duration: a session must be demonstrably idle (the server's
  // durable idleSince) for at least retentionDays before it is safe to
  // reclaim. Without a configured window there is no "idle long enough"
  // to assert, so the probe stays fail-closed and leaves eligibility to
  // explicit release.
  private isIdleEligible(activity: AgentWorkspaceActivity, cutoff: number | null): boolean {
    if (activity.state !== "idle") return false
    if (cutoff === null) return false
    if (activity.idleSince === null) return false
    const idleSinceMs = Date.parse(activity.idleSince)
    if (Number.isNaN(idleSinceMs)) return false
    return idleSinceMs < cutoff
  }

  private idleCutoff(policy: CleanupPolicy | null | undefined): number | null {
    if (!policy) return null
    const days = policy.retentionDays
    if (days === null || days === undefined || days <= 0) return null
    return this.now().getTime() - days * DAY_MS
  }
}
