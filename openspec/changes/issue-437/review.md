# Review — issue-437 (otel.db 自动轮转)

Review of the implemented change (commits `ce9f86d6a` T-001, `bf11cba2a` T-002,
`4bb93df5e` T-003) against the issue's acceptance criteria and the plan under
`openspec/changes/issue-437/`. Artifacts under `openspec/changes/issue-437/` are
this workflow's own products and are not judged here.

## Verification performed

- `dotnet build Mohist.sln -p:SkipWebBuild=true` → **0 warnings, 0 errors**
  (TreatWarningsAsErrors holds).
- Focused OTel specs (`OtelRetentionMaintenanceSpecs`,
  `OtelStorageMaintenanceSpecs`, `OtelStorageGuardSpecs`,
  `OtelStorageRecoveryMaintenanceSpecs`, `OtelDiagnosticsSamplerEnabledSpecs`)
  → **65/65 pass**.
- Read every changed source file and the four capability specs; traced the
  runtime interaction between the three maintenance callbacks and the guard.
- Confirmed the two untouched `FakeFileSystem` test helpers implement
  `Mohist.Cli.IFileSystem` (a different interface), so the
  `Mohist.Server.SystemInfo.IFileSystem` extension by `WriteAllText`/`Delete`
  does not break them.

## Acceptance-criteria coverage

The seven issue ACs are individually addressed by the implementation and have
spec/unit support: 72h bounded-batch complete-Trace deletion (T-001), db+WAL+SHM
high/low-watermark eviction with `incremental_vacuum` + `wal_checkpoint(TRUNCATE)`
and no full `VACUUM` (T-002), budget-aware ingest refusal flowing through the
existing `RecordIngest` → OTLP `partial_success` path with the
`storage_budget_exhausted` reason (T-002), persisted `.meta` marker with
conservative fallback (T-002), and the startup rebuild with `storage_data_reset`
(T-003). The findings below are about correctness/coverage gaps in how those
pieces interact, not missing capabilities.

## Findings

### F-1 (must fix) — A rebuilt store keeps admission closed for up to one maintenance tick

**Where.** `OtelStorageRecoveryMaintenance.Rebuild`
(`packages/server/src/Mohist.Server/Otel/OtelStorageRecoveryMaintenance.cs:128-158`)
never re-arbitrates the guard — it reads only `_guard.BudgetBytes`
(`OtelStorageRecoveryMaintenance.cs:100`, `:143`) and never calls
`_guard.Arbitrate(...)`. Combined with the callback registration order in
`MohistServiceRegistration.cs:190-192` (`OtelRetentionMaintenance` →
`OtelStorageMaintenance` → `OtelStorageRecoveryMaintenance`) and the sampler's
in-order invocation (`OtelDiagnosticsSampler.RunMaintenanceAsync`,
`OtelDiagnosticsSampler.cs:247-264`), recovery runs **last** on the first tick.

**What is wrong.** On the first post-start tick for an oversized pre-existing
database, `OtelStorageMaintenance` runs before recovery. That database was
created before this change, so `auto_vacuum = 0` and `incremental_vacuum` is a
no-op (acknowledged in `design.md` D2) — the file cannot shrink, so after
eviction the re-probe still reports usage above the high watermark and
`OtelStorageGuard.Arbitrate` closes admission and publishes
`storage_budget_exhausted` (`OtelStorageMaintenance.cs:157-183`). Recovery then
rebuilds an empty store, but because it does not re-arbitrate, admission stays
**closed** and `storage_budget_exhausted` stays **active** even though the store
is now empty and well under budget.

Until the next tick (≤10 s), when `OtelStorageMaintenance` re-probes the empty
store and finally calls `Arbitrate` below the low watermark to reopen admission,
`BudgetAwareIngestProtectionDecision.Decide`
(`BudgetAwareIngestProtectionDecision.cs:22-25`) refuses every Span. Consequences:

1. The first write(s) after a rebuild are refused with a non-retry OTLP
   `partial_success` and are lost.
2. `storage_data_reset` cannot clear, because it is cleared by a *committed*
   write (`RuntimeObservability.RecordIngest` → `ClearsStorageWrite`,
   `RuntimeObservability.cs:306-307`), and no write can commit while admission
   is closed. So right after a rebuild both `storage_budget_exhausted` and
   `storage_data_reset` are active for a store that is, in fact, empty.
3. The admission state contradicts reality (empty, fully reclaimable store) for
   up to one tick, contradicting the ingest-admission spec's rule that admission
   closes only when "reclamation cannot keep up."

