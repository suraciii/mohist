# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `WorkflowRunStatusPill` returned an "Unknown" pill for null/undefined status, polluting the DOM. Fixed to early-return `null` so the component renders nothing for absent status.
  Verification: `packages/web/src/widgets/issue-workflow/ui/WorkflowRunStatusPill.test.tsx` — two facts assert `container.isEmptyDOMElement()` for null and undefined. Web tests pass (3595/3596).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: missing-obvious-guards
  Evidence: `WorkflowQuerier` deserialized `WorkflowRun` from persisted State JSON without first applying `WorkflowRunStore.MigrateAssignmentJson`, so legacy rows whose runner binding lived under `claim.runnerId` (pre-rename) would fail to surface `AssignedTo` in the querier path. Fixed by introducing `DeserializeWorkflowRun` that pipes through `MigrateAssignmentJson`, aligning with `WorkflowRunQuerier.LoadAsync`.
  Verification: `WorkflowProfileManagerSpecs.WorkflowQuerier_StatusRead_MigratesLegacyClaimAssignment` asserts `AssignedTo` is correctly resolved from a legacy `claim` row. Server focused tests pass (37/37 for status transition + migration + schema).
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: cleanup
  Evidence: `MohistServiceRegistration` carried a `PendingModelChangesWarning` suppression from T-002 (when the model declared the STORED `Status` column ahead of the migration). With T-004 migration landed (`20260702060000_WorkflowRunStatus`) and the snapshot matching the model, the suppression was dead code in production. Removed.
  Verification: `dotnet build` succeeds without the suppression; server tests pass.
  Status: resolved

- [ID: item-4]
  Severity: info
  Scope: test-fix
  Evidence: `ApprovalFeedbackSpecs.RequestChanges_*` tests called `StartTask` without first assigning a runner. Under the new state machine, `StartTask` admits `Ready` or `Running` but not `Pending`, so the tests would fail. Fixed by adding `run.AssignTo("runner-1", DateTimeOffset.UtcNow)` before `StartTask`, which transitions `Pending → Ready`, satisfying the guard.
  Verification: `ApprovalFeedbackSpecs` (all 13 facts) pass with updated status assertions (`Ready` instead of `Running` post-`RequestChanges`).
  Status: resolved

- [ID: item-5]
  Severity: info
  Scope: test-fix
  Evidence: `WorkflowRunStatusReclassificationMigrationSpecs` previously called `db.Database.MigrateAsync()` to set up the pre-migration schema, which would apply ALL migrations including the one under test, leaving no pre-migration state to reclassify. Fixed to use `MigrateToPreviousAsync` targeting `20260629120000_BackfillIssueCompletedAt` — the migration immediately before the status migration.
  Verification: All 9 reclassification facts pass, each seeding old-vocabulary rows and asserting the STORED column mirrors the reclassified value.
  Status: resolved

## Blocking Items

(None.)

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/` (all partial files)
  Evidence: Per design D2, the 19 scattered `run.Status =` write sites were audited to the new machine but the guards remain ad-hoc per-site. The `HasInFlightWork`, `HasDispatchableWork`, `WaitingForDispatchStatus`, and `ActiveOrWaitingForDispatchStatus` helpers reduce duplication but do not enforce the transition contract — a new write site could still set an invalid status.
  SuggestedAction: Introduce a centralized `WorkflowRun.Transition(command)` or `CanTransition(from, to)` in a follow-up issue, as flagged in design D2 Open Questions. This change deliberately scoped it out to manage blast radius.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/Shared.cs:64-65`
  Evidence: `WaitingForDispatchStatus` and `ActiveOrWaitingForDispatchStatus` are `private static` helpers on `WorkflowRunExtensions`. They are used by 8 of the 19 write sites but not all (e.g., `MarkChecksRunning` in `WorkflowGrain.cs:419` still hard-codes `_run!.Status = WorkflowRunStatus.Running`). The inconsistency is intentional (checks dispatch ≠ completion re-evaluation) but a future reader could accidentally use the hard-coded pattern where the helper is needed.
  SuggestedAction: Consider making the two helpers `public` on the `extension(WorkflowRun run)` block so all write sites that re-evaluate dispatch state funnel through them, or add a comment in `MarkChecksRunning` explaining why it hard-codes `Running` rather than using `ActiveOrWaitingForDispatchStatus`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-8]
  Severity: warning
  Scope: `packages/runner/tests/executor-workspace-boundary.spec.ts`, `packages/runner/tests/workspace-prepare.spec.ts`, `packages/runner/tests/workspace.spec.ts`
  Evidence: Runner test suite exhibits intermittent flakiness — 2 tests fail sporadically in parallel runs (observed: `FirstDispatchPrepares_ThenReentriesReuseWithoutRecloning`, `FastPass_CleanRealGitWorkspace_CompletesUnderOneSecond`, `MidRebaseCrash_ReentryRecoversAndPreservesRunBranch`). These failures use real git operations with real timers and are unrelated to the workflow status vocabulary change. The full suite passes cleanly on re-run.
  SuggestedAction: Investigate and stabilize flaky runner tests in a separate issue. Not blocking this change.
  Status: pre-existing

