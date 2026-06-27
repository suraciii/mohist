# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs
  Evidence: `CheckWorkTimeoutsAsync` snapshots `_activeWorks` at lines 112-128, but each timed-out entry calls `ReconfirmOutstandingAsync`, which reads `RunnerWorks` through `_runnerWorks.FindAsync` at lines 734-748. This violates issue AC #2 / spec lines 89-93 and 125-129: reminder ticks must scan only hydrated memory and perform zero DB reads. The workflow artifact `progress.txt` also records this as satisfied at lines 56-58, but the code contradicts it. [disallowed:product behavior/spec compliance]
  SuggestedAction: Make the timeout reminder path memory-only. If the report path is the authority, update in-memory terminal state before/remove-on-report and rely on that snapshot/reconfirm; keep DB reads out of the tick path. Add a test or fake store assertion that `CheckWorkTimeoutsAsync`/`ReceiveReminder` does not call `FindAsync`/DB reads during scanning.
  Verification: `dotnet build Mohist.sln -p:SkipWebBuild=true` passed; targeted `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~RunnerWorkLedgerSpecs` passed. Manual code inspection shows the DB read remains open.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs
  Evidence: On normal workflow reports, `ReportWorkflowResultAsync` mutates the owner grain at lines 286-294, removes `_activeWorks` at line 296, and only then updates the ledger at lines 299-307. Agent-job reports have the same order at lines 389-402. If `TryMarkTerminalAsync` throws or returns false after the owner has accepted the report, the in-memory row is gone while the persisted `RunnerWorks` row remains `outstanding`; after activation it can be rehydrated as active and later timeout/runner-loss synthesis will target already-terminal owner state. This breaks the spec invariant that terminal update is authoritative and that outstanding rows are closed on report. [disallowed:data safety/behavior change]
  SuggestedAction: Close or guard the ledger transition in a recoverable order. At minimum, do not drop in-memory active state until durable terminal closeout succeeds, handle false returns explicitly, and add regression coverage for DB closeout failure/missing row so stale `outstanding` rows cannot survive successful owner reports.
  Verification: `dotnet build Mohist.sln -p:SkipWebBuild=true` passed; no existing test injects a ledger update failure or false return, so this remains unverified and open.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs
  Evidence: Work take paths insert `RunnerWorks` rows before reminder registration (`AssignAgentJobAsync` lines 249-260, `PollOneWorkflowAsync` lines 632-643). `EnsureWorkTimeoutReminderAsync` catches and suppresses `RegisterOrUpdateReminder` failures at lines 705-717. `OnActivateAsync` hydrates outstanding rows at lines 67-70 but does not ensure the reminder exists for those rows. A process crash or reminder-service failure after ledger insert can leave durable `outstanding` work without the persisted wakeup required by issue AC #4 / spec lines 108-123. [disallowed:recovery behavior]
  SuggestedAction: Make reminder registration part of the durable outstanding-work invariant. Either fail/reject work take when the reminder cannot be registered, or guarantee activation with outstanding rows registers/repairs the reminder. Add coverage where a grain reactivates from an outstanding row and the reminder lifecycle is verified without manually calling `RegisterAsync` or the test hook.
  Verification: Targeted runner ledger specs pass because they call `CheckWorkTimeoutsAsync` manually; they do not cover missing reminder registration after insert or activation repair.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs
  Evidence: `UnregisterAsync` calls `NotifyTrackedWorkflowRunnersLostAsync` and then clears `_activeWorks` unconditionally at lines 169-172. Inside `NotifyTrackedWorkflowRunnersLostAsync`, per-work synthesis exceptions are caught and logged at lines 563-578. If one workflow/agent-job report or ledger closeout fails transiently, the entry is still dropped by the unconditional clear while its ledger row may remain `outstanding`; a later timeout scan starts from an empty memory set and can unregister the reminder. [disallowed:data safety/behavior change]
  SuggestedAction: Track synthesis success per entry and only remove/clear successfully terminal works. Leave failed closeouts active for retry or ensure activation rehydrates and re-registers timeout supervision.
  Verification: No changed test covers partial runner-loss synthesis failure. `RunnerLoss_*` tests cover only the all-success path.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs
  Evidence: Normal report paths drain `_activeWorks` but do not unregister the `work-timeout` reminder when the last item reports successfully (`ReportWorkflowResultAsync` line 296, `ReportAgentJobResultAsync` lines 391-402). The lazy lifecycle in design lines 139-144 and task T-003 requires unregister-on-drain; current code only unregisters from `CheckWorkTimeoutsAsync` when it later wakes with an empty set (lines 102-105, 141-142) or from synthesized agent-job failure (lines 807-808). This leaves persisted reminders waking idle runner grains until a later tick self-cleans. [disallowed:workflow/reminder behavior]
  SuggestedAction: After successful workflow or agent-job report closeout, call the same drain cleanup used by timeout synthesis when `_activeWorks.Count == 0`. Add a test that normal completion removes the reminder or at least exercises the cleanup method.
  Verification: Code inspection; targeted tests do not assert reminder unregistration after successful reports.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs; packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs
  Evidence: After runner grain reactivation, `HydrateOutstandingWorksAsync` restores agent-job rows with `Dispatch: null` at lines 73-82. `DequeueAssignedAgentJobAsync` only returns agent-job work when `Dispatch` is non-null (lines 654-682), so a reconnecting runner cannot poll an outstanding agent-job after server restart before the timeout. A normal late runner report can also be rejected because `AgentJobGrain` is in-memory and rejects `ReportResultAsync` unless it is still `Running` with matching runner/work (AgentJobGrain lines 120-145); `ReportAgentJobResultAsync` only closes the ledger when the report is both tracked and accepted (RunnerGrain lines 385-403). The candidate therefore can turn a completed agent-job into an eventual timeout after reactivation. [disallowed:product behavior]
  SuggestedAction: Decide and implement a clear post-reactivation agent-job contract: either persist enough dispatch/job state to accept/poll/report outstanding agent jobs, or fail hydrated agent-job rows immediately and accurately. Add a restart test where an assigned agent-job reports after runner grain and agent-job grain reactivation.
  Verification: Existing tests cover agent-job timeout/loss by manually assigning synthetic work and do not cover late normal report after reactivation.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Runner/RunnerWorkStore.cs; packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs
  Evidence: `RunnerWorkStore.InsertOutstandingAsync` always inserts a new row (lines 15-20), the schema has only non-unique indexes on `(RunnerId, OwnerKind, OwnerId, WorkId)` (MohistDbContext lines 512-513; migration lines 33-41), and terminal closeout updates only the latest outstanding row (RunnerWorkStore lines 36-44 and 85-93). `HydrateOutstandingWorksAsync` skips duplicate active keys in memory (RunnerGrain lines 75-82). If duplicate outstanding rows are created by retry/reentrancy for the same work key, older rows can remain `outstanding` indefinitely and later be rehydrated or confuse operational ledger history. [disallowed:data model/consistency]
  SuggestedAction: Add idempotency around active-row insert/update. Use a partial unique index where feasible, or have insert detect/reuse an existing outstanding row for the same runner/owner/work and terminal closeout close all matching active rows. Add a duplicate-row regression test.
  Verification: No existing test creates duplicate outstanding rows or verifies active-row uniqueness.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain
  Evidence: Timeout/restart tests call `CheckWorkTimeoutsAsync` directly (`RunnerFailureSpecs.cs:112`, `RunnerWorkLedgerSpecs.cs:225,245,274,326,360`). The reactivation test also manually calls `RegisterAsync` before the scan (`RunnerWorkLedgerSpecs.cs:223-225`). These tests would still pass if `RegisterOrUpdateReminder`, reminder persistence, reminder name, or `ReceiveReminder` were broken, so issue AC #2 and #4 are not actually proven. The issue specifically requires the persisted Orleans reminder to reactivate the grain after server/runner sync restart. [disallowed:test strategy]
  SuggestedAction: Add coverage for the real reminder path. At minimum, inspect the reminder service after take to verify a `work-timeout` reminder exists, invoke `ReceiveReminder` rather than only the public hook, and add an integration-style test where an outstanding row survives deactivation and the reminder mechanism, not manual registration, wakes/scans it.
  Verification: `dotnet test ... --filter FullyQualifiedName~RunnerWorkLedgerSpecs` passed, confirming current tests are green but not sufficient for this acceptance criterion.
  Status: open

