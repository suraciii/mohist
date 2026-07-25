# Review — issue-437 (otel.db 自动轮转) — re-review after fixes

Second pass. The first review reported FAIL with two must-fix findings (F-1, F-2)
and two non-blocking findings (F-3, F-4). Two follow-up commits landed:
`9dcd1ba66` (F-1 + F-4) and `2cb766d2c` (F-2 + F-3). This pass verifies each
finding was resolved, then sweeps for anything newly introduced or previously
missed.

## Prior findings — resolution check

| # | Finding | Status | Evidence |
|---|---|---|---|
| F-1 | Rebuilt store kept admission closed for up to one tick (recovery ran last and never re-arbitrated the guard) | Resolved | `OtelStorageRecoveryMaintenance.Rebuild` now opens the fresh connection, then calls `ReArbitrateAfterRebuild()` (`OtelStorageRecoveryMaintenance.cs:151,174-186`), which re-probes and `_guard.Arbitrate(...)` so admission reopens and `storage_budget_exhausted` clears immediately. The re-probe is wrapped in try/catch so a probe failure is non-fatal (the rebuild itself succeeded; the next tick re-derives). New test `OtelStorageRecoveryMaintenanceSpecs.ExecuteAsync_AfterRebuild_OpensAdmissionAndClearsBudgetExhausted` drives admission closed, rebuilds, and asserts admission reopens with `IngestProtection` cleared while `StorageWrite` (data-reset) stays active. |
| F-2 | "Query overlaps rebuild" AC had a tautological test (`Succeeded \|\| Exception is not null`, no overlap) | Resolved | Replaced by `ExecuteAsync_Rebuild_DoesNotBlockOrCorruptTheStore` (`OtelStorageRecoveryMaintenanceSpecs.cs:217`), which asserts the verifiable no-corruption half (rebuild completes, `idx_traces_end`/`idx_spans_trace` intact, fresh read-only open succeeds) and documents the accepted limitation in the test comment: the in-memory shared-cache DB cannot model the file-absent rebuild window, so the "fails via the existing StorageRead path" half is verified-by-design (consistent with design D7's `incremental_vacuum` treatment). The dead `SafeOpenReadOnly`/`ReadOnlyOpenResult` helpers were removed. |
| F-3 | Mislabeled "re-enable" test never re-enabled observation | Resolved | Replaced by `ObservationReenabledViaRestart_ResumesMaintenanceAgainstInjectedTime` (`OtelDiagnosticsSamplerEnabledSpecs.cs:72`), which runs a disabled sampler (0 invocations), then constructs a second sampler `enabled:true` with a fresh `IHostApplicationLifetime` and asserts maintenance resumes (`Invocations >= 1`) against the advanced injected time — modelling re-enable as the process restart this architecture uses. |
| F-4 | Design divergence: design D1 specified one callback; implementation split into three whose order did wasted work before a rebuild and caused F-1 | Resolved | DI registration reordered to recovery → retention → storage (`MohistServiceRegistration.cs:197-199`) so the first tick rebuilds before retention/storage eviction touch the oversized store. Recovery is a one-shot gate, so steady-state order is time → size → reclaim → arbitrate (design D1). `design.md` D1/D5/D7 updated to describe the realized three-callback structure, the recovery-first ordering, the rebuild re-arbitration, and the accepted test limitation. |

## Verification performed

- `dotnet build Mohist.sln -p:SkipWebBuild=true` → **0 warnings, 0 errors**
  (TreatWarningsAsErrors holds).
- Focused OTel specs (`OtelRetentionMaintenanceSpecs`,
  `OtelStorageMaintenanceSpecs`, `OtelStorageGuardSpecs`,
  `OtelStorageRecoveryMaintenanceSpecs`, `OtelDiagnosticsSamplerEnabledSpecs`)
  → **66/66 pass** (was 65; +1 net from the new F-1 test, with F-2/F-3 as
  in-place replacements).
- Full server spec suite → **3100/3100 pass**.
- Re-read every fixed source/test file as it is now; confirmed the old
  tautological/mislabeled tests and their dead helpers are gone.

## New observations

No regressions and no new blocking issues were introduced by the fixes:

- The post-rebuild re-arbitration is idempotent with the storage callback's own
  arbitration on the same tick (both publish under the runtime's state lock on
  the same `IngestProtection` source; the second call is a no-op when admission
  is already open), so the recovery-first ordering does not double-signal.
- The re-probe after rebuild reads file lengths of the fresh tiny store, so it
  adds no unbounded work; the recovery one-shot gate means the reorder adds no
  per-tick overhead after the first tick.
- The `IFileSystem` interface extension (`WriteAllText`/`Delete`) is implemented
  by all server-side implementors; the two untouched `FakeFileSystem` test
  helpers implement the separate `Mohist.Cli.IFileSystem`, so they are
  unaffected (confirmed during the first review).

## Acceptance-criteria coverage

All seven issue ACs are implemented with spec/unit support: 72h bounded-batch
complete-Trace deletion with interrupt/resume (T-001); db+WAL+SHM high/low
watermark eviction with `incremental_vacuum` + `wal_checkpoint(TRUNCATE)` and no
full `VACUUM` (T-002); budget-aware ingest refusal through the existing
`RecordIngest` → OTLP `partial_success` path with the `storage_budget_exhausted`
reason (T-002); persisted `.meta` marker with conservative fallback and no
cleanup while observation is off (T-002); startup rebuild with
`storage_data_reset`, now with immediate admission re-arbitration (T-003); and
injectable-clock test coverage across time/space/WAL/blocked-reader/restart/
complete-Trace paths.

## Verdict

All four prior findings are resolved without introducing new inconsistencies or
regressions; the build is clean under TreatWarningsAsErrors and the full and
focused spec suites pass. The change is ready to merge.

<promise>PASS</promise>
