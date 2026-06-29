# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs`, rerun-from-stage active-work validation
  Evidence: `RerunFromStage` rejects any task whose status is not `Completed` or `Failed` (`WorkflowRun.Failure.cs:168-174`) and any check whose status is `Pending` or `Running` (`WorkflowRun.Failure.cs:175-178`) before it replaces the target stage. That makes `rerun-from-stage(current)` reject common failed stages that existing `rerun` can restart: `InitializeStage` creates all tasks and checks as pending (`WorkflowRun.Stage.cs:18-31`), and `FailTask` marks only the currently running task failed while leaving later tasks/checks pending (`WorkflowRun.Task.cs:50-66`). Example: a stage has two tasks and/or checks, the first task fails, and the user calls `rerun-from-stage` for that current failed stage. The new action returns `active_work_in_range` instead of creating a new attempt, even though the issue defines `rerun` as the current-stage special case of `rerun-from-stage` and requires existing `rerun` semantics to remain unchanged. Current tests miss this because the API failed-build template has no checks and only one build task (`WorkflowRerunFromStageApiSpecs.cs:324-331`), while domain tests add artificial pending checks only to assert rejection rather than covering the failed-stage recovery case. [disallowed:product-behavior]
  SuggestedAction: Refine active-work detection so it blocks genuinely in-flight work that must be stopped/cancelled, without treating ordinary unstarted control items left behind by a failed target stage as active external work. Add domain/grain/API regression coverage for a failed current stage with additional pending tasks and pending checks, asserting `rerun-from-stage(current)` behaves like `rerun`: state is replaced, failure clears, locks are released consistently, and no active running work is silently orphaned.
  Verification: Add a workflow with at least two tasks and a check in one stage, fail the first task, call `RerunFromStageAsync(currentStage)`, and verify success plus a new initialized attempt. Also verify the existing active-running-task rejection still fires for actual in-flight work in the invalidation range.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowEventQuerier.cs`, workflow event reads
  Evidence: To filter invalidated attempt history, `ListWorkflowEventsAsync` now ignores the caller's `limit` during storage access and calls `_events.ListAsync(workflowRunId, int.MaxValue, ct)` (`WorkflowEventQuerier.cs:42-44`), then filters and paginates in memory (`WorkflowEventQuerier.cs:45-50`). The previous route passed the requested limit directly into `IEventStore.ListAsync` (`origin/master:WorkflowEventRoutes.cs`), which translated to a bounded SQL `Take(limit)` (`EventStore.cs:92-101`). This restores correctness for low-limit rerun timelines, but it turns every workflow-events request into an unbounded per-run read and JSON materialization. Long-lived workflow runs or runs with many artifact/control events can make a small `?limit=20` request scan and allocate the whole event stream. [disallowed:architectural/performance]
  SuggestedAction: Preserve correctness without unbounded reads. For example, persist/query explicit invalidation markers, query only `StageStarted` markers separately to compute cutoff ids, or add a bounded event-store query that can return the requested page plus the minimal marker context needed for filtering.
  Verification: Add a test or benchmark-style integration check that a low-limit timeline after rerun filters old attempts while the storage query remains bounded, and review SQL/log output or a fake `IEventStore` assertion that `int.MaxValue` is not used for normal timeline reads.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: rerun-from-stage regression coverage
  Evidence: The new coverage exercises the happy path, unknown/never-reached stage, active running work, lock release, runtime variable preservation, CLI body shape, and low-limit timeline filtering. It does not cover the failed-current-stage case where normal initialized-but-unstarted tasks/checks remain pending, which is the main behavioral gap in item-1.
  SuggestedAction: Add focused domain and grain/API tests for failed multi-task and task-plus-check stages before changing the active-work predicate, so the intended distinction between inactive pending control items and true in-flight work is locked down.
  Verification: The new tests should fail on the current candidate and pass after the active-work fix.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: npm dependency audit
  Evidence: The full `npm test` run reported `9 vulnerabilities (3 moderate, 3 high, 3 critical)` during package audit output. This change did not add npm dependencies and the audit output is not specific to rerun-from-stage.
  SuggestedAction: Track dependency audit remediation separately from issue-265.
  Status: pre-existing

## Verification

- `dotnet test Mohist.sln --filter "FullyQualifiedName~RerunFromStage|FullyQualifiedName~CliIssueRerunFromStage|FullyQualifiedName~IssueArchivedDetailApiSpecs|FullyQualifiedName~RunnerWorkflowStatusRouterSpecs"` passed: 59 tests.
- `npm test` passed: server/CLI/web build plus runner tests completed successfully; runner output included expected diagnostic stderr from fake failure scenarios.

<promise>FAIL</promise>
