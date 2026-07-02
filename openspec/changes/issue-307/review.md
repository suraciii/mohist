# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs
  Evidence: `IssueGrain` now exposes `internal string GrainKeyForTest` solely so `IssueWorkflowReadPathSpecs` can directly instantiate the grain and call `OnActivateAsync` with a fake key. The hook is low-risk because it is internal and unset in production, but it is still test-only state in a production grain. Current behavior is correct: the property falls back to `this.GetPrimaryKeyString()` when empty, and no production caller sets it.
  SuggestedAction: Prefer an Orleans test fixture or a small test-only wrapper if more direct grain tests are added, so production grain key resolution stays free of test hooks.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: verification wrapper
  Evidence: The exact top-level `npm test` command timed out twice in this environment (first with the default 120s timeout, then with 420s). Direct verification of its relevant components completed successfully: `dotnet test Mohist.sln -p:SkipWebBuild=true` passed with 3371 passed / 13 skipped, `npm run typecheck -w packages/web` passed, `npm run test:run -w packages/web` passed with 3543 passed / 1 skipped, `npm run typecheck -w packages/runner` passed, and `npm test -w packages/runner` passed with 753 passed. No candidate-specific failure was observed.
  SuggestedAction: Investigate the wrapper-level timeout separately if CI sees the same behavior; the reviewed server/web/runner component checks are green.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: accepted transition-period event delivery risk
  Evidence: The issue, proposal, and design explicitly accept removing the daily sweep and lazy read-path fallback before durable event delivery exists. The candidate implements that accepted tradeoff: `IssueWorkflowCompletionHandler` handles `com.mohist.workflow.run.completed`, while `IssueWorkflowReconciliationService` and `ReconcileWithWorkflowTerminalStateAsync` are gone. This is not a defect in issue-307, but operators should still know that a swallowed best-effort event failure can leave an issue in `InProgress` until manually re-triggered.
  SuggestedAction: Track the durable at-least-once event mechanism as the documented follow-up.
  Status: out-of-scope

## Review Notes

- Server acceptance evidence: `IssueWorkflowCompletionHandler` subscribes to `EventCatalog.ReverseDns.WorkflowRunCompleted`, extracts the run id from the CloudEvent source, reverse-resolves the in-progress issue via `IssueQuerier.GetIssueIdForWorkflowRunAsync`, and calls `IIssueGrain.CompleteWorkAsync(workflowRunId)` while swallowing/logging lookup and grain failures. The lookup filters on `WorkflowRunId` plus serialized status `inProgress`, matching the computed DB column contract.
- Reconciliation removal evidence: `MohistServiceRegistration` no longer registers `IssueWorkflowReconciliationService`; grep across `packages/server` found no remaining `IssueWorkflowReconciliationService` or `ReconcileWithWorkflowTerminalStateAsync` references; `IssueGrain.GetWorkflowStatusAsync` now only queries/projects workflow status without writing issue state.
- Web acceptance evidence: `WorkflowView`, `SessionTimeline`, `IssueCard`, and `IssueDetailPage` render only `plan`, `build`, `check`, and `integrate` stage labels/orders. Targeted grep found no `WorkflowStage.Done` rendered-list/order usage in those surfaces; the remaining `currentStage === 'done'` branch in `SessionTimeline` is compatibility logic that marks all real stages completed without rendering a Done cell.
- Coverage evidence: new server specs cover handler subscription shape, reverse lookup filtering, empty-source/no-match no-ops, duplicate delivery idempotency, mismatched workflow-run guard behavior, failed/stopped filter exclusion, in-memory bus dispatch, and pure read-path behavior. Web tests were updated for the removed Done cell in stage bar, session timeline, kanban card, and archived issue detail scenarios.

<promise>PASS</promise>