- [ID: item-9]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/RunnerWorkLedgerSpecs.cs
  Evidence: Several TimeProvider acceptance checks assert only `NotNull`/`NotEqual` for `FinishedAt` (`RunnerWorkLedgerSpecs.cs:141,205,332`). Those assertions would pass if `FinishedAt` used `DateTimeOffset.UtcNow` instead of the injected fake clock, which weakens issue AC #9 for terminal timestamps. [disallowed:test expectation quality]
  SuggestedAction: Advance `FakeTimeProvider`, capture the expected fake time, and assert `FinishedAt == expected` for normal report, runner-loss, and timeout synthesis rows.
  Verification: Current targeted specs pass but do not prove fake-clock terminal time.
  Status: open

- [ID: item-10]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain
  Evidence: The issue AC #7 / spec lines 101-106 require recovery/new attempts to insert a fresh `RunnerWorks` row with its own deadline. The changed tests cover initial take, report, hydrate, timeout, and runner-loss, but no rerun/recovery path asserts a timed-out task's later attempt gets a new row and `TakenAt`. [disallowed:test coverage]
  SuggestedAction: Add a workflow recovery/rerun test that times out an attempt, triggers recovery/new attempt, polls it, and asserts a second `RunnerWorks` row with a new work identity or new attempt deadline and no inherited failed state.
  Verification: No matching test was found in the changed runner specs.
  Status: open

