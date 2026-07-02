## Why

When a workflow's integrate stage completes, the bound issue should reach `Done` within seconds. Today there is **no event-driven path** from workflow-run completion to issue completion — the issue only flips to `Done` via lazy read-path reconciliation (when somebody opens it) or a background sweep (`IssueWorkflowReconciliationService`) that runs **once per day**. If nobody opens the issue, it can sit in `InProgress` for up to 24h after its work is actually finished, misleading the operator and stalling downstream automation (e.g. epic auto-done, delivery-time metrics).

Compounding this, the Web stage bar synthesizes a trailing "Done" stage that is not a real pipeline stage. During the multi-hour gap between workflow completion and issue transition, the four real stages are green while the synthetic "Done" cell stays grey, implying a fifth pending step that does not exist.

## What Changes

- Add an event-driven bridge: a new `ICloudEventHandler` subscribes to `com.mohist.workflow.run.completed`, resolves the owning issue from the workflow run, and calls `IIssueGrain.CompleteWorkAsync(workflowRunId)`. This is symmetric to `EpicAutoDoneHandler` (issue→epic) and `RunnerWorkflowTerminalStatusHandler` (already subscribes to the same event). Idempotency is inherited from `CompleteWorkAsync`'s existing status guards (issue must be `InProgress`, `workflowRunId` must match).
- `failed` / `stopped` terminal states are **not** handled by this change (out of scope).
- Delete `IssueWorkflowReconciliationService` (the 24h background sweep) and its hosted registration.
- Delete the lazy reconciliation inside `IssueGrain.GetWorkflowStatusAsync` (`ReconcileWithWorkflowTerminalStateAsync`); the read path returns state only and **no longer mutates** issue status (command/query separation).
- Remove the synthetic "Done" stage from Web surfaces: stage bar (`WorkflowView.tsx`), session timeline (`SessionTimeline.tsx`), kanban card (`IssueCard.tsx`), and issue detail (`IssueDetailPage.tsx`). The `WorkflowStage.Done` enum member is retained (widely referenced, harmless); only the places that **add it to the rendered stage list** are changed. Terminal state remains expressed as "Integrate all-green + issue status pill".
- Drop the now-dead tests for the deleted sweep (`IssueWorkflowReconciliationServiceSpecs`) and update affected Web snapshots/tests.

## Capabilities

### New Capabilities

- `issue-workflow-completion`: Event-driven transition of an issue to `Done` when its bound workflow run reaches `Completed`. Covers the `com.mohist.workflow.run.completed` subscription, issue resolution from the run, idempotent completion trigger, and the explicit exclusion of `failed`/`stopped` terminal states.

### Modified Capabilities

- `issue-workflow-run-reference`: The background reconciliation sweep requirement is removed (the daily `IssueWorkflowReconciliationService` is deleted), and the read path (`GetWorkflowStatusAsync`) becomes a pure query with no write side-effects — lazy terminal-state reconciliation is removed.
- `web-ui`: Workflow stage-progression surfaces (stage bar, session timeline, kanban card, issue detail) SHALL render only the real executable pipeline stages (`plan`/`build`/`check`/`integrate`) and SHALL NOT synthesize a terminal "Done" stage cell.

## Impact

- **Server / Events**: New handler under `Events/Subscriptions/` (mirrors `EpicAutoDoneHandler`); needs issue→workflow-run lookup (resolve `issueId` from the completed run, e.g. via the workflow run's issue context or an existing querier).
- **Server / Issue**: `IssueGrain.GetWorkflowStatusAsync` simplified (pure read); `ReconcileWithWorkflowTerminalStateAsync` deleted. `IssueWorkflowReconciliationService` and its `AddHostedService` registration deleted.
- **Web**: Four components edited to drop the synthesized Done stage from their stage lists/order; `WorkflowStage.Done` enum retained.
- **Tests**: `IssueWorkflowReconciliationServiceSpecs` deleted; new spec asserting the event subscription drives issue→Done with injectable time (no scan, no manual open); Web snapshots/tests updated.
- **Known transition-period gap**: Until a durable at-least-once event mechanism (transactional outbox + dispatcher + DLQ, or event-store replay) lands in a separate issue, event delivery remains best-effort in-memory. Removing the sweep + lazy reconciliation eliminates the automatic fallback, so a momentary dispatch/handling failure can leave an issue stuck in `InProgress` requiring manual re-trigger. This is an accepted tradeoff for refusing needless complexity; the durable mechanism is an explicit Non-Goal of this issue.
- No schema migration, no public API contract change.
