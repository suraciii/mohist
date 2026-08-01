# Review — Issue 537

## Summary

The implementation externalizes WorkflowRun dispatch snapshots into a separate
`WorkflowDispatchSnapshots` table, reads them via PK lookup on redelivery,
best-effort deletes on terminal transition, and migrates legacy embedded
snapshots at cold start. The design is sound, the code is correct, and both
acceptance criteria (T-001 runtime externalization, T-002 cold-start migration)
are fully met. Build passes with 0 warnings (`TreatWarningsAsErrors`); all
3624 spec + 1726 unit + 51 arch tests pass green.

A prior review found two issues (a vacuous test and a dead interface member).
Both were fixed in commit `4ec12bc15` and verified.

No problems remain that must be fixed before merge.

---

## Prior findings — resolved

### 1. Vacuous test replaced with a real save-failure test

The original `TerminalTransition_SaveFailure_LeavesSnapshotIntact` in
`DispatchSnapshotPersistenceSpecs.cs` never triggered a terminal transition or
simulated a save failure — it merely asserted a snapshot existed right after
dispatch. It was removed and replaced by
`TerminalReport_SaveFailure_LeavesSnapshotIntact` in
`WorkflowGrainStateSaveFailureSpecs.cs:174-210`.

The new test is meaningful: it dispatches a task, stores a snapshot, then
creates a grain backed by a `FailingWorkflowRunStore` (extended to fail on the
events-save path at `:277-283`) and triggers `ReceiveTaskReportAsync`. It
asserts the report throws (`InvalidOperationException`), exactly one event-save
was attempted (`EventSaveAttempts == 1`), and the snapshot survives in the
store — proving `DeleteSnapshotBestEffortAsync` was skipped because
`CommitAsync` threw before reaching it.

### 2. Dead interface member removed

`IDispatchSnapshotStore DispatchSnapshotStore` was removed from
`IWorkflowGrainContext` (`IWorkflowGrainContext.cs`) and its implementation in
`WorkflowGrain.cs`. The grain continues to use its private `_dispatchSnapshotStore`
field directly, which is the correct pattern — the member was never consumed
through the interface. Net diff from base (`0887761a0`) to HEAD on
`IWorkflowGrainContext.cs` is zero (added then removed).

---

## What is solid

- **Store design** (`WorkflowDispatchSnapshotStore.cs`): first-write-wins via
  load-then-insert with `DbUpdateException` reload; best-effort delete tolerating
  `DbUpdateConcurrencyException`; `DeleteForRunAsync` via `ExecuteDeleteAsync`.
  Key consistency is maintained end-to-end: the store key is
  `task.WorkId ?? task.Id` everywhere (grain write, DispatchService read,
  redelivery lookup, upgrader externalization, orphan sweep).

- **Terminal-delete placement** (`WorkflowGrain.Reports.cs`,
  `WorkflowGrain.cs:281-298`): every terminal path captures the workId before
  the transition, commits State, then deletes the snapshot only after a
  successful commit. The `StopAsync` path correctly captures
  `RunningTask?.WorkId` before `AbandonRunningWorkAsync` fails the task.

- **Checks dispatch isolation** (`DispatchService.cs:245-255`): the store
  load/store is gated behind `activeWork.IsTask`; checks fall through to
  `TranslateToDispatchAsync` and never touch the store.

- **Cascade delete** (`WorkflowRunStore.cs:133`): `DeleteForRunAsync` runs after
  the run row is removed.

- **Cold-start upgrader** (`WorkflowDispatchSnapshotDataUpgrader.cs`): preflight
  + backup + single-transaction + idempotent pattern, reusing
  `WorkflowRunStateDataUpgrader.CreateAndVerifyBackupAsync`. Case-insensitive
  status comparison handles the Web JSON enum convention. The sweep
  deserializes canonical State and tests `TaskRunStatus.Running`. INSERT OR
  IGNORE prevents overwriting pre-existing rows. Idempotency verified: second
  run produces zero writes with byte-identical State and unchanged ETag.

- **DatabaseInitializer ordering** (`DatabaseInitializer.cs:23-28`): #536 upgrader
  runs before #537's `ExternalizeAsync`, which runs before `SweepOrphansAsync`.

- **Test coverage**: State-has-no-snapshot, verbatim redelivery after
  deactivation, first-write-wins, completed/failed/retry/stopped snapshot drop,
  checks isolation, cascade delete, save-failure-leaves-snapshot-intact,
  externalization correctness, idempotency, preflight-failure-aborts, backup
  verification, pre-existing-row protection, and orphan sweep are all
  meaningfully tested.

<promise>PASS</promise>
