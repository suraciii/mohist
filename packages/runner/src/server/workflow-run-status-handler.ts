// The server-pushed terminal
// `ReceiveWorkflowRunStatus` SignalR method is registered through the
// free-function `registerWorkflowRunStatusHandler(conn, deps)` so the
// cluster's dependency surface is explicit and the registry transition
// path can be exercised independently of the other push handlers.
//
// Behaviour:
//   - drops on null/undefined payload, missing/empty `workflowRunId`,
//     non-terminal status, or unregistered `registry`
//   - terminal status (`Completed` / `Stopped`) →
//     `registry.markEligible(workflowRunId)` → registry transitions
//     `active` → `eligible` and stamps `terminalAt` (idempotent: an
//     already-eligible entry is left alone with `terminalAt` unchanged)
//   - non-terminal status (including `Failed`, which is recoverable
//     mid-state, and any unknown value) → registry entry untouched,
//     `terminalAt` stays `null`
//   - unknown runId (runner never materialised that workflow) → no
//     throw, registry unchanged
//   - registry operation throws → logged, handler resolves silently
//     (lifecycle events MUST NOT crash the SignalR transport)
//
// Push is a latency optimization. If the push is missed (runner
// offline at the moment of the event, transport drop, race with
// assignment), the convergence backstop wired into
// RunnerHost.startup / onReconnected / periodic timer is the
// authoritative catch-all — see `cleanup-convergence.ts`.

import * as signalR from "@microsoft/signalr"
import { isTerminalWorkflowStatus } from "../runtime/workflow-terminal-status.js"
import type { WorkspaceRegistry } from "../runtime/workspace-registry.js"
import type { ReceiveWorkflowRunStatusPayload } from "./session-target.js"
import { runnerLogger } from "../system/logger.js"

const log = runnerLogger.child("cleanup")

export interface WorkflowRunStatusHandlerDeps {
  registry?: WorkspaceRegistry | null
}

export function registerWorkflowRunStatusHandler(
  conn: signalR.HubConnection,
  deps: WorkflowRunStatusHandlerDeps,
): void {
  conn.on("ReceiveWorkflowRunStatus", async (payload: ReceiveWorkflowRunStatusPayload | null | undefined) => {
    await handleWorkflowRunStatus(payload, deps)
  })
}

async function handleWorkflowRunStatus(
  payload: ReceiveWorkflowRunStatusPayload | null | undefined,
  deps: WorkflowRunStatusHandlerDeps,
): Promise<void> {
  if (!payload) return
  const workflowRunId = payload.workflowRunId
  const status = payload.status
  if (!workflowRunId || typeof workflowRunId !== "string") return
  if (!isTerminalWorkflowStatus(status)) {
    // Server only pushes terminal statuses today (see
    // RunnerWorkflowStatusRouter), but guard defensively: an unknown /
    // non-terminal status leaves the entry active. Convergence will
    // re-check on its next tick if needed.
    return
  }
  const registry = deps.registry ?? null
  if (!registry) return
  try {
    const updated = await registry.markEligible(workflowRunId)
    if (!updated) {
      // Push for a run the runner never materialized (e.g. an event for
      // a workflow whose workspace lives on another runner). The runner
      // only tracks workspaces it owns; nothing to do.
      return
    }
    log.info("workspace transitioned to eligible", { run: workflowRunId, reason: `status=${status} terminalAt=${updated.terminalAt}` })
  } catch (error) {
    log.error("workspace cleanup failed to mark eligible from push", { run: workflowRunId, exception: error })
  }
}
