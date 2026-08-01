# Review — Issue 537

## Summary

The implementation is well-structured and faithful to the design. The core
mechanism — separating dispatch snapshots into a `WorkflowDispatchSnapshots`
table, reading them via PK lookup on redelivery, best-effort deleting on
terminal transition, and migrating legacy embedded snapshots at cold start —
is correct and matches every acceptance criterion at the code level. The build
passes with 0 warnings (`TreatWarningsAsErrors`); the DispatchSnapshot spec and
unit suites pass green on the final tree.

One test is vacuous — it claims to verify an acceptance criterion that it does
not exercise. That is the sole blocking finding.

---

## Findings

### 1. [Must Fix] Vacuous test: `TerminalTransition_SaveFailure_LeavesSnapshotIntact`

**Where:** `packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/Grain/DispatchSnapshotPersistenceSpecs.cs:206-221`

**What is wrong:** The test claims to verify acceptance criterion T-001 #8
("An in-memory terminal transition whose State save fails (MarkRunReloadRequired)
does NOT delete the snapshot"). The test body does none of that:

1. It starts a workflow and polls work (dispatches a task).
2. It opens a scope, loads the snapshot, and asserts `Assert.NotNull(stored)`.

It never triggers a terminal transition (no `ReportAsync` / `FailActiveWorkAsync`
/ `StopAsync` call), never simulates a State save failure (no failing store
injection), and never verifies that the snapshot survives a failed terminal
commit. The assertion — "snapshot exists right after dispatch" — is trivially
true and always passes regardless of whether the delete-on-save-failure logic
is correct. If someone later moves `DeleteSnapshotBestEffortAsync` before
`CommitAsync` (breaking the invariant), this test will not catch it.

The code itself is correct: `DeleteSnapshotBestEffortAsync` is called only
after `CommitAsync` returns in every terminal path
(`WorkflowGrain.Reports.cs:33-35`, `55-56`, `73-74`; `WorkflowGrain.cs:295-297`),
and `CommitAsync` → `SaveRunAsync` calls `MarkRunReloadRequired` + rethrows on
`DbUpdateConcurrencyException` (`WorkflowGrain.cs:698-712`), so the delete is
skipped on save failure. But the test does not verify this.

**How to fix:** Inject a failing `IWorkflowRunStore` (the `FailingWorkflowRunStore`
pattern already exists at `WorkflowGrainStateSaveFailureSpecs.cs:213-241`),
trigger a terminal report (e.g. `ReceiveTaskReportAsync` with a Succeeded report),
assert the report call throws (save failure), then verify the snapshot is still
present in `IDispatchSnapshotStore` — proving the delete was skipped because
`CommitAsync` threw before reaching `DeleteSnapshotBestEffortAsync`.

### 2. [Low] Dead interface member: `IWorkflowGrainContext.DispatchSnapshotStore`

**Where:** `packages/server/src/Mohist.Server/Workflow/Grains/IWorkflowGrainContext.cs:20`

**What is wrong:** `IDispatchSnapshotStore DispatchSnapshotStore { get; }` was
added to `IWorkflowGrainContext` and implemented at `WorkflowGrain.cs:90`, but
no consumer of `IWorkflowGrainContext` ever reads it. `WorkflowWorkLifecycle`
(the main consumer) does not access it; the grain uses its private
`_dispatchSnapshotStore` field directly for all store operations
(`StoreActiveWorkDispatchAsync`, `DeleteSnapshotBestEffortAsync`). The member
is dead code on the interface.

**Recommendation:** Remove the property from `IWorkflowGrainContext` and its
implementation, or move the snapshot-delete calls into `WorkflowWorkLifecycle`
if the intent was to centralize terminal-transition side effects through the
context. Either way, the interface should not expose members nothing reads.

---

## What is solid

- **Store design** (`WorkflowDispatchSnapshotStore.cs`): first-write-wins via
  load-then-insert with `DbUpdateException` reload is correct; best-effort
  delete tolerating `DbUpdateConcurrencyException` is correct; `DeleteForRunAsync`
  via `ExecuteDeleteAsync` is efficient. Key consistency is maintained end-to-end:
  the store key is `task.WorkId ?? task.Id` everywhere (grain write at
  `WorkflowGrain.cs:505`, DispatchService read at `DispatchService.cs:247`,
  redelivery lookup at `WorkflowRun.Work.cs:218`, upgrader externalization at
  `WorkflowDispatchSnapshotDataUpgrader.cs:365`, sweep at `:183`).

- **Terminal-delete placement** (`WorkflowGrain.Reports.cs`, `WorkflowGrain.cs:281-298`):
  every terminal path captures the workId before the transition, commits State,
  then deletes the snapshot only after a successful commit. The `StopAsync` path
  correctly captures `RunningTask?.WorkId` before `AbandonRunningWorkAsync` fails
  the task (`WorkflowGrain.cs:290`).

- **Checks dispatch isolation** (`DispatchService.cs:245-255`): the store
  load/store is gated behind `activeWork.IsTask`; checks fall through to
  `TranslateToDispatchAsync` and return without touching the store. Verified by
  `ChecksDispatch_DoesNotPersistSnapshotAndRedeliveryReconstructs`.

- **Cascade delete** (`WorkflowRunStore.cs:133`): `DeleteForRunAsync` runs after
  the run row is removed, matching the design's "store explicit delete" decision.

- **Cold-start upgrader** (`WorkflowDispatchSnapshotDataUpgrader.cs`): the
  preflight + backup + single-transaction + idempotent pattern is correctly
  implemented and reuses `WorkflowRunStateDataUpgrader.CreateAndVerifyBackupAsync`.
  The case-insensitive status comparison (`OrdinalIgnoreCase` at `:362`) correctly
  handles the Web JSON enum convention (`"running"`). The sweep deserializes
  canonical State and tests `TaskRunStatus.Running` (`:181`), avoiding the
  case-sensitivity trap. INSERT OR IGNORE prevents overwriting pre-existing rows.
  Idempotency is verified: second run produces zero writes and byte-identical
  State with unchanged ETag.

- **DatabaseInitializer ordering** (`DatabaseInitializer.cs:23-28`): #536 upgrader
  runs before #537's `ExternalizeAsync`, which runs before `SweepOrphansAsync`.
  The shared `MohistDbContext` is safe: preflight uses `AsNoTracking`, the write
  phase re-queries with tracking, and the sweep uses `AsNoTracking` reads +
  `ExecuteDeleteAsync` (bypasses change tracker).

- **Migration + DbContext** (`20260801092137_AddWorkflowDispatchSnapshots.cs`,
  `MohistDbContext.cs:887-893`): table schema matches the design (PK
  `(WorkflowRunId, WorkId)`, `WorkflowRunId` MaxLength 50 consistent with
  `WorkflowRunRow`).

- **Test coverage** (excluding Finding 1): externalization correctness, idempotency,
  preflight-failure-aborts, backup verification, pre-existing-row protection,
  orphan sweep, State-has-no-snapshot, verbatim redelivery after deactivation,
  first-write-wins, completed/failed/retry/stopped snapshot drop, checks
  isolation, and cascade delete are all meaningfully tested.

<promise>FAIL</promise>
