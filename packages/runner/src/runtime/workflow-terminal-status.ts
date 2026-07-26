// Workflow run lifecycle status contract for the runner.
//
// The server owns the authoritative WorkflowRunStatus enum; the runner
// receives the canonical name as a string and decides what to do with
// each state. Only terminal states (Completed / Stopped) make a
// workspace eligible for cleanup.
//
// `Failed` is deliberately NOT terminal: it is a recoverable mid-state —
// the server's Retry/Rerun/RerunFromStage revive it back to a
// dispatchable status. Reclaiming a retriable run's workspace loses
// plan/build artifacts because the next prepare() rebuilds the branch
// from base. A status the server reports but we don't recognize is
// conservatively treated as non-terminal — better to leave a workspace
// `active` for a future convergence tick than to mark it eligible by
// mistake.

export type WorkflowRunStatusName =
  | "Created"
  | "Pending"
  | "Ready"
  | "Running"
  | "AwaitingApproval"
  | "Paused"
  | "Stopped"
  | "Completed"
  | "Failed"

export const TERMINAL_WORKFLOW_STATUSES = new Set<WorkflowRunStatusName>([
  "Completed",
  "Stopped",
])

export function isTerminalWorkflowStatus(status: string | null | undefined): status is WorkflowRunStatusName {
  if (!status) return false
  return TERMINAL_WORKFLOW_STATUSES.has(status as WorkflowRunStatusName)
}
