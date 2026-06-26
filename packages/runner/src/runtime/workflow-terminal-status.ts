// Workflow run lifecycle status contract for the runner.
//
// The server's authoritative WorkflowRunStatus enum lives at
// `packages/server/.../Workflow/Domain/Run/WorkflowRun.cs`:
//   Pending, Running, AwaitingApproval, Paused, Stopped, Completed, Failed
//
// The runner is the consumer: it receives the canonical enum name as a
// string (via SignalR push or convergence batch query) and decides what
// to do with each state. Only terminal states (Completed / Stopped /
// Failed) cause a workspace to transition from `active` to `eligible`.
//
// Keeping the terminal-set knowledge centralized here means the SignalR
// handler and the convergence loop share one definition. A status that
// the server reports but we don't recognize is conservatively treated as
// non-terminal — better to leave a workspace `active` for a future
// convergence tick than to mark it eligible by mistake.

export type WorkflowRunStatusName =
  | "Pending"
  | "Running"
  | "AwaitingApproval"
  | "Paused"
  | "Stopped"
  | "Completed"
  | "Failed"

export const TERMINAL_WORKFLOW_STATUSES = new Set<WorkflowRunStatusName>([
  "Completed",
  "Stopped",
  "Failed",
])

export function isTerminalWorkflowStatus(status: string | null | undefined): status is WorkflowRunStatusName {
  if (!status) return false
  return TERMINAL_WORKFLOW_STATUSES.has(status as WorkflowRunStatusName)
}