**Why this is a defect, not a nit.** It is a behavioural correctness deviation
on a path the issue explicitly calls out (oversized-DB upgrade), it drops data on
a lossy-but-stated lifecycle, and it is **not covered by any test**: the three
callbacks are exercised only in isolation
(`OtelStorageMaintenanceSpecs`/`OtelStorageRecoveryMaintenanceSpecs` never run
together), and `OtelStorageRecoveryMaintenanceSpecs.ExecuteAsync_AboveBudget_*`
do not assert `guard.AdmissionClosed` after rebuild, so the suite cannot catch
it.

**Fix direction.** After a successful rebuild, re-arbitrate admission against
the fresh store (e.g. re-probe and `_guard.Arbitrate(usageBytes)` — the empty
store will be below the low watermark, so admission opens and
`storage_budget_exhausted` clears), or register `OtelStorageRecoveryMaintenance`
first so the rebuild happens before the storage callback runs on the first tick.
Either way, add an integration assertion that admission is open (and
`storage_budget_exhausted` is inactive) immediately after a rebuild.

### F-2 (must fix) — The "query overlaps rebuild" acceptance criterion has no genuine test

**Where.**
`OtelStorageRecoveryMaintenanceSpecs.ExecuteAsync_ReadOnlyQueryDuringRebuild_SurfacesAsStorageReadFailure`
(`packages/server/tests/Mohist.Server.SpecTests/Specs/Telemetry/OtelStorageRecoveryMaintenanceSpecs.cs:177-213`).

**What is wrong.** The test `await`s `recovery.ExecuteAsync(...)` to completion
**first**, then opens a read-only connection. There is no overlap with the
rebuild window. The core assertion is `readOutcome.Succeeded || readOutcome.Exception is not null`
(`:200-201`), which is a tautology — it can never fail. The test's own comment
admits "The recovery runs to completion on its own." It therefore does not
verify the T-003 acceptance criterion / spec scenario it is named after: "A
read-only observation query overlapping the bounded rebuild window fails and
surfaces through the existing storage-read degradation path without blocking or
corrupting the rebuild."

**Why it matters.** An acceptance criterion is recorded as covered that is not
actually exercised, which violates the project testing principle that tests must
genuinely verify behaviour rather than assert tautologies.

**Fix direction.** Drive an actual overlapping read — e.g. a fake
`IOtelDbPool`/`IFileSystem` that pauses recovery at the file-replacement step
while a read-only open is attempted on another task, asserting the read fails
with a bounded `SqliteException` and that recovery still completes — or, if the
concurrency is judged unverifiable under the no-real-filesystem constraint,
state that explicitly (as D7 does for `incremental_vacuum` efficacy) and replace
the tautological assertion with a targeted one.

## Non-blocking observations

- **F-3 — Mislabeled "re-enable" test.**
  `OtelDiagnosticsSamplerEnabledSpecs.ObservationDisabled_AndReEnabledThenAdvanced_MaintenanceRunsAgainstInjectedTime`
  (`OtelDiagnosticsSamplerEnabledSpecs.cs:71-90`) is named and documented as a
  re-enable scenario but never re-enables observation
  (`OtelDiagnosticsSampler._enabled` is a readonly field set at construction). It
  only re-asserts the disabled case already covered by
  `ObservationDisabled_MaintenanceCallbackNeverInvoked`. The intended behaviour
  is satisfied via process restart (a new sampler constructed with `enabled:true`
  resumes from the injected time / persisted marker), so this is a clarity fix,
  not a behaviour gap.

- **F-4 — Design divergence that enabled F-1.** `design.md` D1 specified a
  *single* maintenance callback running time → size → reclaim → arbitrate in
  order, with rebuild-first on the first tick. The implementation splits the work
  into three separately registered callbacks whose order (retention → storage →
  recovery) performs wasted eviction on a doomed oversized database and is the
  root cause of F-1 (recovery runs last and does not re-arbitrate). The split
  itself is acceptable provided the design's ordering invariants (rebuild-first;
  admission arbitrated after rebuild) are preserved — they are not today.

## Verdict

The change is well-structured, builds cleanly under TreatWarningsAsErrors, and
the isolated unit/spec coverage is strong. However, F-1 is a real behavioural
correctness defect on a stated acceptance-criteria path (admission stays closed
and `storage_budget_exhausted` stays published for an empty, rebuilt store for up
to one tick, dropping the first post-rebuild write and delaying the
`storage_data_reset` clear), and it is not covered by any integration test. F-2
records an acceptance criterion as covered by a tautological test that does not
exercise it. Both must be fixed before merge.

<promise>FAIL</promise>
