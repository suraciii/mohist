// Workflow run lifecycle status contract for the runner.
//
// The server's authoritative WorkflowRunStatus enum lives at
// `packages/server/.../Workflow/Domain/Run/WorkflowRun.cs`:
//   Created, Pending, Ready, Running, AwaitingApproval, Paused,
//   Stopped, Completed, Failed
//
// The runner is the consumer: it receives the canonical enum name as a
// string (via SignalR push or convergence batch query) and decides what
// to do with each state. Only terminal states (Completed / Stopped)
// cause a workspace to transition from `active` to `eligible`.
//
// `Failed` is deliberately NOT terminal: it is a recoverable mid-state —
// the server's Retry/Rerun/RerunFromStage revive it back to a
// dispatchable status. A run that can be retried must not have its
// workspace reclaimed (reclaiming mid-retry loses plan/build artifacts
// because the next prepare() rebuilds the branch from base). Only the
// true terminals (Completed / Stopped) make a workspace eligible.
//
// Keeping the terminal-set knowledge centralized here means the SignalR
// handler and the convergence loop share one definition. A status that
// the server reports but we don't recognize is conservatively treated as
// non-terminal — better to leave a workspace `active` for a future
// convergence tick than to mark it eligible by mistake.
//
// The union deliberately matches the new state-machine vocabulary
// (D1/D7): `Created` is "built but not started", `Pending` is "started,
// waiting for any runner to claim", `Ready` is "assigned, waiting for
// the bound runner to pick up work". The cleanup-safety logic only
// inspects `TERMINAL_WORKFLOW_STATUSES`; widening the union to all
// non-terminal values (including the new `Created` and `Ready`) keeps
// the type contract in sync with the server enum without any
// behavioral change — anything not in the terminal set continues to
// block automatic workspace removal by construction.

export type WorkflowRunStatusName =
  | "Created"
  | "AwaitingBinding"
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
