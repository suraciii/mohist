import type { AgentWorkspaceManager, RepositorySnapshot } from "./agent-workspace.js"
import type { ServerConnection } from "../server/connection.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("source")

// Parent Project source confirmation seam (agent-workspace.md
// "WorkspaceRepository 生产者与确认"). The Server marks a parent
// session's WorkspaceRepository `unconfirmed` on launch; the Runner
// verifies on first execution that the authoritative workDir is
// runner-owned and its origin equals the Project Repository snapshot,
// then reports `workspace_source_confirmed` / `workspace_source_rejected`.
// The Server slice wires a durable transport (runtime-event outbox or
// dedicated channel) as the reporter; the default reporter logs only.

export type WorkspaceSourceRejectionReason = "origin-mismatch" | "not-runner-owned"

export interface WorkspaceSourceConfirmationRequest {
  sessionId: string
  workDir: string
  repository: RepositorySnapshot
  /**
   * Runtime session id bound at attach time. Carried in the report so
   * the Server's runner-owned runtime-events route can accept it; the
   * transition itself does not require a current binding. Null when the
   * report is produced before a binding exists.
   */
  runtimeSessionId?: string | null
}

export type WorkspaceSourceConfirmationResult =
  | { kind: "confirmed" }
  | { kind: "rejected"; reason: WorkspaceSourceRejectionReason }

export interface WorkspaceSourceReporter {
  reportConfirmed(request: WorkspaceSourceConfirmationRequest, signal: AbortSignal): Promise<void>
  reportRejected(request: WorkspaceSourceConfirmationRequest, reason: WorkspaceSourceRejectionReason, signal: AbortSignal): Promise<void>
}

export class WorkspaceSourceConfirmer {
  // sessionId:repositoryName → last reported verdict. A reported key is
  // never re-reported (the Server's confirmation state is authoritative
  // once set); a failed report is NOT cached so the next execution
  // retries it.
  private readonly reported = new Map<string, WorkspaceSourceConfirmationResult>()

  constructor(
    private readonly manager: AgentWorkspaceManager,
    private readonly reporter: WorkspaceSourceReporter = loggingWorkspaceSourceReporter(),
  ) {}

  async confirm(request: WorkspaceSourceConfirmationRequest, signal: AbortSignal): Promise<WorkspaceSourceConfirmationResult> {
    const key = `${request.sessionId}:${request.repository.name}`
    const cached = this.reported.get(key)
    if (cached) return cached

    let verdict: WorkspaceSourceConfirmationResult
    try {
      const evaluated = await this.manager.validateParentWorkDir(request.workDir, request.repository.gitUrl, signal)
      verdict = evaluated.kind === "ok"
        ? { kind: "confirmed" }
        : { kind: "rejected", reason: evaluated.reason }
    } catch (error) {
      log.error("workspace source verification failed", { session: request.sessionId, exception: error })
      verdict = { kind: "rejected", reason: "not-runner-owned" }
    }

    try {
      if (verdict.kind === "confirmed") await this.reporter.reportConfirmed(request, signal)
      else await this.reporter.reportRejected(request, verdict.reason, signal)
      this.reported.set(key, verdict)
    } catch (error) {
      log.error("workspace source report failed; will retry on next execution", { session: request.sessionId, exception: error })
    }
    return verdict
  }
}

export function loggingWorkspaceSourceReporter(): WorkspaceSourceReporter {
  return {
    async reportConfirmed(request, _signal) {
      log.info("workspace source confirmed", { session: request.sessionId, repository: request.repository.name })
    },
    async reportRejected(request, reason, _signal) {
      log.warn("workspace source rejected", { session: request.sessionId, repository: request.repository.name, reason })
    },
  }
}

// Reports the verdict to the Server through the runner-owned session
// runtime-events route (`POST /api/runner/{runnerId}/agent-sessions/
// {sessionId}/runtime-events`). That route already validates that the
// reporting runner owns the session (`existing.RunnerId == runnerId`),
// so this carries no fake runtime session identity: runtimeSessionId is
// the real attached id (null only in the pre-binding window). A failed
// report throws so the confirmer does not cache it and retries on the
// next execution.
export function createServerConnectionWorkspaceSourceReporter(
  getConnection: () => ServerConnection | null,
): WorkspaceSourceReporter {
  return {
    async reportConfirmed(request, signal) {
      const connection = getConnection()
      if (!connection) return
      await connection.reconcileAgentSessionRuntimeEvents(request.sessionId, {
        runtimeSessionId: request.runtimeSessionId ?? "",
        runtimeEvents: [{
          type: "workspace_source_confirmed",
          payload: {
            repositoryName: request.repository.name,
            gitUrl: request.repository.gitUrl,
            baseBranch: request.repository.baseBranch,
          },
        }],
      }, signal)
    },
    async reportRejected(request, reason, signal) {
      const connection = getConnection()
      if (!connection) return
      await connection.reconcileAgentSessionRuntimeEvents(request.sessionId, {
        runtimeSessionId: request.runtimeSessionId ?? "",
        runtimeEvents: [{
          type: "workspace_source_rejected",
          payload: {
            repositoryName: request.repository.name,
            gitUrl: request.repository.gitUrl,
            baseBranch: request.repository.baseBranch,
            reason,
          },
        }],
      }, signal)
    },
  }
}
