import type { AgentWorkspaceManager, RepositorySnapshot } from "./agent-workspace.js"
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
}

export type WorkspaceSourceConfirmationResult =
  | { kind: "confirmed" }
  | { kind: "rejected"; reason: WorkspaceSourceRejectionReason }

export interface WorkspaceSourceReporter {
  reportConfirmed(request: WorkspaceSourceConfirmationRequest): Promise<void>
  reportRejected(request: WorkspaceSourceConfirmationRequest, reason: WorkspaceSourceRejectionReason): Promise<void>
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
      if (verdict.kind === "confirmed") await this.reporter.reportConfirmed(request)
      else await this.reporter.reportRejected(request, verdict.reason)
      this.reported.set(key, verdict)
    } catch (error) {
      log.error("workspace source report failed; will retry on next execution", { session: request.sessionId, exception: error })
    }
    return verdict
  }
}

export function loggingWorkspaceSourceReporter(): WorkspaceSourceReporter {
  return {
    async reportConfirmed(request) {
      log.info("workspace source confirmed", { session: request.sessionId, repository: request.repository.name })
    },
    async reportRejected(request, reason) {
      log.warn("workspace source rejected", { session: request.sessionId, repository: request.repository.name, reason })
    },
  }
}