- [ID: item-9]
  Severity: warning
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs` — `DeliveryTimeMetrics_*` (4 tests)
  Evidence: Four `DeliveryTimeMetrics_*` tests fail in parallel runs of the full integration suite due to a shared-state issue with the fake time provider. Same failures reproduce on the master branch (`git stash` verified). Self-review documents this at `progress.txt:132-139`.
  SuggestedAction: Fix shared time-provider state in a separate issue.
  Status: pre-existing

---

## Acceptance Criteria Verification

| AC | Description | Evidence | Verdict |
|---|---|---|---|
| AC1 | Enum values = `Created, Pending, Ready, Running, AwaitingApproval, Paused, Stopped, Completed, Failed` | `WorkflowRun.cs:15` — enum definition. `WorkflowRunStatusTransitionSpecs.WorkflowRunStatus_ValuesMatchSingleWaitingObjectVocabulary` asserts exact member list. | PASS |
| AC2 | All transition points follow new machine | Domain state machine audited across all 19 write sites in `WorkflowRun.Lifecycle.cs`, `Task.cs`, `Check.cs`, `Failure.cs`, `Work.cs`, `Assignment.cs`, `Approval.cs`, `Stage.cs`, and grain-side `MarkChecksRunning` (`WorkflowGrain.cs:419`). 15 facts in `WorkflowRunStatusTransitionSpecs.cs` cover Start, AssignRunner, StartTask, CompleteTask-with-remaining, CompleteTask-no-remaining, Pause, Resume-both-branches, Approve, Fail, Stop, Retry, Rerun, RerunFromStage. | PASS |
| AC3 | STORED `status` computed column | `MohistDbContext.cs:382` — `HasComputedColumnSql("LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))", stored: true)`. `WorkflowRunStatusSchemaMigrationSpecs.DbContext_ExposesStatusComputedColumnOnWorkflowRunRow` asserts exact SQL expression. Migration `20260702060000_WorkflowRunStatus.cs` materializes the column via two-step SQLite pattern. | PASS |
| AC4 | Scheduling queries filter at DB layer | `WorkflowRunQuerier.FindAssignableAsync` (`:71-73`) — `Where(row => row.Status == StatusString(Pending) && row.AssignedRunnerId == null)`. `FindAssignedToAsync` (`:51`) — `Where(row => row.Status == StatusString(Ready) && row.AssignedRunnerId == runner)`. `QuerierSchedulingSpecs` (9 facts) assert correct filtering, exclusion of non-matching statuses, and no in-memory deserialization of non-matching rows. | PASS |
| AC5 | Historical data reclassification | Migration SQL (`20260702060000_WorkflowRunStatus.cs:124-201`) covers 4 scenarios: old-pending→created, old-running+unassigned→pending, old-running+inflight→running, old-running+assigned+idle→ready. `WorkflowRunStatusReclassificationMigrationSpecs` (9 facts) seeds each shape and asserts reclassified value in both `State.status` and the STORED column. Activation-time shim `ReconcileReadyStatusWithInFlightWork()` (`Shared.cs:55-61`) is the runtime backstop. | PASS |
| AC6 | UI distinguishes pending-claim vs assigned-waiting | `WorkflowRunStatusPill.tsx` renders `pending` as violet "Pending runner", `ready` as cyan "Ready to run", `running` as blue "Running" — with distinct testids and color tokens. `IssueDetailPage.test.tsx` (3 integration facts) asserts the pill renders in the detail header for each status. `WorkflowRunStatusPill.test.tsx` (13 facts) asserts distinct presentation per status. | PASS |
| AC7 | Existing tests updated; new transition tests added | All server workflow/grain/querier specs updated to new vocabulary (`StatusSpecs`, `BacklogSpecs`, `BoundarySpecs`, `PausingWorkSpecs`, `DispatchAndLoadingSpecs`, `WorkflowRetrySpecs`, etc.) — 948 pass. 37 new status-transition + migration + schema + scheduling + frontend-mapper facts pass. | PASS |
| AC8 | Poll loop removes `GetCurrentWorkIdAsync` busy check | `RunnerGrain.PollAssignedOrAssignableWorkflowAsync` (`RunnerGrain.cs:691`) iterates `FindAssignedToAsync` rows and calls `PollOneWorkflowAsync` directly — no `GetCurrentWorkIdAsync` call. `RunnerPollSchedulingSpecs` (5 facts) assert structural absence of the symbol. | PASS |

## Spec Compliance

### workflow-run-lifecycle (ADDED)

All 7 requirements verified:

- **Enumeration captures single waiting object** — `WorkflowRunStatus` at `WorkflowRun.cs:15` with 9 values matching the vocabulary table. Unit test `WorkflowRunStatus_ValuesMatchSingleWaitingObjectVocabulary` pins the exact member list.
- **Status transitions follow single state machine** — All transition points (acceptance criteria AC2) verified against the spec transition graph.
- **Status persisted as STORED computed column** — Model declaration (`MohistDbContext.cs:382`) + migration (`20260702060000_WorkflowRunStatus.cs`) + schema spec (`WorkflowRunStatusSchemaMigrationSpecs`).
- **Runner scheduling queries filter at DB layer** — `WorkflowRunQuerier.FindAssignableAsync`/`FindAssignedToAsync` use STORED column. `CountRunningAssignedToAsync` covers the `ActiveWorkflowCountAsync` regression.
- **Poll loop picks up Ready without busy pre-check** — `RunnerGrain.cs:691-696` drops the `GetCurrentWorkIdAsync` call.
- **Historical reclassification** — Migration SQL + `WorkflowRunStatusReclassificationMigrationSpecs`.
- **Web UI distinguishes pending from ready** — `WorkflowRunStatusPill` + `IssueDetailPage` integration.

### runner-workspace-cleanup (MODIFIED)

- `WorkflowRunStatusName` union widened to include `Created` and `Ready` (`workflow-terminal-status.ts:29-38`). `TERMINAL_WORKFLOW_STATUSES` unchanged (3 values).
- Convergence backstop tested for `Created`, `Pending`, `Ready` (`cleanup-convergence.spec.ts` — 3 new facts).
- SignalR push handler non-terminal vocab extended to full D1 set (`runner-signalr-workflow-status.spec.ts`).
- `isTerminalWorkflowStatus` negative tests for `Created` and `Ready` (`workflow-terminal-status.spec.ts` — 2 new facts).

## Cross-cutting Verification

- **Security**: No new input validation surfaces. The `WorkflowRunStatusPill` renders server-provided strings defensively (unknown values fall through to gray pill; null/undefined renders nothing). Status values are enum-derived, not user-controlled.
- **Data safety**: Migration is idempotent (tested by `Up_ReclassificationIsIdempotent`). The STORED column is automatically recomputed on every `State` write. The activation-time shim catches residual misclassification. No data loss risk.
- **Public contracts**: `WorkflowRunStatus` is an internal server enum with no external API contract dependency on integer ordinals (enums serialize as strings). The runner-side `WorkflowRunStatusName` union is widened but `TERMINAL_WORKFLOW_STATUSES` is untouched — backward-compatible.
- **Migration impact**: Single migration adds STORED column + index + reclassifies historical data. Table rebuild is O(n) where n is the `WorkflowRuns` row count (expected to be modest). Forward-only by design per active-development policy.

<promise>PASS</promise>
