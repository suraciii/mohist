## Context

RunnerGrain drives work-completion timeout supervision through a single per-runner Orleans reminder named `work-timeout` (period 1 min, persisted in `OrleansRemindersTable`). The reminder ticks `CheckWorkTimeoutsAsync`, which walks the in-memory outstanding set, and for any work where `now - CreatedAt > WorkCompletionTimeout` synthesizes `WorkResult(status="failed", reason="timeout")` via the existing `ReportWorkflowResultAsync` channel. The reminder is unregistered when a scan observes no pending/running work (drain behavior).

**The bug.** Both `PollOneWorkflowAsync` (RunnerGrain.cs:753) and `AssignAgentJobAsync` (RunnerGrain.cs:316) end by calling `EnsureWorkTimeoutReminderAsync`, which today is a thin wrapper over `this.RegisterOrUpdateReminder(...)` (RunnerGrain.cs:868). `RegisterOrUpdateReminder` resets the reminder's *due-time* to the full period on **every** call. Because there is a single runner-level reminder covering all outstanding work, every new assignment pushes the next tick later for *every* outstanding item — including older work closer to its deadline. Under sustained dispatch a work item can remain "running" well past the 30 min `WorkCompletionTimeout`, delaying failure synthesis, retry, and workflow convergence.

This violates the domain invariant stated in the issue: each work's deadline is derived from its own `TakenAt`/`CreatedAt`, and the reminder is only the mechanism that *checks* those deadlines — it must not be a delay that restarts on every assignment.