- [ID: item-11]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/RunnerFailureSpecs.cs
  Evidence: `RunnerUnregisters_IsIdempotentForRunnerLoss` is named and commented as a double-unregister regression, but it calls `UnregisterAsync` only once at line 86 and then asserts the first failure state. It does not test idempotency. [disallowed:test expectation quality]
  SuggestedAction: Call `UnregisterAsync` twice and assert no second synthesis/ledger mutation occurs, or rename/remove the test if that scenario is no longer meaningful.
  Verification: Code inspection; no current assertion exercises a second unregister.
  Status: open

- [ID: item-12]
  Severity: warning
  Scope: full server test suite
  Evidence: `npm test` failed in the post-review snapshot: `RunnerFailureSpecs.StoppedWorkflow_KeepsAssignment_AndRunnerDropsPendingWork` timed out after 30s waiting for `IManagementGrain.ForceActivationCollection` during `WorkflowGrainSpecs.ClearBacklogAsync`. A single rerun of just that test passed, so this may be suite-level flakiness, but the required full server suite was not green in the review run.
  SuggestedAction: Investigate the full-suite management timeout and rerun `npm test` after fixes. If it is pre-existing/flaky, quarantine or harden the test fixture separately, but do not count the candidate as fully verified until the full suite passes.
  Verification: `npm test` failed with 1 failed, 2845 passed, 13 skipped. Single-test rerun with `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~RunnerFailureSpecs.StoppedWorkflow_KeepsAssignment_AndRunnerDropsPendingWork` passed.
  Status: open

## Follow-up Items

- [ID: item-13]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Runner/RunnerWorkStore.cs; database schema
  Evidence: The table has no check constraint for `Status`/`OwnerKind`, and `ParseStatus` maps unknown status strings to `RunnerWorkStatus.Outstanding` at lines 132-138. Corrupt or future values would be treated as active work, which is the least safe fallback for a ledger that drives synthesized failures. This is not necessarily introduced by normal code paths, but it is a useful hardening improvement.
  SuggestedAction: Add check constraints or strict parsing that fails closed/logs invalid rows instead of treating them as outstanding.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-14]
  Severity: info
  Scope: package audit output
  Evidence: Test/build commands that invoke the web build reported `npm audit` findings: 9 vulnerabilities (3 moderate, 3 high, 3 critical) and pending `allow-scripts` warnings. This appears unrelated to the server runner supervision change and was not investigated as part of this review.
  SuggestedAction: Track dependency audit cleanup separately.
  Status: out-of-scope

<promise>FAIL</promise>