Stakeholders / constraints:
- Touched code is shared by all outstanding runner work (workflow work + agent jobs), so regressions affect failure convergence and retry across the whole system (issue labeled `risk: medium`).
- Reminder persistence already covers the server+runner restart "orphan work" case (issue #275); that behavior must remain intact.
- Reentrant grain + concurrent report: the scan already snapshots the active set and re-confirms outstanding before synthesizing (RunnerGrain.cs:126, :159); unchanged here.

## Goals / Non-Goals

**Goals:**
- Make `EnsureWorkTimeoutReminderAsync` register-if-absent: register the `work-timeout` reminder only when outstanding work transitions zero → non-zero; never reset its due-time while it already exists.
- Preserve the existing per-work deadline basis (`CreatedAt`/`TakenAt`) and synthesis path (`failed`/`reason=timeout` via `ReportWorkflowResultAsync`) unchanged.
- Preserve the drain-side unregister behavior (reminder released when a scan observes no pending/running work).
- Add tests that assert **reminder scheduling behavior** (register-once vs re-register), not just the side effects of calling `CheckWorkTimeoutsAsync` directly.
- Add tests for: older outstanding work still timing out when newer work is assigned before its deadline; reminder lifecycle across drain → new-work reappearance.

**Non-Goals:**
- Do not change the default `WorkCompletionTimeout` (30 min) or the reminder period (1 min).
- Do not redesign runner concurrency, work leasing, or `MaxWorkflowSlots`.
- Do not change the reminder name, the `RunnerWorks` ledger schema, or any config/API surface.
- Do not change GitHub PR action semantics.
- Do not address the unrelated stale-heartbeat `InvalidCastException` log noise.

## Decisions

### Decision 1 — `EnsureWorkTimeoutReminderAsync` becomes register-if-absent

Change the helper to check the reminder's existence before registering, instead of unconditionally calling `RegisterOrUpdateReminder`:

```csharp
private async Task EnsureWorkTimeoutReminderAsync()
{
    try
    {
        if (await this.GetReminder(WorkTimeoutReminderName) is not null)
            return;                       // already ticking — do NOT reset due-time
        await this.RegisterOrUpdateReminder(
            WorkTimeoutReminderName,
            WorkTimeoutReminderPeriod,
            WorkTimeoutReminderPeriod);
    }
    catch (Exception ex)
    {
        _log.LogWarning(ex, "Runner {RunnerId} failed to register work-timeout reminder", RunnerId);
    }
}
```

Call sites (`PollOneWorkflowAsync`, `AssignAgentJobAsync`) are unchanged — they still call `Ensure…` after recording outstanding work; the helper now no-ops when the reminder already exists.

**Rationale.** `GetReminder` + `RegisterOrUpdateReminder` is the minimal, idiomatic Orleans pattern for register-if-absent. It keeps the call sites honest (they still "ensure" a reminder exists) while removing the due-time reset. The `RegisterOrUpdateReminder` API is intentionally retained for the *initial* register because it is the documented create-or-update primitive; the guard is what makes it idempotent for our needs.

**Alternatives considered.**
- *Track reminder presence in an in-memory flag* (e.g. `_workTimeoutReminderRegistered`). Rejected: a flag would desync from the reminder table on grain reactivation (the persisted reminder re-fires `ReceiveReminder`, but the flag resets to `false`), re-introducing the "reset on every activation" smell and forcing reactivation-time reconciliation logic.
- *Keep `RegisterOrUpdateReminder` but pass a near-zero due-time.* Rejected: the API still updates the row; the point of the fix is to *not touch* the reminder when it already exists, so its existing tick schedule is observable and stable.

### Decision 2 — Leave the drain-side unregister path as-is

`MaybeUnregisterWorkTimeoutReminderAsync` (RunnerGrain.cs:883) and its three call sites in `CheckWorkTimeoutsAsync` (:146, :176) and `ReportWorkflowResultAsync` (:405) already do `GetReminder` → `UnregisterReminder`. This is the existing drain behavior; the issue explicitly requires preserving it. No change.

This keeps the symmetry the issue describes: reminder lifecycle is driven by outstanding-work *presence* (zero ↔ non-zero), not by assignment events.

### Decision 3 — Tests assert reminder scheduling, not just scan output

Acceptance criterion #7 forbids asserting only via direct `CheckWorkTimeoutsAsync` calls. Two complementary verification strategies:

1. **Query the reminder table directly.** The test fixture (`WorkflowGrainFixture`) already resolves silo services via `Cluster.GetSiloServiceProvider(null)` and the silo uses `UseInMemoryReminderService()`. Tests resolve `IReminderService` (or `IReminderTable`) from the silo provider and assert:
   - After the first assignment: a `work-timeout` reminder row exists for the runner grain id.
   - After a second assignment while the reminder exists: the row's `StartAt` is **unchanged** (proves no due-time reset).
   - After drain: the row is gone.
   - After drain + new work: a row exists again (fresh `StartAt`).

2. **End-to-end behavioral assertion** for the older-work scenario: poll W1, advance fake clock near (but under) `WorkCompletionTimeout`, assign W2, then advance just past W1's deadline and drive the reminder tick (`ReceiveReminder` or a small time advance in the in-memory reminder service). Assert W1 synthesizes `failed(reason=timeout)` and W2 remains outstanding — proving W2's assignment did not postpone W1's judgment.

The first strategy is the precise, intent-revealing check for the actual regression (due-time drift); the second guards the user-visible behavior.

### Decision 4 — No state, schema, or config changes

The fix is purely in reminder-registration control flow. `WorkCompletionTimeout` (default 30 min), the reminder name/period, `RunnerWorksState`, the `RunnerWorks` ledger, and the `failed/timeout` synthesis result are all unchanged. This keeps the blast radius to one method body plus new tests.

## Risks / Trade-offs

- [Reminder now fires on a strict 1-min cadence regardless of dispatch bursts] -> Acceptable and intended. Worst-case detection latency for an already-timed-out item is one reminder period (~1 min), independent of how many new items are assigned. Previously the latency could grow unboundedly under sustained dispatch; now it is bounded.
- [`GetReminder` on every assignment adds one reminder-table read per assignment] -> Negligible: assignments are low-frequency relative to the reminder period, the reminder table is in-process/in-memory in tests and a single row in prod, and `GetReminder` is already used on the unregister path. The read is local to the silo hosting the grain.
- [Stale persisted reminder after a silo crash mid-unregister] -> Pre-existing condition, unchanged by this fix. The drain path's `GetReminder`→`UnregisterReminder` is already best-effort with logged failures; a stray reminder that fires with no outstanding work hits the existing `snapshot.Count == 0 -> MaybeUnregister` branch and self-cleans on the next tick.
- [Test relying on `IReminderService`/in-memory reminder table internals] -> Coupling is to a stable Orleans abstraction, and only in the test layer. Mitigated by pairing the reminder-table assertion with the end-to-end behavioral assertion (Decision 3) so a refactor of the assertion helper cannot mask a real regression.

## Migration Plan

1. Modify `EnsureWorkTimeoutReminderAsync` per Decision 1 (single-file change in `RunnerGrain.cs`). No data migration.
2. Add the test coverage from Decision 3 to `RunnerWorkLedgerSpecs.cs` / `RunnerFailureSpecs.cs` (+ a small helper to read the reminder table from the fixture, if not already present).
3. Run `npm test` (server); `npm run typecheck -w packages/web` and runner typecheck/tests are not affected (no web/runner change) but run as part of CI gating.
4. Deploy via the normal `mo update server` managed-restart path. On restart, currently-outstanding runners rehydrate their outstanding set in `OnActivateAsync` → `HydrateOutstandingWorksAsync`; the persisted `work-timeout` reminders resume ticking on their existing schedule. Existing in-flight work deadlines (based on each item's own `TakenAt`) are unaffected — exactly the invariant being restored.
5. **Rollback.** Revert the single helper change. `RegisterOrUpdateReminder` semantics return; the system degrades to the pre-fix behavior (the bug) with no data inconsistency, since no schema/state was touched.

## Open Questions

- None blocking implementation. (If the team later wants sub-minute detection latency, the reminder *period* — currently 1 min — is the knob, intentionally out of scope for this issue per Non-Goals.)
